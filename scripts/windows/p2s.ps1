<#
p2s - Azure Point-to-Site VPN with Microsoft Entra ID, several profiles at once.

Native Windows port of the OpenP2S approach:
  * Entra sign-in in your browser (auth code + PKCE, loopback redirect), using the
    application id and audience from the Azure profile, MFA / Conditional Access as usual.
  * A patched OpenVPN 2.7.8 (USER_PASS_LEN 8192, TLS_CHANNEL_BUF_SIZE 16384) gets
    username "AzureAD" and the access token as password over the management
    interface, multi-line base64 form. The token is never written to disk.
  * Each profile runs its own openvpn.exe on its own ovpn-dco adapter "P2S-<name>",
    so any number can be connected at the same time.

Commands
  p2s import <azurevpnconfig.xml> [name]   add a profile (name defaults from the file name)
  p2s list                                  profiles and whether they are connected
  p2s login <name>                          sign in (browser) and cache the session
  p2s up <name>                             connect; opens an elevated window that stays open
  p2s down <name> | all                     disconnect
  p2s status                                adapters, addresses and routes of live tunnels
  p2s dns [show] | apply <name> | clear     DNS routing (NRPT) of private domains to the VPN's DNS
  p2s hosts apply | remove | show           fallback: private-endpoint names in the hosts file
  p2s logout <name>                         forget the cached sign-in for that profile
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Command = 'help',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)] [string[]] $Rest
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Security

