namespace P2SVpn;

internal static class AppPaths
{
    public const string ServiceName = "P2SVpn";
    public const string ServiceDisplayName = "P2S VPN Service";
    public const string AppName = "P2S VPN";
    public const string ExeName = "P2SVpn.exe";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "P2SVpn");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);
    public static string OpenVpnExe => Path.Combine(InstallDir, "bin", "openvpn.exe");

    /// <summary>Service-owned state: generated configs, logs. SYSTEM/Admins write, Users read.</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "P2SVpn");
    public static string RunDir => Path.Combine(DataDir, "run");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string DnsNamespacesFile => Path.Combine(DataDir, "dns-namespaces.txt");

    /// <summary>Per-user state: imported profiles, cached sign-in.</summary>
    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P2SVpn");
    public static string ProfilesDir => Path.Combine(UserDir, "profiles");
    public static string TokenCacheDir => Path.Combine(UserDir, "cache");

    public static string TapCtl =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "tapctl.exe");

    public static string AdapterName(string profile) => "P2S-" + profile;

    public static bool IsValidProfileName(string? name) =>
        name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,30}$");
}
