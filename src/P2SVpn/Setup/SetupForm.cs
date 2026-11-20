using System.Diagnostics;
using P2SVpn.App;

namespace P2SVpn.Setup;

/// <summary>Shown when the exe runs from anywhere but Program Files: install or update.</summary>
internal sealed class SetupForm : Form
{
    private readonly Label _status = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent };
    private readonly ModernButton _install;
    private readonly ModernButton _open;

    public SetupForm()
    {
        Text = AppPaths.AppName + " Setup";
        Icon = UiKit.AppIcon;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(this, 560), Theme.S(this, 430));

        var installed = Installer.InstalledVersion();
        var exists = installed is not null && File.Exists(AppPaths.InstalledExe);

        var card = new RoundedPanel { Location = new Point(Theme.S(this, 20), Theme.S(this, 20)), Size = new Size(Theme.S(this, 520), Theme.S(this, 330)) };
        var logo = new PictureBox { Image = UiKit.AppIconBitmap(Theme.S(this, 56)), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.S(this, 28), Theme.S(this, 28)), BackColor = Color.Transparent };
        var title = new Label { Text = AppPaths.AppName, Font = Theme.H1, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent, Location = new Point(Theme.S(this, 98), Theme.S(this, 28)) };
        var ver = new Label
        {
            Text = exists ? $"Installed {installed}   ·   This setup {Installer.Version}" : $"Version {Installer.Version}",
            Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent, Location = new Point(Theme.S(this, 100), Theme.S(this, 66)),
        };
        var intro = new Label
        {
            Text = "Connect to several Azure VPNs at the same time, signing in with your Microsoft work account.",
            Font = Theme.Body, ForeColor = Theme.Text, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Theme.S(this, 460), 0), Location = new Point(Theme.S(this, 28), Theme.S(this, 108)),
        };
        card.Controls.AddRange([logo, title, ver, intro]);

        int y = Theme.S(this, 160);
        foreach (var (head, text) in new[]
                 {
                     ("The P2S VPN app", "Installed for everyone on this PC, with a Start menu entry."),
                     ("A background service", "Runs the VPN tunnels, so connecting never asks for admin rights."),
                     ("The OpenVPN network driver", "Official signed TAP-Windows6 driver, skipped if already installed."),
                 })
        {
            var check = new Label { Text = "✓", Font = Theme.BodyBold, ForeColor = Theme.Green, AutoSize = true, BackColor = Color.Transparent, Location = new Point(Theme.S(this, 30), y) };
            var h = new Label { Text = head, Font = Theme.BodyBold, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent, Location = new Point(Theme.S(this, 54), y) };
            var t = new Label { Text = text, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, BackColor = Color.Transparent, Location = new Point(Theme.S(this, 54), y + h.PreferredHeight) };
            card.Controls.AddRange([check, h, t]);
            y += Theme.S(this, 48);
        }

        _install = new ModernButton(exists ? "Update" : "Install", ButtonKind.Primary);
        _install.Click += async (_, _) => await InstallAsync();
        _open = new ModernButton("Open installed app", ButtonKind.Secondary) { Visible = exists };
        _open.Click += (_, _) => { LaunchInstalled(); Close(); };
        var close = new ModernButton("Close", ButtonKind.Ghost);
        close.Click += (_, _) => Close();

        Controls.AddRange([card, _install, _open, close, _status]);
        Load += (_, _) =>
        {
            int by = card.Bottom + Theme.S(this, 18);
            _install.Location = new Point(card.Left, by);
            _open.Location = new Point(_install.Right + Theme.S(this, 10), by);
            close.Location = new Point(card.Right - close.Width, by);
            _status.Location = new Point(card.Left + 2, _install.Bottom + Theme.S(this, 10));
            ClientSize = new Size(ClientSize.Width, _status.Top + Theme.S(this, 34));
        };
        AcceptButton = _install;
    }

    private async Task InstallAsync()
    {
        _install.Enabled = false;
        _open.Enabled = false;
        _status.ForeColor = Theme.Muted;
        _status.Text = "Installing... approve the administrator prompt. This can take a minute.";
        var code = await Task.Run(() => Installer.RunElevated("--install"));
        if (code == 0)
        {
            _status.ForeColor = Theme.Green;
            _status.Text = "Installed. Starting P2S VPN...";
            await Task.Delay(800);
            LaunchInstalled();
            Close();
            return;
        }
        _status.ForeColor = Theme.Red;
        _status.Text = code == -1
            ? "Setup was cancelled at the administrator prompt."
            : $"Setup failed (code {code}). Details: {Path.Combine(AppPaths.DataDir, "install.log")}";
        _install.Enabled = true;
        _open.Enabled = true;
    }

    private static void LaunchInstalled()
    {
        try { Process.Start(new ProcessStartInfo(AppPaths.InstalledExe) { UseShellExecute = true }); } catch { }
    }
}