# ---------------------------------------------------------------- paths ----
$Root      = $PSScriptRoot
$Self      = $PSCommandPath
$OpenVpn   = Join-Path $Root 'bin\openvpn.exe'
$Profiles  = Join-Path $Root 'profiles'
$CacheDir  = Join-Path $Root 'cache'
$RunDir    = Join-Path $Root 'run'
$LogDir    = Join-Path $Root 'logs'
$HostsList = Join-Path $Root 'hosts.txt'
$TapCtl    = Join-Path $env:ProgramFiles 'OpenVPN\bin\tapctl.exe'
$HostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
foreach ($d in @($Profiles, $CacheDir, $RunDir, $LogDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}

# Roots Azure P2S gateways chain to (pinned by SHA-256, same as OpenP2S).
$AzureRootSha256 = @(
    'cb3ccbb76031e5e0138f8dd39a23f9de47ffc35e43c1144cea27d46a5ab1cb5f', # DigiCert Global Root G2
    '4348a0e9444c78cb265e058d5e8944b4d84f9662bd26db257f8934a443c70161'  # DigiCert Global Root CA
)

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Warn([string]$m) { Write-Host "warning: $m" -ForegroundColor Yellow }
function Fail([string]$m)        { throw "p2s: $m" }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ValidName([string]$n) {
    if ($n -notmatch '^[a-z0-9][a-z0-9-]{0,24}$') { Fail "invalid profile name '$n' (use a-z, 0-9, -; max 25 chars)" }
}

# ------------------------------------------------------------- profiles ----
function Select-XmlText($xml, [string]$xpath) {
    $n = $xml.SelectSingleNode($xpath)
    if ($null -eq $n) { return $null }
    $t = $n.InnerText.Trim()
    if ($t.Length -eq 0) { return $null }
    return $t
}
function LN([string]$name) { return "*[local-name()='$name']" }

function Get-CertificateName([string]$gatewayHost) {
    $h = $gatewayHost.ToLowerInvariant()
    $guid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
    if ($h -match "^azuregateway-($guid)-[0-9a-z]+\.((?:ln\.)?vpn\.azure\.com)$") { return "$($Matches[1]).$($Matches[2])" }
    if ($h -match '^[a-z0-9-]+\.vpn\.azure\.com$') { return '*.vpn.azure.com' }
    return $null
}

function Get-AzureRootsPem {
    $sha = [Security.Cryptography.SHA256]::Create()
    $found = @{}
    foreach ($c in (Get-ChildItem Cert:\LocalMachine\Root)) {
        $h = ([BitConverter]::ToString($sha.ComputeHash($c.RawData))).Replace('-', '').ToLowerInvariant()
        if ($AzureRootSha256 -contains $h -and -not $found.ContainsKey($h)) {
            $b64 = [Convert]::ToBase64String($c.RawData, 'InsertLineBreaks')
            $found[$h] = "-----BEGIN CERTIFICATE-----`n$($b64 -replace "`r", '')`n-----END CERTIFICATE-----"
        }
    }
    foreach ($h in $AzureRootSha256) {
        if (-not $found.ContainsKey($h)) { Fail "Azure gateway root $h is missing from the Windows root store" }
    }
    return ($AzureRootSha256 | ForEach-Object { $found[$_] }) -join "`n"
}

function Import-Profile([string]$xmlPath, [string]$name) {
    if (-not (Test-Path $xmlPath)) { Fail "profile not found: $xmlPath" }
    if (-not $name) {
        $name = ([IO.Path]::GetFileNameWithoutExtension($xmlPath)).ToLowerInvariant()
        $name = $name -replace 'azurevpnconfig', '' -replace '_aad', '' -replace '[^a-z0-9]+', '-'
        $name = $name.Trim('-')
        if (-not $name) { $name = 'default' }
    }
    Test-ValidName $name

    $xml = New-Object Xml.XmlDocument
    $xml.XmlResolver = $null
    $xml.Load((Resolve-Path $xmlPath).Path)

    $authType = Select-XmlText $xml "//$(LN 'clientauth')/$(LN 'type')"
    if ($authType -and $authType -ne 'aad') { Fail "profile uses '$authType' authentication; only Entra ID (aad) is supported" }

    $aad      = "//$(LN 'clientauth')//$(LN 'aad')"
    $tenantU  = Select-XmlText $xml "$aad/$(LN 'tenant')"
    $audience = Select-XmlText $xml "$aad/$(LN 'audience')"
    $appId    = Select-XmlText $xml "$aad/$(LN 'applicationid')"
    $secret   = Select-XmlText $xml "//$(LN 'servervalidation')/$(LN 'serversecret')"
    $vpnName  = Select-XmlText $xml "/*/$(LN 'name')"
    $gateways = @($xml.SelectNodes("//$(LN 'serverlist')//$(LN 'fqdn')") | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ })
    $transport = Select-XmlText $xml "//$(LN 'transportprotocol')"

    if (-not $tenantU)  { Fail 'profile has no <tenant>' }
    if (-not $audience) { Fail 'profile has no <audience>' }
    if (-not $secret)   { Fail 'profile has no <serversecret>' }
    if ($gateways.Count -eq 0) { Fail 'profile has no gateway <fqdn>' }
    if ($transport -and $transport -ne 'tcp') { Fail "profile requests '$transport' transport; Entra P2S needs tcp" }
    if ($secret -notmatch '^[0-9a-fA-F]{512}$') { Fail '<serversecret> is not 512 hex characters' }

    $tu = [Uri]$tenantU
    if ($tu.Host -ne 'login.microsoftonline.com') { Fail "unsupported authority host $($tu.Host)" }
    $tenant = $tu.AbsolutePath.Trim('/')
    if ($tenant -notmatch '^[0-9a-fA-F-]{36}$') { Fail "cannot read the tenant id from $tenantU" }
    if ($audience -notmatch '^[0-9a-fA-F-]{36}$') { Fail "audience '$audience' is not an application id" }
    $clientId = $audience
    if ($appId) { $clientId = $appId }

    foreach ($g in $gateways) { if ($g -notmatch '^[A-Za-z0-9.-]+$') { Fail "suspicious gateway name '$g'" } }
    $certNames = @($gateways | ForEach-Object { Get-CertificateName $_ } | Sort-Object -Unique)
    if ($certNames.Count -ne 1 -or -not $certNames[0]) { Fail "cannot work out the gateway certificate name for $($gateways -join ', ')" }

    $keyLines = for ($i = 0; $i -lt 512; $i += 32) { $secret.ToLowerInvariant().Substring($i, 32) }

    $cfg = @(
        '# Generated by p2s from an Azure VPN Client profile. Contains the tls-auth key.',
        'client', 'dev tun', 'proto tcp-client'
    ) + ($gateways | ForEach-Object { "remote $_ 443" }) + @(
        'nobind', 'resolv-retry infinite',
        'remote-cert-tls server',
        "verify-x509-name $($certNames[0]) name",
        'cipher AES-256-GCM', 'data-ciphers AES-256-GCM', 'data-ciphers-fallback AES-256-GCM', 'auth SHA256',
        'reneg-sec 0', 'auth-nocache',
        'auth-user-pass',
        'management-client', 'management-query-passwords', 'management-hold',
        'verb 3',
        '<ca>', (Get-AzureRootsPem), '</ca>',
        'key-direction 1',
        '<tls-auth>', '-----BEGIN OpenVPN Static key V1-----'
    ) + $keyLines + @('-----END OpenVPN Static key V1-----', '</tls-auth>', '')

    $dir = Join-Path $Profiles $name
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    [IO.File]::WriteAllText((Join-Path $dir 'openvpn.ovpn'), ($cfg -join "`n"), (New-Object Text.UTF8Encoding $false))
    $meta = [ordered]@{
        name = $name; vpnName = $vpnName; gateways = $gateways; certificateName = $certNames[0]
        tenant = $tenant; clientId = $clientId; scope = "$audience/.default"; adapter = "P2S-$name"
        source = (Resolve-Path $xmlPath).Path; imported = (Get-Date).ToString('s')
    }
    ($meta | ConvertTo-Json) | Set-Content -Path (Join-Path $dir 'profile.json') -Encoding UTF8
    Write-Step "imported '$name' ($vpnName) -> adapter P2S-$name, gateway $($gateways[0])"
}

