using System.Diagnostics;

namespace P2SVpn.App;

/// <summary>Clean white dialog shell: icon, title, body, buttons on a grey footer.</summary>
internal class DialogBase : Form
{
    protected readonly FlowLayoutPanel Buttons = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
        Padding = new Padding(16, 12, 8, 12), BackColor = Theme.GraySoft, WrapContents = false,
    };
    protected readonly Panel Body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 22, 24, 12), BackColor = Color.White };

    public DialogBase(string title, int width = 460)
    {
        Text = title;
        Font = Theme.Body;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = UiKit.AppIcon;
        ClientSize = new Size(Theme.S(this, width), Theme.S(this, 200));
        Controls.Add(Body);
        Controls.Add(Buttons);
    }

    protected ModernButton AddButton(string text, ButtonKind kind, DialogResult result)
    {
        var b = new ModernButton(text, kind) { DialogResult = result, Margin = new Padding(8, 0, 0, 0) };
        b.Click += (_, _) => { DialogResult = result; Close(); };
        Buttons.Controls.Add(b);
        return b;
    }

    /// <summary>Fit the height to the body content.</summary>
    protected void FitHeight(Control last)
    {
        PerformLayout();
        ClientSize = new Size(ClientSize.Width, last.Bottom + Body.Padding.Bottom + Buttons.Height + Theme.S(this, 6));
    }

    protected static Label Heading(string text) =>
        new() { Text = text, Font = Theme.H2, ForeColor = Theme.Text, AutoSize = true, UseMnemonic = false };

    protected static Label Paragraph(string text, int width) =>
        new() { Text = text, Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false };
}

internal sealed class ConfirmDialog : DialogBase
{
    private ConfirmDialog(string title, string heading, string message, string confirm, bool danger) : base(title)
    {
        var icon = new PictureBox
        {
            Image = (danger ? SystemIcons.Warning : SystemIcons.Question).ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.S(this, 24), Theme.S(this, 24)),
        };
        var h = Heading(heading);
        h.Location = new Point(Theme.S(this, 72), Theme.S(this, 22));
        var p = Paragraph(message, Theme.S(this, 340));
        p.Location = new Point(Theme.S(this, 74), h.Bottom + 8);
        Body.Controls.AddRange([icon, h, p]);

        var cancel = AddButton("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        var ok = AddButton(confirm, danger ? ButtonKind.Danger : ButtonKind.Primary, DialogResult.OK);
        Buttons.Controls.SetChildIndex(ok, 1);
        Buttons.Controls.SetChildIndex(cancel, 0);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => cancel.Focus();
        Load += (_, _) => { p.Top = h.Bottom + Theme.S(this, 8); FitHeight(p); };
    }

    public static bool Ask(IWin32Window owner, string title, string heading, string message, string confirm, bool danger = false)
    {
        using var d = new ConfirmDialog(title, heading, message, confirm, danger);
        return d.ShowDialog(owner) == DialogResult.OK;
    }
}

internal sealed class RenameDialog : DialogBase
{
    private readonly TextBox _box = new() { Font = new Font("Segoe UI", 11f), BorderStyle = BorderStyle.FixedSingle };
    private readonly Label _error = new() { ForeColor = Theme.Red, AutoSize = true, Font = Theme.Small };
    private readonly Func<string, string?> _validate;
    public string NewName => _box.Text.Trim();

    private RenameDialog(string current, string detail, Func<string, string?> validate) : base("Rename connection")
    {
        _validate = validate;
        var h = Heading("Rename connection");
        h.Location = new Point(24, 22);
        var p = Paragraph(detail, Theme.S(this, 410));
        p.Location = new Point(24, h.Bottom + 6);
        _box.Text = current;
        _box.MaxLength = ProfileStore.MaxNameLength;
        _box.Location = new Point(24, p.Bottom + 14);
        _box.Width = Theme.S(this, 410);
        _error.Location = new Point(24, _box.Bottom + 6);
        Body.Controls.AddRange([h, p, _box, _error]);

        var cancel = AddButton("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        var save = new ModernButton("Save", ButtonKind.Primary) { Margin = new Padding(8, 0, 0, 0) };
        save.Click += (_, _) => TrySave();
        Buttons.Controls.Add(save);                          // right-to-left: [Save] [Cancel]
        AcceptButton = save;
        CancelButton = cancel;
        _box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TrySave(); } };
        Load += (_, _) =>
        {
            p.Top = h.Bottom + Theme.S(this, 6);
            _box.Top = p.Bottom + Theme.S(this, 14);
            _error.Top = _box.Bottom + Theme.S(this, 6);
            _error.Text = " ";
            FitHeight(_error);
        };
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void TrySave()
    {
        var err = _validate(NewName);
        if (err is not null) { _error.Text = err; _box.Focus(); return; }
        DialogResult = DialogResult.OK;
        Close();
    }

