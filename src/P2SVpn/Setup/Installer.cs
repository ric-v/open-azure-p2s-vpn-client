using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using P2SVpn.Service;

namespace P2SVpn.Setup;

/// <summary>Install / uninstall. Both run elevated (the app relaunches itself with runas).</summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\P2SVpn";
    private static string StartMenuLink =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppPaths.AppName + ".lnk");

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsAdmin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // ---- service control via sc.exe ------------------------------------------
    // ServiceController throws in half-removed states (e.g. a service "marked for
    // deletion" by an earlier uninstall still enumerates but cannot be opened), so
    // setup talks to the Service Control Manager through sc.exe and its exit codes.
    private enum SvcState { Missing, Stopped, Running, Pending, Unknown }

    private const int ErrServiceDoesNotExist = 1060;
    private const int ErrServiceAlreadyRunning = 1056;
    private const int ErrServiceNotActive = 1062;
    private const int ErrServiceMarkedForDelete = 1072;
    private const int ErrServiceExists = 1073;

    private static (SvcState State, int Pid, string Raw) QueryService()
    {
        var (code, output) = SystemTools.Run("sc.exe", ["queryex", AppPaths.ServiceName]);
        if (code == ErrServiceDoesNotExist) return (SvcState.Missing, 0, output);
        if (code != 0) return (SvcState.Unknown, 0, output);
        var st = System.Text.RegularExpressions.Regex.Match(output, @"STATE\s*:\s*(\d+)");
        var pid = System.Text.RegularExpressions.Regex.Match(output, @"PID\s*:\s*(\d+)");
        int p = pid.Success ? int.Parse(pid.Groups[1].Value) : 0;
        return st.Success ? (st.Groups[1].Value switch { "1" => SvcState.Stopped, "4" => SvcState.Running, _ => SvcState.Pending }, p, output)
                          : (SvcState.Unknown, p, output);
    }

    public static bool ServiceExists() => QueryService().State != SvcState.Missing;

    private static InvalidOperationException RebootNeeded() => new(
        "Windows is still removing an older P2S VPN service from this PC.\n\n" +
        "Restart the computer, then run P2S VPN setup again.");

    public static string? InstalledVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
        return k?.GetValue("DisplayVersion") as string;
    }

    public static bool RunningFromInstallDir() =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(AppPaths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Relaunch this exe elevated with the given argument; returns its exit code (or -1 if UAC was refused).</summary>
    public static int RunElevated(string arg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }
    }

    private static void Log(string m)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "install.log"), $"{DateTime.Now:s} {m}\r\n");
        }
        catch { }
    }

    public static int Install()
    {
        try
        {
            Log($"install {Version} from {Environment.ProcessPath}");
            StopServiceIfRunning();
            KillOtherInstances();

            // Folders. ProgramData\P2SVpn: SYSTEM/Admins full, Users read only, so a
            // normal user cannot plant configs or logs the service would trust.
            Directory.CreateDirectory(Path.Combine(AppPaths.InstallDir, "bin"));
            Directory.CreateDirectory(AppPaths.DataDir);
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(AppPaths.DataDir).SetAccessControl(ds);
            Directory.CreateDirectory(AppPaths.RunDir);
            Directory.CreateDirectory(AppPaths.LogDir);
            Nrpt.Namespaces(); // writes the default dns-namespaces.txt

            // Files.
            if (!RunningFromInstallDir()) File.Copy(Environment.ProcessPath!, AppPaths.InstalledExe, true);
            ExtractResource("payload.openvpn.exe", AppPaths.OpenVpnExe);
            VerifyFilesPresent();
            Log("files copied");

            // TAP-Windows6 driver + tapctl.exe, from the official signed OpenVPN installer.
            var haveTapCtl = File.Exists(AppPaths.TapCtl);
            var haveTap = TapDriverInstalled();
            Log($"tapctl present: {haveTapCtl}, TAP driver present: {haveTap}");
            if (haveTapCtl && !haveTap)
            {
                // OpenVPN is installed (e.g. with only the DCO driver); its installer will not
                // run again on top of itself, so the user has to add the TAP feature.
                throw new InvalidOperationException(
                    "OpenVPN is already installed on this PC, but without the TAP-Windows6 driver that P2S VPN needs.\n\n" +
                    "Open Settings > Apps > Installed apps > OpenVPN > Modify, choose Change, enable \"Drivers > TAP-Windows6\", " +
                    "finish, then run P2S VPN setup again.");
            }
            if (!haveTapCtl)
            {
                var msi = Path.Combine(Path.GetTempPath(), $"P2SVpn-OpenVPN-{Guid.NewGuid():N}.msi");
                ExtractResource("payload.OpenVPN.msi", msi);
                var msiLog = Path.Combine(AppPaths.DataDir, "openvpn-driver-install.log");
                var (code, _) = SystemTools.Run("msiexec.exe",
                    ["/i", msi, "/qn", "/norestart", "ADDLOCAL=OpenVPN,Drivers,Drivers.TAPWindows6", "/l*v", msiLog], 600000);
                try { File.Delete(msi); } catch { }
                Log($"OpenVPN driver msi exit {code}");
                if (code != 0 && code != 3010) throw new InvalidOperationException($"The OpenVPN TAP driver install failed (msiexec {code}). See {msiLog}");
                if (!File.Exists(AppPaths.TapCtl) || !TapDriverInstalled())
                    throw new InvalidOperationException($"The OpenVPN installer finished but the TAP driver is not present. See {msiLog}");
            }

            // Service.
            InstallOrUpdateService();
            StartService();
            Log("service running");

            // Start menu + Apps & Features.
            CreateShortcut(StartMenuLink, AppPaths.InstalledExe);
            using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppPaths.AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "P2S VPN contributors");
                k.SetValue("InstallLocation", AppPaths.InstallDir);
                k.SetValue("DisplayIcon", AppPaths.InstalledExe);
                k.SetValue("UninstallString", $"\"{AppPaths.InstalledExe}\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(AppPaths.InstalledExe).Length / 1024) + 7000, RegistryValueKind.DWord);
            }
            Log("install complete");
            return 0;
        }
        catch (Exception e)
        {
            Log("install failed: " + e);
            MessageBox.Show("Installation failed:\n\n" + e.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static int Uninstall()
    {
        if (MessageBox.Show("Remove P2S VPN from this computer?\n\nAll P2S VPN connections will be closed.",
                AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 2;
        try
        {
            Log("uninstall");
            StopServiceIfRunning();
            if (QueryService().State != SvcState.Missing) Sc(["delete", AppPaths.ServiceName]);
            KillOtherInstances();
            Nrpt.Remove(null);
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                if (nic.Name.StartsWith("P2S-", StringComparison.OrdinalIgnoreCase)) Adapters.Delete(nic.Name);

            try { File.Delete(StartMenuLink); } catch { }
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                run?.DeleteValue(AppPaths.AppName, false);
            try { Directory.Delete(AppPaths.DataDir, true); } catch { }

            // The running exe lives in InstallDir: remove it after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{AppPaths.InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            MessageBox.Show("P2S VPN was removed.\n\nThe OpenVPN TAP driver was left installed (other software may use it); remove \"OpenVPN\" in Apps & Features if you don't need it.",
                AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Uninstall failed:\n\n" + e.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Sc(string[] args)
    {
        var (code, output) = SystemTools.Run("sc.exe", args);
        Log($"sc {args[0]} -> {code} {output.Replace("\r", " ").Replace("\n", " ")}");
        return code;
    }

    private static void InstallOrUpdateService()
    {
        var bin = $"\"{AppPaths.InstalledExe}\" --service";
        var state = QueryService();
        Log($"service before install: {state.State}");

        string[] create = ["create", AppPaths.ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", AppPaths.ServiceDisplayName];
        string[] config = ["config", AppPaths.ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", AppPaths.ServiceDisplayName];

        int code;
        if (state.State == SvcState.Missing)
        {
            code = Sc(create);
            // Listed as missing but the name is taken: fall back to updating it.
            if (code == ErrServiceExists) code = Sc(config);
        }
        else
        {
            code = Sc(config);
        }
        if (code == ErrServiceMarkedForDelete) throw RebootNeeded();

        if (code != 0 || ServiceKey(writable: false) is null)
        {
            // Half-removed service: still listed by the Service Control Manager but its
            // registry key is gone (sc config fails with 2 "file not found"). Security
            // software that rolls back "suspicious" changes is the usual cause.
            Log($"service registration is broken (sc {code}); deleting and recreating it");
            Sc(["delete", AppPaths.ServiceName]);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (QueryService().State != SvcState.Missing && DateTime.UtcNow < until) Thread.Sleep(500);
            if (QueryService().State != SvcState.Missing) throw RebootNeeded();
            code = Sc(create);
            if (code == ErrServiceMarkedForDelete || code == ErrServiceExists) throw RebootNeeded();
            if (code != 0) throw new InvalidOperationException($"Could not register the P2S VPN service (sc error {code}).\n\n{AntivirusHint}");
        }

        Sc(["description", AppPaths.ServiceName, "Runs Azure Point-to-Site VPN tunnels for the P2S VPN app."]);
        Sc(["failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"]);

        // The single-file exe unpacks native runtime files on start. For SYSTEM that would
        // be C:\Windows\Temp, where antivirus often blocks loading DLLs; use Program Files.
        var extract = Path.Combine(AppPaths.InstallDir, ".net");
        Directory.CreateDirectory(extract);
        using var key = ServiceKey(writable: true)
            ?? throw new InvalidOperationException("The P2S VPN service was registered but its settings disappeared from the registry.\n\n" + AntivirusHint);
        key.SetValue("Environment", new[] { $"DOTNET_BUNDLE_EXTRACT_BASE_DIR={extract}" }, RegistryValueKind.MultiString);
    }

    private static RegistryKey? ServiceKey(bool writable) =>
        Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{AppPaths.ServiceName}", writable);

    public const string AntivirusHint =
        "If antivirus software is installed (for example Kaspersky), it may have blocked or rolled back P2S VPN. " +
        "Allow / exclude \"C:\\Program Files\\P2SVpn\" and \"C:\\ProgramData\\P2SVpn\" (or mark P2SVpn.exe and openvpn.exe as trusted), " +
        "check its quarantine, then run setup again.";

    /// <summary>Antivirus can quarantine a freshly copied exe a moment later; check before going on.</summary>
    private static void VerifyFilesPresent()
    {
        Thread.Sleep(1500);
        foreach (var f in new[] { AppPaths.InstalledExe, AppPaths.OpenVpnExe })
            if (!File.Exists(f))
                throw new InvalidOperationException($"{f} was removed right after it was copied.\n\n{AntivirusHint}");
    }

    private static void StartService()
    {
        var code = Sc(["start", AppPaths.ServiceName]);
        if (code == ErrServiceMarkedForDelete) throw RebootNeeded();
        if (code != 0 && code != ErrServiceAlreadyRunning)
            Log($"sc start returned {code}; waiting for the service anyway");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var s = QueryService();
            if (s.State == SvcState.Running) return;
            if (s.State == SvcState.Stopped && code is not 0 and not ErrServiceAlreadyRunning) break;
            Thread.Sleep(500);
        }
        var last = QueryService();
        var exit = System.Text.RegularExpressions.Regex.Match(last.Raw, @"WIN32_EXIT_CODE\s*:\s*(\d+)");
        Log($"service not running: {last.Raw.Replace("\r", " ").Replace("\n", " ")}");
        throw new InvalidOperationException(
            $"The P2S VPN service did not start ({last.State}, sc start {code}{(exit.Success ? $", exit code {exit.Groups[1].Value}" : "")}).\n\n" +
            (File.Exists(AppPaths.InstalledExe) ? "" : $"{AppPaths.InstalledExe} is missing.\n\n") +
            AntivirusHint);
    }

    /// <summary>Stop the service if it runs; never throws (setup must get past a broken old install).</summary>
    private static void StopServiceIfRunning()
    {
        try
        {
            var s = QueryService();
            Log($"service state: {s.State}");
            if (s.State is SvcState.Missing or SvcState.Stopped) return;

            var code = Sc(["stop", AppPaths.ServiceName]);
            if (code == ErrServiceNotActive) return;

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var now = QueryService();
                if (now.State is SvcState.Stopped or SvcState.Missing) return;
                Thread.Sleep(500);
            }

            // Stuck: end the service process so its files can be replaced.
            var stuck = QueryService();
            if (stuck.Pid > 0)
            {
                Log($"service did not stop; ending pid {stuck.Pid}");
                try { Process.GetProcessById(stuck.Pid).Kill(true); } catch { }
            }
        }
        catch (Exception e)
        {
            Log("stop service: " + e.Message);
        }
    }

    /// <summary>Close copies of the installed app (they lock the exe); never the setup that launched us.</summary>
    private static void KillOtherInstances()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("P2SVpn"))
        {
            if (p.Id == me) continue;
            try
            {
                var path = p.MainModule?.FileName;
                if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(AppPaths.InstalledExe), StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { }
        }
    }

    private static bool TapDriverInstalled()
    {
        // pnputil shows the TAP-Windows6 package as "Provider Name: TAP-Windows Provider V9"
        // (original name oemvista.inf); the tap0901 hardware id is not printed.
        var (_, output) = SystemTools.Run("pnputil.exe", ["/enum-drivers", "/class", "Net"]);
        return output.Contains("TAP-Windows Provider", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractResource(string name, string dest)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Missing embedded {name}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var f = File.Create(dest);
        s.CopyTo(f);
    }

    private static void CreateShortcut(string link, string target)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = "Azure Point-to-Site VPN";
            sc.Save();
        }
        catch (Exception e) { Log("shortcut: " + e.Message); }
    }
}