function Get-P2SProfile([string]$name) {
    Test-ValidName $name
    $f = Join-Path $Profiles "$name\profile.json"
    if (-not (Test-Path $f)) { Fail "no profile '$name' (p2s import <xml> $name)" }
    $p = Get-Content $f -Raw | ConvertFrom-Json
    $p | Add-Member -NotePropertyName config -NotePropertyValue (Join-Path $Profiles "$name\openvpn.ovpn")
    return $p
}

# ---------------------------------------------------------- Entra sign-in ----
function ConvertTo-Base64Url([byte[]]$b) { return [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function Get-CacheFile($p) { return Join-Path $CacheDir ("{0}_{1}.dat" -f $p.tenant, $p.clientId) }

function Save-RefreshToken($p, [string]$rt, [string]$account) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{ refresh_token = $rt; account = $account } | ConvertTo-Json -Compress))
    $prot = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'CurrentUser')
    [IO.File]::WriteAllBytes((Get-CacheFile $p), $prot)
}

function Read-RefreshToken($p) {
    $f = Get-CacheFile $p
    if (-not (Test-Path $f)) { return $null }
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($f), $null, 'CurrentUser')
        return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json)
    } catch { return $null }
}

function Get-JwtClaims([string]$jwt) {
    $parts = $jwt.Split('.')
    if ($parts.Count -lt 2) { return $null }
    $s = $parts[1].Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }
    return ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) | ConvertFrom-Json)
}

function Invoke-TokenRequest($p, [hashtable]$body) {
    $body['client_id'] = $p.clientId
    $body['scope'] = "$($p.scope) offline_access openid profile"
    $uri = "https://login.microsoftonline.com/$($p.tenant)/oauth2/v2.0/token"
    try {
        return Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/x-www-form-urlencoded'
    } catch {
        $msg = $null
        $detail = $_.ErrorDetails.Message
        if ($detail) {
            try { $e = $detail | ConvertFrom-Json; $msg = "Entra: $($e.error): $(([string]$e.error_description -split "`r?`n")[0])" } catch { $msg = $null }
        }
        if ($msg) { throw $msg }
        throw
    }
}

function Complete-TokenResult($p, $r) {
    if (-not $r.access_token) { Fail 'Entra returned no access token' }
    $claims = Get-JwtClaims $r.access_token
    $account = $null
    if ($claims) { $account = $claims.upn; if (-not $account) { $account = $claims.unique_name } }
    if ($r.refresh_token) { Save-RefreshToken $p $r.refresh_token $account }
    if ($r.access_token.Length -gt 8000) { Fail "access token is $($r.access_token.Length) bytes, over this build's 8192-byte credential limit" }
    return $r.access_token
}

function Read-HttpRequestLine($client) {
    $client.ReceiveTimeout = 10000
    $s = $client.GetStream()
    $buf = New-Object byte[] 8192
    $sb = New-Object Text.StringBuilder
    while ($sb.ToString().IndexOf("`r`n`r`n") -lt 0) {
        $n = $s.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        [void]$sb.Append([Text.Encoding]::ASCII.GetString($buf, 0, $n))
        if ($sb.Length -gt 65536) { break }
    }
    return ($sb.ToString() -split "`r`n")[0]
}

function Send-HttpReply($client, [string]$title, [string]$text) {
    $html = "<!doctype html><meta charset=utf-8><title>$title</title><body style='font-family:Segoe UI,sans-serif;margin:3em'><h2>$title</h2><p>$text</p></body>"
    $bytes = [Text.Encoding]::UTF8.GetBytes($html)
    $hdr = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
    $s = $client.GetStream(); $s.Write($hdr, 0, $hdr.Length); $s.Write($bytes, 0, $bytes.Length); $s.Flush()
    $client.Close()
}

