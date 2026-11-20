<#
Builds hosts.txt next to this script: "<private ip> <fqdn>" for every private endpoint,
App Service private endpoint and Postgres flexible server that is reachable through
the VPN ranges given. Uses your signed-in Azure CLI (read-only Resource Graph queries).
Apply afterwards with: p2s hosts apply
#>
param(
    # Your Entra tenant id (the GUID in <tenant> of azurevpnconfig.xml).
    [Parameter(Mandatory)] [string] $Tenant,
    # Ranges routed by your VPN profiles, in preference order (first match wins
    # when a name has endpoints in several networks), e.g. '10.40.0.0/16','172.16.0.0/16'.
    [Parameter(Mandatory)] [string[]] $Ranges,
    # Never map into these, e.g. your home LAN '192.168.1.0/24'.
    [string[]] $Exclude = @(),
    [string]   $Out = (Join-Path $PSScriptRoot 'hosts.txt')
)
$ErrorActionPreference = 'Stop'
$env:AZURE_CORE_ONLY_SHOW_ERRORS = 'true'
$env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = 'no'

function Test-InRange([string]$ip, [string]$cidr) {
    $net, $bits = $cidr.Split('/')
    $a = [BitConverter]::ToUInt32(([Net.IPAddress]::Parse($ip).GetAddressBytes()[3..0]), 0)
    $n = [BitConverter]::ToUInt32(([Net.IPAddress]::Parse($net).GetAddressBytes()[3..0]), 0)
    $all = [uint64]4294967295
    $mask = if ([int]$bits -eq 0) { [uint32]0 } else { [uint32](($all -shl (32 - [int]$bits)) -band $all) }
    return (($a -band $mask) -eq ($n -band $mask))
}
function Get-Rank([string]$ip) {
    foreach ($x in $Exclude) { if (Test-InRange $ip $x) { return -1 } }
    for ($i = 0; $i -lt $Ranges.Count; $i++) { if (Test-InRange $ip $Ranges[$i]) { return $i } }
    return -1
}

$subs = @(az account list --query "[?tenantId=='$Tenant'].id" -o tsv)
function Invoke-Graph([string]$Kql) {
    $a = @('graph', 'query', '-q', $Kql, '--first', '1000', '-o', 'json', '--subscriptions') + $subs
    ((& az @a 2>$null) | Out-String | ConvertFrom-Json).data
}

$cands = New-Object Collections.Generic.List[object]
function Add-Cand([string]$fqdn, [string]$ip, [string]$what) {
    if (-not $fqdn -or -not $ip) { return }
    $r = Get-Rank $ip
    if ($r -ge 0) { $cands.Add([pscustomobject]@{ fqdn = $fqdn.ToLowerInvariant(); ip = $ip; rank = $r; what = $what }) }
}

# 1. Private endpoints: custom DNS configs and the NIC's link FQDNs.
$pes = Invoke-Graph "resources | where type =~ 'microsoft.network/privateendpoints' | mv-expand cfg = properties.customDnsConfigs | project target=tostring(split(properties.privateLinkServiceConnections[0].properties.privateLinkServiceId,'/')[8]), fqdn=tostring(cfg.fqdn), ip=tostring(cfg.ipAddresses[0])"
foreach ($p in $pes) { Add-Cand $p.fqdn $p.ip $p.target }
$nics = Invoke-Graph "resources | where type =~ 'microsoft.network/networkinterfaces' and isnotempty(properties.privateEndpoint.id) | mv-expand ipc = properties.ipConfigurations | mv-expand f = ipc.properties.privateLinkConnectionProperties.fqdns | project nic=name, ip=tostring(ipc.properties.privateIPAddress), fqdn=tostring(f)"
foreach ($n in $nics) { Add-Cand $n.fqdn $n.ip $n.nic }

# 2. Custom domains of App Services, mapped to the app's private endpoint IP.
$sites = Invoke-Graph "resources | where type =~ 'microsoft.web/sites' | mv-expand h = properties.hostNames | project app=name, host=tostring(h), defaultHost=tostring(properties.defaultHostName)"
$byFqdn = @{}; foreach ($c in $cands) { if (-not $byFqdn.ContainsKey($c.fqdn) -or $c.rank -lt $byFqdn[$c.fqdn].rank) { $byFqdn[$c.fqdn] = $c } }
foreach ($s in $sites) {
    $d = $s.defaultHost.ToLowerInvariant()
    if ($s.host -and $byFqdn.ContainsKey($d) -and $s.host.ToLowerInvariant() -ne $d) { Add-Cand $s.host $byFqdn[$d].ip "$($s.app) (custom domain)" }
}

# 3. Postgres flexible servers with private access: IP from the private DNS zone.
$pg = Invoke-Graph "resources | where type =~ 'microsoft.dbforpostgresql/flexibleservers' and tostring(properties.network.publicNetworkAccess) =~ 'Disabled' | project name, fqdn=tostring(properties.fullyQualifiedDomainName), zone=tostring(properties.network.privateDnsZoneArmResourceId)"
foreach ($s in $pg) {
    if (-not $s.zone) { continue }
    $parts = $s.zone.Split('/')
    $sub = $parts[2]; $rg = $parts[4]; $zone = $parts[8]
    $recs = az network private-dns record-set a list --subscription $sub -g $rg -z $zone --query "[].{n:name, ip:aRecords[0].ipv4Address}" -o json 2>$null | Out-String | ConvertFrom-Json
    # The server's own record is the hex-named one (VM records are also in shared zones).
    $own = $recs | Where-Object { $_.n -match '^[0-9a-f]{12}$' } | Select-Object -First 1
    if ($own) { Add-Cand $s.fqdn $own.ip "postgres $($s.name)" }
}

# Pick one IP per name, by range preference.
$final = $cands | Group-Object fqdn | ForEach-Object { $_.Group | Sort-Object rank | Select-Object -First 1 } | Sort-Object { [version]$_.ip }, fqdn
$lines = @("# Generated $(Get-Date -Format s) by New-P2SHostsList.ps1 from Azure Resource Graph",
           "# Ranges (preference order): $($Ranges -join ', ')   excluded: $($Exclude -join ', ')")
$lines += $final | ForEach-Object { "{0,-15} {1,-60} # {2}" -f $_.ip, $_.fqdn, $_.what }
$lines | Set-Content -Path $Out -Encoding ASCII
Write-Host "$(@($final).Count) names written to $Out"
