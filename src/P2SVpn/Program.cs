using System.ServiceProcess;
using P2SVpn.App;
using P2SVpn.Service;
using P2SVpn.Setup;

namespace P2SVpn;

internal static class Program
{
    private const string MutexName = @"Local\P2SVpn.Gui";
    private const string ShowEventName = @"Local\P2SVpn.Gui.Show";

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant();

        if (mode == "--service")
        {
            ServiceBase.Run(new VpnService());
            return 0;
        }

        ApplicationConfiguration.Initialize();

        switch (mode)
        {
            case "--install":
                return Installer.IsAdmin() ? Installer.Install() : Installer.RunElevated("--install");
            case "--uninstall":
                return Installer.IsAdmin() ? Installer.Uninstall() : Installer.RunElevated("--uninstall");
        }

        // Developer aid: render the main window (or a dialog) to a PNG and exit.
        if (mode == "--screenshot" && args.Length >= 2)
        {
            Form f = args.Length >= 3 && args[2] == "settings"
                ? new SettingsDialog(AppSettings.Load(), true, Installer.Version)
                : args.Length >= 3 && args[2] == "setup" ? new SetupForm()
                : new MainForm(startHidden: false, autostart: false);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, 0);
            f.ShowInTaskbar = false;
            f.Shown += async (_, _) =>
            {
                await Task.Delay(3500);
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                Environment.Exit(0);
            };
            Application.Run(f);
            return 0;
        }

        if (!Installer.RunningFromInstallDir())
        {
            Application.Run(new SetupForm());
            return 0;
        }

        // One app per user session; a second launch brings the first to the front.
        using var mutex = new Mutex(true, MutexName, out var first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            showEvent.Set();
            return 0;
        }

        // --autostart: launched at Windows sign-in (--tray is the 1.0.x spelling).
        var autostart = mode is "--autostart" or "--tray";
        var form = new MainForm(startHidden: autostart && AppSettings.Load().StartMinimized, autostart: autostart);
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { form.BeginInvoke(form.ShowMain); } catch { return; }
            }
        }) { IsBackground = true };
        listener.Start();

        Application.Run(form);
        return 0;
    }
}