function Invoke-InteractiveLogin($p) {
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $vb = New-Object byte[] 48; $rng.GetBytes($vb); $verifier = ConvertTo-Base64Url $vb
    $sb = New-Object byte[] 24; $rng.GetBytes($sb); $state = ConvertTo-Base64Url $sb
    $challenge = ConvertTo-Base64Url ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))

    $l4 = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
    $l4.Start()
    $port = $l4.LocalEndpoint.Port
    $l6 = $null
    try { $l6 = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::IPv6Loopback), $port; $l6.Start() } catch { $l6 = $null }
    $redirect = "http://localhost:$port"

    $q = @{
        client_id = $p.clientId; response_type = 'code'; redirect_uri = $redirect; response_mode = 'query'
        scope = "$($p.scope) offline_access openid profile"; state = $state
        code_challenge = $challenge; code_challenge_method = 'S256'; prompt = 'select_account'
    }
    $qs = ($q.GetEnumerator() | ForEach-Object { "$($_.Key)=$([Uri]::EscapeDataString([string]$_.Value))" }) -join '&'
    $url = "https://login.microsoftonline.com/$($p.tenant)/oauth2/v2.0/authorize?$qs"

    Write-Step 'opening the Microsoft sign-in page in your browser...'
    Start-Process $url
    $deadline = (Get-Date).AddMinutes(5)
    $code = $null
    try {
        while (-not $code) {
            if ((Get-Date) -gt $deadline) { Fail 'timed out waiting for the browser sign-in' }
            $client = $null
            if ($l4.Pending()) { $client = $l4.AcceptTcpClient() }
            elseif ($l6 -and $l6.Pending()) { $client = $l6.AcceptTcpClient() }
            else { Start-Sleep -Milliseconds 200; continue }

            $line = Read-HttpRequestLine $client
            if ($line -notmatch '^GET (\S+) HTTP') { $client.Close(); continue }
            $u = [Uri]("http://localhost" + $Matches[1])
            $params = @{}
            foreach ($kv in $u.Query.TrimStart('?').Split('&')) {
                if (-not $kv) { continue }
                $k, $v = $kv.Split('=', 2)
                $params[[Uri]::UnescapeDataString($k)] = [Uri]::UnescapeDataString(([string]$v).Replace('+', ' '))
            }
            if ($params.ContainsKey('error')) {
                Send-HttpReply $client 'Sign-in failed' 'You can close this tab and check the terminal.'
                Fail "Entra: $($params['error']): $(($params['error_description'] -split "`r?`n")[0])"
            }
            if (-not $params.ContainsKey('code')) { Send-HttpReply $client 'p2s' 'Waiting for sign-in...'; continue }
            if ($params['state'] -ne $state) { Send-HttpReply $client 'Sign-in failed' 'State mismatch.'; Fail 'sign-in response state did not match; aborted' }
            $code = $params['code']
            Send-HttpReply $client 'Signed in' 'p2s has your sign-in. You can close this tab.'
        }
    } finally {
        $l4.Stop(); if ($l6) { $l6.Stop() }
    }
    $r = Invoke-TokenRequest $p @{ grant_type = 'authorization_code'; code = $code; redirect_uri = $redirect; code_verifier = $verifier }
    return Complete-TokenResult $p $r
}

function Get-AccessToken($p, [switch]$Interactive, [switch]$ForceLogin) {
    if (-not $ForceLogin) {
        $c = Read-RefreshToken $p
        if ($c -and $c.refresh_token) {
            try {
                $r = Invoke-TokenRequest $p @{ grant_type = 'refresh_token'; refresh_token = $c.refresh_token }
                return Complete-TokenResult $p $r
            } catch {
                if (-not $Interactive) { throw "cached sign-in could not be refreshed ($($_.Exception.Message)); run: p2s login $($p.name)" }
                Write-Warn "cached sign-in expired: $($_.Exception.Message)"
            }
        } elseif (-not $Interactive) {
            Fail "not signed in; run: p2s login $($p.name)"
        }
    }
    return Invoke-InteractiveLogin $p
}

# ------------------------------------------------------------- adapters ----
# Azure P2S gateways do not negotiate the DATA_V2 packet format that ovpn-dco
# needs ("pushed options are incompatible with data channel offload"), so each
# profile gets its own TAP-Windows6 adapter and OpenVPN runs with --disable-dco.
function Confirm-Adapter($p) {
    if (-not (Test-Path $TapCtl)) { Fail "tapctl.exe not found at $TapCtl; install OpenVPN 2.7 for Windows (drivers) first" }
    $a = Get-NetAdapter -Name $p.adapter -ErrorAction SilentlyContinue
    if ($a -and $a.InterfaceDescription -like '*TAP-Windows*') { return }
    if ($a) {
        Write-Step "replacing '$($p.adapter)' ($($a.InterfaceDescription)) with a TAP-Windows6 adapter"
        & $TapCtl delete $p.adapter | Out-Host
    } else {
        Write-Step "creating network adapter '$($p.adapter)' (TAP-Windows6)"
    }
    & $TapCtl create --name $p.adapter --hwid 'root\tap0901' | Out-Host
    $a = Get-NetAdapter -Name $p.adapter -ErrorAction SilentlyContinue
    if (-not $a -or $a.InterfaceDescription -notlike '*TAP-Windows*') { Fail "could not create TAP adapter $($p.adapter)" }
}

