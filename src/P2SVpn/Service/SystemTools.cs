using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace P2SVpn.Service;

internal static class SystemTools
{
    public static (int code, string output) Run(string exe, IEnumerable<string> args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    public static (int code, string output) PowerShell(string script) =>
        Run(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
}

/// <summary>One TAP-Windows6 adapter per profile, named P2S-&lt;profile&gt;.</summary>
internal static class Adapters
{
    // Azure P2S does not negotiate DATA_V2, which ovpn-dco requires, so TAP it is.
    public static void Ensure(string adapter)
    {
        if (!File.Exists(AppPaths.TapCtl))
            throw new InvalidOperationException("The OpenVPN TAP driver tools are missing. Reinstall P2S VPN.");

        var nic = Find(adapter);
        if (nic is not null && nic.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)) return;
        if (nic is not null) SystemTools.Run(AppPaths.TapCtl, ["delete", adapter]);

        var (code, output) = SystemTools.Run(AppPaths.TapCtl, ["create", "--name", adapter, "--hwid", @"root\tap0901"]);
        nic = Find(adapter);
        if (nic is null || !nic.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not create network adapter {adapter} (tapctl {code}: {output})");
    }

    public static NetworkInterface? Find(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void Delete(string adapter)
    {
        if (File.Exists(AppPaths.TapCtl) && Find(adapter) is not null) SystemTools.Run(AppPaths.TapCtl, ["delete", adapter]);
    }
}

/// <summary>
/// NRPT rules: Windows queries every adapter's DNS server in parallel and takes the
/// first answer, so a VPN DNS server loses to the home router. NRPT sends chosen
/// domains to the VPN's resolver only (what the Azure VPN Client does).
/// </summary>
internal static class Nrpt
{
    // Azure services that commonly sit behind private endpoints. Add your own
    // company domains (custom domains of private apps) to dns-namespaces.txt.
    public static readonly string[] DefaultNamespaces =
    [
        "azurewebsites.net", "azurestaticapps.net", "mysql.database.azure.com",
        "postgres.database.azure.com", "database.windows.net", "documents.azure.com",
        "vault.azure.net", "azurecr.io", "eventgrid.azure.net", "servicebus.windows.net", "redis.cache.windows.net",
        "blob.core.windows.net", "file.core.windows.net", "queue.core.windows.net", "table.core.windows.net",
    ];

    public static List<string> Namespaces()
    {
        try
        {
            if (!File.Exists(AppPaths.DnsNamespacesFile))
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllLines(AppPaths.DnsNamespacesFile,
                    ["# Domains sent to a VPN's DNS server while that VPN is connected (one per line).",
                     "# Edit as administrator; applies on the next connect.",
                     "# Add your organisation's private domains here, e.g. intranet.example.com", .. DefaultNamespaces]);
            }
            return File.ReadAllLines(AppPaths.DnsNamespacesFile)
                .Select(l => l.Trim().TrimStart('.').ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && Regex.IsMatch(l, "^[a-z0-9.-]{1,200}$"))
                .Distinct().Select(l => "." + l).ToList();
        }
        catch { return DefaultNamespaces.Select(n => "." + n).ToList(); }
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Returns the number of rules added.</summary>
    public static int Apply(string profile, IReadOnlyList<string> servers)
    {
        if (!AppPaths.IsValidProfileName(profile)) return 0;
        var srv = servers.Where(s => System.Net.IPAddress.TryParse(s, out _)).ToList();
        if (srv.Count == 0) return 0;
        var ns = Namespaces();
        var script =
            $"$c={Q("p2s:" + profile)};" +
            "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq $c } | ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force };" +
            "$owned=@{}; Get-DnsClientNrptRule | Where-Object { $_.Comment -like 'p2s:*' } | ForEach-Object { foreach($n in $_.Namespace){ $owned[$n]=1 } };" +
            "$added=0;" +
            $"foreach($n in @({string.Join(",", ns.Select(Q))})) {{ if (-not $owned.ContainsKey($n)) {{ Add-DnsClientNrptRule -Namespace $n -NameServers @({string.Join(",", srv.Select(Q))}) -Comment $c | Out-Null; $added++ }} }};" +
            "Clear-DnsClientCache; Write-Output $added";
        var (code, output) = SystemTools.PowerShell(script);
        return code == 0 && int.TryParse(output.Split('\n').LastOrDefault()?.Trim(), out var n) ? n : 0;
    }

    public static void Remove(string? profile)
    {
        var filter = profile is null ? "$_.Comment -like 'p2s:*'" : $"$_.Comment -eq {Q("p2s:" + profile)}";
        SystemTools.PowerShell(
            $"Get-DnsClientNrptRule | Where-Object {{ {filter} }} | ForEach-Object {{ Remove-DnsClientNrptRule -Name $_.Name -Force }}; Clear-DnsClientCache");
    }
}