    public static string? Ask(IWin32Window owner, string current, string detail, Func<string, string?> validateAndSave)
    {
        using var d = new RenameDialog(current, detail, validateAndSave);
        return d.ShowDialog(owner) == DialogResult.OK ? d.NewName : null;
    }
}

internal sealed class SettingsDialog : DialogBase
{
    public SettingsDialog(AppSettings settings, bool serviceUp, string version) : base("Settings", 500)
    {
        var y = 22;
        var h = Heading("Settings");
        h.Location = new Point(24, y);
        Body.Controls.Add(h);
        y = h.Bottom + Theme.S(this, 14);

        Control Row(string title, string detail, bool value, Action<bool> onChange)
        {
            var sw = new ToggleSwitch { Location = new Point(Theme.S(this, 24), y + 2) };
            sw.SetSilently(value);
            sw.CheckedChanged += (_, _) => onChange(sw.Checked);
            var t = new Label { Text = title, Font = Theme.BodyBold, AutoSize = true, Location = new Point(Theme.S(this, 82), y), ForeColor = Theme.Text };
            var d = new Label { Text = detail, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(Theme.S(this, 380), 0), Location = new Point(Theme.S(this, 82), y + t.Height + 2) };
            t.Click += (_, _) => sw.Checked = !sw.Checked;
            Body.Controls.AddRange([sw, t, d]);
            y = d.Bottom + Theme.S(this, 16);
            return d;
        }

        Row("Start P2S VPN when I sign in to Windows",
            "Opens quietly in the notification area after you sign in or turn on the PC. Connections marked \"auto-connect\" come up by themselves.",
            AppSettings.StartWithWindows, v => { try { AppSettings.StartWithWindows = v; } catch { } });
        Row("Start minimized to the notification area",
            "When started at sign-in, don't open the window.",
            settings.StartMinimized, v => { settings.StartMinimized = v; settings.Save(); });
        Row("Show notifications",
            "Tell me when a VPN connects, disconnects or needs me to sign in again.",
            settings.Notifications, v => { settings.Notifications = v; settings.Save(); });

        var sep = new Panel { BackColor = Theme.Border, Height = 1, Width = Theme.S(this, 452), Location = new Point(Theme.S(this, 24), y) };
        y += Theme.S(this, 14);
        var about = new Label
        {
            Text = $"P2S VPN {version}   ·   Service {(serviceUp ? "running" : "not running")}",
            Font = Theme.Small, ForeColor = serviceUp ? Theme.Muted : Theme.Red, AutoSize = true, Location = new Point(Theme.S(this, 24), y),
        };
        var logs = new LinkLabel
        {
            Text = "Open log folder", AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent,
            Location = new Point(Theme.S(this, 24), about.Bottom + Theme.S(this, 6)),
        };
        logs.LinkClicked += (_, _) =>
        {
            if (Directory.Exists(AppPaths.LogDir)) Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });
        };
        var uninstall = new Label
        {
            Text = "To remove P2S VPN: Settings > Apps > Installed apps > P2S VPN > Uninstall.",
            Font = Theme.Small, ForeColor = Theme.Subtle, AutoSize = true, Location = new Point(Theme.S(this, 24), logs.Bottom + Theme.S(this, 6)),
        };
        Body.Controls.AddRange([sep, about, logs, uninstall]);

        var close = AddButton("Done", ButtonKind.Primary, DialogResult.OK);
        AcceptButton = close;
        CancelButton = close;
        Load += (_, _) => FitHeight(uninstall);
    }
}