# ------------------------------------------------------------------- DNS ----
# Windows asks every adapter's DNS server in parallel and takes the first
# answer, so a VPN-pushed DNS server loses to the home router. NRPT rules send
# chosen domains to the VPN's resolver only - what the Azure VPN Client does.
$DefaultNamespaces = @(
    # Azure services commonly behind private endpoints; add your own domains in dns-namespaces.txt.
    'azurewebsites.net', 'azurestaticapps.net', 'mysql.database.azure.com',
    'postgres.database.azure.com', 'database.windows.net', 'documents.azure.com',
    'vault.azure.net', 'azurecr.io', 'eventgrid.azure.net', 'servicebus.windows.net', 'redis.cache.windows.net',
    'blob.core.windows.net', 'file.core.windows.net', 'queue.core.windows.net', 'table.core.windows.net'
)

function Get-DnsNamespaces {
    $f = Join-Path $Root 'dns-namespaces.txt'
    if (-not (Test-Path $f)) {
        @('# Domains sent to the VPN-pushed DNS server while a profile with one is connected.',
          '# One per line; edit freely. Takes effect on the next "p2s up".') + $DefaultNamespaces |
            Set-Content -Path $f -Encoding ASCII
    }
    return @(Get-Content $f | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } |
             ForEach-Object { '.' + $_.TrimStart('.') } | Sort-Object -Unique)
}

function Get-PushedDns($p, [string]$log) {
    $servers = @()
    if (Test-Path $log) {
        $push = Select-String -Path $log -Pattern 'PUSH_REPLY' | Select-Object -Last 1
        if ($push) { $servers = @([regex]::Matches($push.Line, 'dhcp-option DNS ([0-9.]+)') | ForEach-Object { $_.Groups[1].Value }) }
    }
    if ($servers.Count -eq 0) {
        $servers = @((Get-DnsClientServerAddress -InterfaceAlias $p.adapter -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
    }
    return @($servers | Where-Object { $_ })
}

function Remove-P2SDnsRules([string]$name) {
    Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq "p2s:$name" } |
        ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force -ErrorAction SilentlyContinue }
    Clear-DnsClientCache -ErrorAction SilentlyContinue
}

function Set-P2SDnsRules($p, [string]$log) {
    $servers = Get-PushedDns $p $log
    if ($servers.Count -eq 0) { Write-Host '    DNS: this gateway pushes no DNS server'; return }
    Remove-P2SDnsRules $p.name
    $owned = @{}
    Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -like 'p2s:*' } |
        ForEach-Object { foreach ($ns in $_.Namespace) { $owned[$ns] = $_.Comment } }
    $added = 0
    foreach ($ns in (Get-DnsNamespaces)) {
        if ($owned.ContainsKey($ns)) { continue }   # another connected profile already serves it
        Add-DnsClientNrptRule -Namespace $ns -NameServers $servers -Comment "p2s:$($p.name)" | Out-Null
        $added++
    }
    Clear-DnsClientCache
    Write-Host "    DNS: $added domains -> $($servers -join ', ') (NRPT; edit dns-namespaces.txt to change)"
}

# ------------------------------------------------------------ management ----
function Send-Mgmt($stream, [string]$line) {
    $b = [Text.Encoding]::ASCII.GetBytes("$line`n")
    $stream.Write($b, 0, $b.Length); $stream.Flush()
}

function Send-Credentials($stream, [string]$token) {
    Send-Mgmt $stream 'username "Auth" "AzureAD"'
    Send-Mgmt $stream 'password "Auth"'
    # 256-char lines: OpenVPN reads management input 256 bytes at a time into a
    # 1024-byte line buffer and silently clears it on overflow (manage.c,
    # command_line_add). With 768-char lines an unfinished line plus the next
    # read can overflow it and corrupt the token.
    $enc = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($token))
    for ($i = 0; $i -lt $enc.Length; $i += 256) { Send-Mgmt $stream $enc.Substring($i, [Math]::Min(256, $enc.Length - $i)) }
    Send-Mgmt $stream 'END'
}

function Get-RunFile([string]$name)  { return Join-Path $RunDir "$name.json" }
function Get-StopFile([string]$name) { return Join-Path $RunDir "$name.stop" }

function Get-LiveRun([string]$name) {
    $f = Get-RunFile $name
    if (-not (Test-Path $f)) { return $null }
    $r = Get-Content $f -Raw | ConvertFrom-Json
    $proc = Get-Process -Id $r.pid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq 'openvpn') { return $r }
    Remove-Item $f -ErrorAction SilentlyContinue
    return $null
}

