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

    public static bool ServiceExists() =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(AppPaths.ServiceName, StringComparison.OrdinalIgnoreCase));

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
            var bin = $"\"{AppPaths.InstalledExe}\" --service";
            if (ServiceExists())
                Sc(["config", AppPaths.ServiceName, "binPath=", bin, "start=", "auto"]);
            else
                Sc(["create", AppPaths.ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", AppPaths.ServiceDisplayName]);
            Sc(["description", AppPaths.ServiceName, "Runs Azure Point-to-Site VPN tunnels for the P2S VPN app."]);
            Sc(["failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"]);
            using (var sc = new ServiceController(AppPaths.ServiceName))
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
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
            if (ServiceExists()) Sc(["delete", AppPaths.ServiceName]);
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

    private static void Sc(string[] args)
    {
        var (code, output) = SystemTools.Run("sc.exe", args);
        Log($"sc {args[0]} -> {code} {output.Replace("\r", " ").Replace("\n", " ")}");
        if (code != 0 && args[0] is "create" or "config")
            throw new InvalidOperationException($"sc {args[0]} failed ({code}): {output}");
    }

    private static void StopServiceIfRunning()
    {
        if (!ServiceExists()) return;
        using var sc = new ServiceController(AppPaths.ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return;
        try
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch { }
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