function Start-Tunnel([string]$name) {
    if (-not (Test-Admin)) { Fail 'connecting needs an elevated PowerShell (use: p2s up <name>)' }
    $p = Get-P2SProfile $name
    if (Get-LiveRun $name) { Fail "'$name' is already connected (p2s down $name)" }
    if (-not (Test-Path $OpenVpn)) { Fail "patched openvpn.exe not found at $OpenVpn" }
    $Host.UI.RawUI.WindowTitle = "p2s: $name"

    Confirm-Adapter $p
    Write-Step "getting an Entra token for $($p.vpnName)"
    $token = Get-AccessToken $p

    $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
    $listener.Start()
    $mport = $listener.LocalEndpoint.Port
    $log = Join-Path $LogDir "$name.log"
    Remove-Item (Get-StopFile $name) -ErrorAction SilentlyContinue

    $argList = @('--config', "`"$($p.config)`"", '--dev-node', "`"$($p.adapter)`"", '--disable-dco',
                 '--management', '127.0.0.1', "$mport", '--log', "`"$log`"")
    Write-Step "starting OpenVPN on adapter $($p.adapter)"
    $proc = Start-Process -FilePath $OpenVpn -ArgumentList $argList -WindowStyle Hidden -PassThru
    $null = $proc.Handle   # keep a handle so ExitCode is readable after exit
    @{ pid = $proc.Id; adapter = $p.adapter; started = (Get-Date).ToString('s'); log = $log } |
        ConvertTo-Json | Set-Content (Get-RunFile $name) -Encoding UTF8

    $client = $null; $stopping = $false; $sent = $false
    try {
        $deadline = (Get-Date).AddSeconds(20)
        while (-not $listener.Pending()) {
            if ($proc.HasExited) { Fail "openvpn exited at start (code $($proc.ExitCode)); see $log" }
            if ((Get-Date) -gt $deadline) { Fail "openvpn did not connect to the management port; see $log" }
            Start-Sleep -Milliseconds 100
        }
        $client = $listener.AcceptTcpClient()
        $listener.Stop()
        # Only answer the openvpn.exe we started.
        $rp = $client.Client.RemoteEndPoint.Port
        $owner = (Get-NetTCPConnection -LocalPort $rp -RemotePort $mport -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
        if ($owner -ne $proc.Id) { Fail "management connection came from pid $owner, not openvpn $($proc.Id); aborted" }

        $stream = $client.GetStream()
        Send-Mgmt $stream 'version 5'
        $buf = New-Object byte[] 8192
        $pending = ''
        Write-Host "    (Ctrl+C or 'p2s down $name' to disconnect; log: $log)"

        while (-not $proc.HasExited) {
            if (-not $stopping -and (Test-Path (Get-StopFile $name))) {
                Write-Step 'disconnect requested'; $stopping = $true; Send-Mgmt $stream 'signal SIGTERM'
            }
            if (-not $stream.DataAvailable) { Start-Sleep -Milliseconds 150; continue }
            $n = $stream.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $pending += [Text.Encoding]::ASCII.GetString($buf, 0, $n)
            while (($i = $pending.IndexOf("`n")) -ge 0) {
                $line = $pending.Substring(0, $i).TrimEnd("`r"); $pending = $pending.Substring($i + 1)
                if ($line.StartsWith('>HOLD:')) {
                    Send-Mgmt $stream 'state on'; Send-Mgmt $stream 'hold release'
                } elseif ($line.StartsWith(">PASSWORD:Need 'Auth'")) {
                    if ($sent) {
                        try { $token = Get-AccessToken $p } catch { Write-Warn $_.Exception.Message; Send-Mgmt $stream 'signal SIGTERM'; continue }
                    }
                    Send-Credentials $stream $token; $sent = $true
                    Write-Host '    credentials sent'
                } elseif ($line.StartsWith('>PASSWORD:Verification Failed')) {
                    Write-Warn 'the gateway rejected the token (AUTH_FAILED)'
                } elseif ($line -match '^>STATE:\d+,([A-Z_]+),([^,]*),([^,]*)') {
                    $st = $Matches[1]; $ip = $Matches[3]
                    if ($st -eq 'CONNECTED') {
                        Write-Host "    CONNECTED  $($p.adapter)  $ip" -ForegroundColor Green
                        Get-NetRoute -InterfaceAlias $p.adapter -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                            Where-Object { $_.DestinationPrefix -notmatch '^(255\.|224\.|0\.0\.0\.0)' -and $_.DestinationPrefix -notlike '*/32' } |
                            ForEach-Object { Write-Host "      route $($_.DestinationPrefix)" }
                        try { Set-P2SDnsRules $p $log } catch { Write-Warn "DNS rules not applied: $($_.Exception.Message)" }
                    } elseif ($st -eq 'RECONNECTING' -or $st -eq 'EXITING') {
                        Remove-P2SDnsRules $p.name
                        Write-Host "    $st"
                    } else {
                        Write-Host "    $st"
                    }
                } elseif ($line.StartsWith('>FATAL:') -or $line.StartsWith('ERROR:')) {
                    Write-Warn $line
                }
            }
        }
    } finally {
        if (-not $proc.HasExited) {
            try { if ($client -and $client.Connected) { Send-Mgmt $client.GetStream() 'signal SIGTERM' } } catch { }
            if (-not $proc.WaitForExit(10000)) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        }
        if ($client) { $client.Close() }
        try { $listener.Stop() } catch { }
        Remove-P2SDnsRules $name
        Remove-Item (Get-RunFile $name), (Get-StopFile $name) -ErrorAction SilentlyContinue
        Write-Step "$name disconnected"
    }
}

# ---------------------------------------------------------------- hosts ----
function Update-Hosts([switch]$Remove) {
    if (-not (Test-Admin)) { Fail 'editing the hosts file needs an elevated PowerShell' }
    $begin = '# BEGIN p2s (private endpoints)'; $end = '# END p2s'
    $lines = [IO.File]::ReadAllLines($HostsFile)
    $out = New-Object Collections.Generic.List[string]
    $skip = $false
    foreach ($l in $lines) {
        if ($l -eq $begin) { $skip = $true; continue }
        if ($l -eq $end) { $skip = $false; continue }
        if (-not $skip) { $out.Add($l) }
    }
    if (-not $Remove) {
        if (-not (Test-Path $HostsList)) { Fail "no $HostsList" }
        $entries = Get-Content $HostsList | Where-Object { $_ -match '^\s*\d+\.\d+\.\d+\.\d+\s+\S' }
        $out.Add($begin); foreach ($e in $entries) { $out.Add($e) }; $out.Add($end)
        Write-Step "$(@($entries).Count) entries written to the hosts file"
    } else {
        Write-Step 'p2s entries removed from the hosts file'
    }
    Copy-Item $HostsFile "$HostsFile.p2s-backup" -Force
    [IO.File]::WriteAllLines($HostsFile, $out.ToArray(), (New-Object Text.UTF8Encoding $false))
    Clear-DnsClientCache
}

# -------------------------------------------------------------- commands ----
function Show-Help {
    $text = Get-Content $Self -TotalCount 25 | Select-Object -Skip 1
    $text | Where-Object { $_ -ne '#>' } | ForEach-Object { Write-Host $_ }
}

function Invoke-Elevated([string]$cmd, [string[]]$cmdArgs, [switch]$KeepOpen) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass')
    if ($KeepOpen) { $a += '-NoExit' }
    $a += @('-File', "`"$Self`"", $cmd) + $cmdArgs
    Start-Process powershell.exe -Verb RunAs -ArgumentList $a | Out-Null
}

$arg1 = $null; if ($Rest -and $Rest.Count -ge 1) { $arg1 = $Rest[0] }
$arg2 = $null; if ($Rest -and $Rest.Count -ge 2) { $arg2 = $Rest[1] }

try {
    switch ($Command) {
        'import' {
            if (-not $arg1) { Fail 'usage: p2s import <azurevpnconfig.xml> [name]' }
            Import-Profile $arg1 $arg2
        }
        'list' {
            Get-ChildItem $Profiles -Directory | ForEach-Object {
                $p = Get-P2SProfile $_.Name
                $state = 'down'; if (Get-LiveRun $p.name) { $state = 'UP' }
                [pscustomobject]@{ Name = $p.name; State = $state; Adapter = $p.adapter; VNet = $p.vpnName; Gateway = @($p.gateways)[0] }
            } | Format-Table -AutoSize
        }
        'login' {
            if (-not $arg1) { Fail 'usage: p2s login <name>' }
            $p = Get-P2SProfile $arg1
            $t = Get-AccessToken $p -Interactive -ForceLogin
            $c = Get-JwtClaims $t
            Write-Step "signed in as $($c.upn) (token valid until $([DateTimeOffset]::FromUnixTimeSeconds($c.exp).LocalDateTime))"
        }
        'logout' {
            if (-not $arg1) { Fail 'usage: p2s logout <name>' }
            Remove-Item (Get-CacheFile (Get-P2SProfile $arg1)) -ErrorAction SilentlyContinue
            Write-Step 'cached sign-in removed'
        }
        'up' {
            if (-not $arg1) { Fail 'usage: p2s up <name>' }
            $p = Get-P2SProfile $arg1
            if (Get-LiveRun $p.name) { Fail "'$($p.name)' is already connected" }
            # Sign in here, un-elevated, so the browser never runs as admin.
            [void](Get-AccessToken $p -Interactive)
            if (Test-Admin) { Start-Tunnel $p.name }
            else {
                Invoke-Elevated 'run' @($p.name) -KeepOpen
                Write-Step "connecting '$($p.name)' in a new elevated window (keep it open; Ctrl+C there or 'p2s down $($p.name)' to disconnect)"
            }
        }
        'run' {
            if (-not $arg1) { Fail 'usage: p2s run <name>' }
            Start-Tunnel $arg1
        }
        'down' {
            if (-not $arg1) { Fail 'usage: p2s down <name>|all' }
            $names = @($arg1)
            if ($arg1 -eq 'all') { $names = @(Get-ChildItem $RunDir -Filter *.json | ForEach-Object { $_.BaseName }) }
            foreach ($n in $names) {
                $r = Get-LiveRun $n
                if (-not $r) { Write-Host "$n is not connected"; continue }
                New-Item -ItemType File -Path (Get-StopFile $n) -Force | Out-Null
                $proc = Get-Process -Id $r.pid -ErrorAction SilentlyContinue
                if ($proc -and -not $proc.WaitForExit(15000)) { Write-Warn "$n did not stop in 15s; close its p2s window" }
                else { Write-Step "$n disconnected" }
            }
        }
        'status' {
            $any = $false
            Get-ChildItem $Profiles -Directory | ForEach-Object {
                $p = Get-P2SProfile $_.Name
                $r = Get-LiveRun $p.name
                if (-not $r) { return }
                $any = $true
                $ip = (Get-NetIPAddress -InterfaceAlias $p.adapter -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
                $media = (Get-NetAdapter -Name $p.adapter -ErrorAction SilentlyContinue).Status
                if ($media -eq 'Up' -and $ip -and $ip -notlike '169.254.*') {
                    Write-Host ("{0,-14} CONNECTED since {1}  {2}  {3}" -f $p.name, $r.started, $p.adapter, $ip) -ForegroundColor Green
                } else {
                    Write-Host ("{0,-14} connecting (openvpn running, adapter {1}, no tunnel address yet; see logs\{0}.log)" -f $p.name, $media) -ForegroundColor Yellow
                }
                Get-NetRoute -InterfaceAlias $p.adapter -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                    Where-Object { $_.DestinationPrefix -notmatch '^(255\.|224\.|0\.0\.0\.0)' -and $_.DestinationPrefix -notlike '*/32' } |
                    ForEach-Object { Write-Host "    route $($_.DestinationPrefix)" }
            }
            if (-not $any) { Write-Host 'no p2s tunnels are up' }
        }
        'dns' {
            switch ($arg1) {
                'apply' {
                    if (-not $arg2) { Fail 'usage: p2s dns apply <name>' }
                    $p = Get-P2SProfile $arg2
                    if (-not (Get-LiveRun $p.name)) { Fail "'$($p.name)' is not connected" }
                    if (Test-Admin) { Set-P2SDnsRules $p (Join-Path $LogDir "$($p.name).log") }
                    else { Invoke-Elevated 'dns' @('apply', $p.name) -KeepOpen }
                }
                'clear' {
                    if (Test-Admin) {
                        Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'p2s:*' } |
                            ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }
                        Clear-DnsClientCache; Write-Step 'all p2s DNS rules removed'
                    } else { Invoke-Elevated 'dns' @('clear') -KeepOpen }
                }
                default {
                    $rules = @(Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'p2s:*' })
                    if ($rules.Count -eq 0) { Write-Host 'no p2s DNS rules' }
                    $rules | Select-Object @{n='Profile';e={$_.Comment.Substring(4)}}, @{n='Domain';e={$_.Namespace -join ','}}, @{n='DNS';e={$_.NameServers -join ','}} |
                        Sort-Object Profile, Domain | Format-Table -AutoSize
                }
            }
        }
        'hosts' {
            switch ($arg1) {
                'apply'  { if (Test-Admin) { Update-Hosts } else { Invoke-Elevated 'hosts' @('apply') -KeepOpen } }
                'remove' { if (Test-Admin) { Update-Hosts -Remove } else { Invoke-Elevated 'hosts' @('remove') -KeepOpen } }
                default  { if (Test-Path $HostsList) { Get-Content $HostsList } else { Write-Host "no $HostsList" } }
            }
        }
        default { Show-Help }
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
