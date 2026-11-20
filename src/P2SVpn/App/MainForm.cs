using System.Diagnostics;

namespace P2SVpn.App;

internal sealed class MainForm : Form
{
    // ---- state ---------------------------------------------------------------
    private readonly AppSettings _settings = AppSettings.Load();
    private List<StoredProfile> _profiles = [];
    private Dictionary<string, TunnelStatus> _status = new();
    private readonly Dictionary<string, string> _busy = new();          // id -> "Signing in..." etc.
    private readonly HashSet<string> _tokenInFlight = new();
    private readonly HashSet<string> _signInNeeded = new();
    private readonly Dictionary<string, string> _lastState = new();
    private string? _selectedId;
    private bool _serviceUp = true;
    private bool _polling, _exiting, _trayHintShown, _initDone, _autoConnectDone;
    private readonly bool _startHidden, _autostart;
    private int _logTick;

    // ---- chrome --------------------------------------------------------------
    private readonly Label _summary = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
    private readonly Label _svcLabel = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly DropZone _empty = new() { Dock = DockStyle.Fill };
    private readonly DropZone _dragOverlay = new()
    {
        Dock = DockStyle.Fill, Visible = false, Active = true,
        Title = "Drop to add the connection", Subtitle = "azurevpnconfig.xml or the Azure profile .zip",
    };
    private readonly FlowLayoutPanel _cards = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        BackColor = Theme.Bg, Padding = new Padding(0, 0, 4, 0),
    };
    private readonly Label _cardsCaption = new() { Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = true };

    // ---- detail panel --------------------------------------------------------
    private readonly RoundedPanel _detail = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 20) };
    private readonly Label _name = new() { Font = Theme.H1, ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false };
    private readonly Pill _statusPill = new();
    private readonly ModernButton _primary = new("Connect", ButtonKind.Primary);
    private readonly ModernButton _rename = new("Rename", ButtonKind.Ghost);
    private readonly Label _subline = new() { Font = Theme.Body, ForeColor = Theme.Muted, AutoSize = true, UseMnemonic = false };
    private readonly InfoTile _tAddress = new("Tunnel address"), _tSince = new("Connected for"), _tTraffic = new("Traffic"),
        _tAccount = new("Signed in as"), _tDns = new("DNS"), _tGateway = new("Gateway");
    private readonly FlowLayoutPanel _routes = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
    private readonly ToggleSwitch _autoSwitch = new();
    private readonly ModernButton _signIn = new("Sign in again", ButtonKind.Secondary);
    private readonly ModernButton _remove = new("Remove connection", ButtonKind.DangerGhost);
    private readonly TextBox _log = new();
    private readonly CheckBox _follow = new() { Text = "Follow", Checked = true, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent };

    // ---- tray ----------------------------------------------------------------
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new() { ShowImageMargin = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public MainForm(bool startHidden, bool autostart)
    {
        _startHidden = startHidden;
        _autostart = autostart;
        Text = AppPaths.AppName;
        Icon = UiKit.AppIcon;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ClientSize = new Size(Theme.S(this, 1080), Theme.S(this, 700));
        MinimumSize = new Size(Theme.S(this, 900), Theme.S(this, 600));
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        BuildChrome();
        BuildDetail();
        BuildTray();
        WireDragDrop(this);

        _timer.Tick += async (_, _) =>
        {
            // A drag cancelled with Esc over the window leaves no DragLeave we can trust.
            if (_dragOverlay.Visible && MouseButtons == MouseButtons.None) _dragOverlay.Visible = false;
            await PollAsync();
        };
        FormClosing += OnClosing;
        Resize += (_, _) => FitCards();
    }

    // Start hidden at sign-in without flashing the window.
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated && _startHidden)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initDone) return;
        _initDone = true;
        _settings.ApplyFirstRunDefaults();
        ReloadProfiles();
        _timer.Start();
        _ = PollAsync();
    }

    // ======================================================================== layout
    private void BuildChrome()
    {
        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(this, 76), BackColor = Color.White };
        header.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };
        var logo = new PictureBox { Image = UiKit.AppIconBitmap(Theme.S(this, 36)), SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Color.Transparent };
        var title = new Label { Text = AppPaths.AppName, Font = Theme.AppTitle, ForeColor = Theme.Text, AutoSize = true };
        var add = new ModernButton("+  Add connection", ButtonKind.Primary);
        add.Click += (_, _) => BrowseForProfile();
        var settings = new ModernButton("Settings", ButtonKind.Secondary);
        settings.Click += (_, _) => ShowSettings();
        header.Controls.AddRange([logo, title, _summary, add, settings]);
        header.Resize += (_, _) =>
        {
            int pad = Theme.S(this, 24);
            logo.Location = new Point(pad, (header.Height - logo.Height) / 2);
            title.Location = new Point(logo.Right + Theme.S(this, 12), Theme.S(this, 14));
            _summary.Location = new Point(logo.Right + Theme.S(this, 13), title.Bottom);
            add.Location = new Point(header.Width - pad - add.Width, (header.Height - add.Height) / 2);
            settings.Location = new Point(add.Left - Theme.S(this, 10) - settings.Width, add.Top);
        };

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(this, 30), BackColor = Theme.Bg };
        var ver = new Label { Text = "v" + Setup.Installer.Version, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Subtle };
        footer.Controls.AddRange([_svcLabel, ver]);
        footer.Resize += (_, _) =>
        {
            _svcLabel.Location = new Point(Theme.S(this, 24), (footer.Height - _svcLabel.Height) / 2);
            ver.Location = new Point(footer.Width - ver.Width - Theme.S(this, 24), _svcLabel.Top);
        };

        // Body: list of cards on the left, details on the right.
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.S(this, 20), Theme.S(this, 18), Theme.S(this, 20), Theme.S(this, 4)) };
        var left = new Panel { Dock = DockStyle.Left, Width = Theme.S(this, 360), BackColor = Theme.Bg };
        var capPanel = new Panel { Dock = DockStyle.Top, Height = Theme.S(this, 28), BackColor = Theme.Bg };
        _cardsCaption.Location = new Point(Theme.S(this, 2), Theme.S(this, 4));
        capPanel.Controls.Add(_cardsCaption);
        left.Controls.Add(_cards);
        left.Controls.Add(capPanel);
        _cards.Resize += (_, _) => FitCards();
        var gap = new Panel { Dock = DockStyle.Left, Width = Theme.S(this, 16), BackColor = Theme.Bg };

        _content.Controls.Add(_detail);
        _content.Controls.Add(gap);
        _content.Controls.Add(left);

        _empty.Browse.Click += (_, _) => BrowseForProfile();
        _dragOverlay.Browse.Visible = false;

        body.Controls.Add(_content);
        body.Controls.Add(_empty);
        body.Controls.Add(_dragOverlay);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private void BuildDetail()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Header: name + status pill + rename, primary action on the right.
        var head = new Panel { Dock = DockStyle.Fill, Height = Theme.S(this, 92), BackColor = Color.Transparent, Margin = new Padding(0) };
        head.Controls.AddRange([_name, _statusPill, _rename, _primary]);
        head.Resize += (_, _) => LayoutHead(head);
        _primary.Resize += (_, _) => LayoutHead(head);
        _primary.Click += async (_, _) => await ToggleAsync(Selected());
        _rename.Click += (_, _) => RenameSelected();

        // Detail tiles.
        var tiles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(this, 6), 0, 0) };
        for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int i = 0; i < 2; i++) tiles.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(this, 68)));
        tiles.Controls.AddRange([_tAddress, _tSince, _tTraffic, _tAccount, _tDns, _tGateway]);

        // Routes.
        var routesBox = new Panel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(this, 4), 0, 0) };
        var routesCap = new Label { Text = "ROUTES", Font = Theme.Caption, ForeColor = Theme.Muted, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, Theme.S(this, 6)) };
        routesBox.Controls.Add(_routes);
        routesBox.Controls.Add(routesCap);

        // Options + secondary actions.
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(this, 8), 0, 0) };
        var autoLabel = new Label { Text = "Connect automatically when I sign in to Windows", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, Theme.S(this, 4), Theme.S(this, 24), 0), Cursor = Cursors.Hand };
        autoLabel.Click += (_, _) => _autoSwitch.Checked = !_autoSwitch.Checked;
        _autoSwitch.CheckedChanged += (_, _) => { if (Selected() is { } p) { _settings.SetAutoConnect(p.Id, _autoSwitch.Checked); RenderAll(); } };
        options.Controls.AddRange([_autoSwitch, autoLabel]);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(this, 10), 0, Theme.S(this, 6)) };
        _signIn.Click += async (_, _) => { if (Selected() is { } p) await SignInAsync(p); };
        _remove.Click += async (_, _) => await RemoveAsync(Selected());
        actions.Controls.AddRange([_signIn, _remove]);

        // Activity log.
        var logHead = new Panel { Dock = DockStyle.Fill, Height = Theme.S(this, 40), BackColor = Color.Transparent, Margin = new Padding(0) };
        logHead.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, logHead.Width, 0); };
        var logTitle = new Label { Text = "Activity log", Font = Theme.BodyBold, ForeColor = Theme.Text, AutoSize = true, BackColor = Color.Transparent };
        var copy = new ModernButton("Copy", ButtonKind.Ghost);
        copy.Click += (_, _) => { if (_log.TextLength > 0) Clipboard.SetText(_log.Text); };
        var folder = new ModernButton("Open folder", ButtonKind.Ghost);
        folder.Click += (_, _) => { if (Directory.Exists(AppPaths.LogDir)) Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true }); };
        logHead.Controls.AddRange([logTitle, _follow, copy, folder]);
        logHead.Resize += (_, _) =>
        {
            logTitle.Location = new Point(0, Theme.S(this, 12));
            folder.Location = new Point(logHead.Width - folder.Width, Theme.S(this, 6));
            copy.Location = new Point(folder.Left - copy.Width, folder.Top);
            _follow.Location = new Point(copy.Left - _follow.Width - Theme.S(this, 8), Theme.S(this, 12));
        };
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = true;
        _log.Font = Theme.Mono;
        _log.BackColor = Theme.GraySoft;
        _log.ForeColor = Theme.Text;
        _log.BorderStyle = BorderStyle.None;
        _log.Dock = DockStyle.Fill;
        var logWrap = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.GraySoft, Stroke = Color.Transparent, Radius = 10, Padding = new Padding(10, 8, 4, 8), Margin = new Padding(0) };
        logWrap.Controls.Add(_log);

        layout.RowCount = 8;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(this, 92)));  // head
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // subline
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(this, 146)));  // tiles
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // routes
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // auto-connect
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                      // actions
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(this, 40)));  // log header
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));                  // log
        _subline.Margin = new Padding(0, 0, 0, Theme.S(this, 10));
        layout.Controls.Add(head, 0, 0);
        layout.Controls.Add(_subline, 0, 1);
        layout.Controls.Add(tiles, 0, 2);
        layout.Controls.Add(routesBox, 0, 3);
        layout.Controls.Add(options, 0, 4);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(logHead, 0, 6);
        layout.Controls.Add(logWrap, 0, 7);
        layout.Resize += (_, _) =>
        {
            _subline.MaximumSize = new Size(Math.Max(100, layout.Width - 10), 0);
            // Let the route chips wrap onto more rows within the panel width.
            _routes.MaximumSize = new Size(Math.Max(100, layout.Width - 6), 0);
            _routes.MinimumSize = new Size(Math.Max(100, layout.Width - 6), 0);
        };
        _detail.Controls.Add(layout);
    }

    private void LayoutHead(Panel head)
    {
        _primary.Location = new Point(head.Width - _primary.Width, Theme.S(this, 6));
        _name.Location = new Point(0, 0);
        _name.Size = new Size(Math.Max(50, _primary.Left - Theme.S(this, 16)), Theme.H1.Height + 6);
        _statusPill.Location = new Point(Theme.S(this, 2), _name.Bottom + Theme.S(this, 6));
        _rename.Location = new Point(_statusPill.Right + Theme.S(this, 8), _statusPill.Top + (_statusPill.Height - _rename.Height) / 2);
    }

    private void FitCards()
    {
        int w = _cards.ClientSize.Width - _cards.Padding.Horizontal;
        foreach (Control c in _cards.Controls) if (c.Width != w) c.Width = w;
    }

    private void BuildTray()
    {
        _tray.Icon = UiKit.MakeIcon(Theme.Gray);
        _tray.Text = AppPaths.AppName;
        _tray.Visible = true;
        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => ShowMain();
        _trayMenu.Opening += (_, _) => RebuildTrayMenu();
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        var open = new ToolStripMenuItem("Open P2S VPN", null, (_, _) => ShowMain()) { Font = new Font(_trayMenu.Font, FontStyle.Bold) };
        _trayMenu.Items.Add(open);
        _trayMenu.Items.Add(new ToolStripSeparator());
        foreach (var p in _profiles)
        {
            var (h, text) = StateOf(p.Id);
            var running = IsRunning(p.Id);
            var prof = p;
            var item = new ToolStripMenuItem($"{p.DisplayName}   ({text})", UiKit.Dot(Theme.Strong(h)))
            {
                ToolTipText = running ? "Click to disconnect" : "Click to connect",
                Checked = false,
            };
            item.Click += async (_, _) => await ToggleAsync(prof);
            _trayMenu.Items.Add(item);
        }
        if (_profiles.Count > 0) _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Settings...", null, (_, _) => { ShowMain(); ShowSettings(); });
        _trayMenu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
    }

    // ======================================================================== drag & drop
    private void WireDragDrop(Control c)
    {
        if (c is TextBox) return;
        c.AllowDrop = true;
        c.DragEnter += OnDragEnter;
        c.DragOver += OnDragEnter;
        c.DragLeave += (_, _) => EndDrag();
        c.DragDrop += (_, e) => { _dragOverlay.Visible = false; ImportFiles(DroppedFiles(e)); };
        c.ControlAdded += (_, e) => { if (e.Control is not null) WireDragDrop(e.Control); };
        foreach (Control child in c.Controls) WireDragDrop(child);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = DroppedFiles(e).Length > 0;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && !_dragOverlay.Visible) { _dragOverlay.Visible = true; _dragOverlay.BringToFront(); }
    }

    private void EndDrag()
    {
        // DragLeave also fires when moving between child controls; only hide once the
        // cursor has really left the window.
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position)) || (MouseButtons & MouseButtons.Left) == 0)
            _dragOverlay.Visible = false;
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

    // ======================================================================== profiles
    private void ReloadProfiles(string? select = null)
    {
        _profiles = ProfileStore.Load();
        _selectedId = select ?? (_profiles.Any(p => p.Id == _selectedId) ? _selectedId : _profiles.FirstOrDefault()?.Id);

        _cards.SuspendLayout();
        var existing = _cards.Controls.OfType<ConnectionCard>().ToDictionary(c => c.Profile.Id);
        foreach (var c in existing.Values.Where(c => _profiles.All(p => p.Id != c.Profile.Id)))
        {
            _cards.Controls.Remove(c);
            c.Dispose();
        }
        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            if (!existing.TryGetValue(p.Id, out var card))
            {
                card = new ConnectionCard(p);
                card.CardClicked += (s, _) => Select(((ConnectionCard)s!).Profile.Id);
                card.ToggleClicked += async (s, _) => await ToggleAsync(((ConnectionCard)s!).Profile);
                card.MenuRequested += (s, pt) => ShowCardMenu((ConnectionCard)s!, pt);
                _cards.Controls.Add(card);
            }
            _cards.Controls.SetChildIndex(card, i);
        }
        _cards.ResumeLayout();
        FitCards();

        _empty.Visible = _profiles.Count == 0;
        _content.Visible = _profiles.Count > 0;
        RenderAll();
    }

    private StoredProfile? Selected() => _profiles.FirstOrDefault(p => p.Id == _selectedId);

    private void Select(string id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        _log.Clear();
        _logTick = 99;
        RenderAll();
    }

    private void ShowCardMenu(ConnectionCard card, Point at)
    {
        var p = card.Profile;
        var running = IsRunning(p.Id);
        var m = new ContextMenuStrip();
        m.Items.Add(running ? "Disconnect" : "Connect", null, async (_, _) => await ToggleAsync(p));
        m.Items.Add("Rename...", null, (_, _) => { Select(p.Id); RenameSelected(); });
        var auto = new ToolStripMenuItem("Connect automatically at sign-in") { Checked = _settings.IsAutoConnect(p.Id) };
        auto.Click += (_, _) => { _settings.SetAutoConnect(p.Id, !auto.Checked); RenderAll(); };
        m.Items.Add(auto);
        m.Items.Add("Sign in again", null, async (_, _) => await SignInAsync(p));
        m.Items.Add(new ToolStripSeparator());
        var rm = new ToolStripMenuItem("Remove...", null, async (_, _) => await RemoveAsync(p)) { ForeColor = Theme.Red };
        m.Items.Add(rm);
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(card, at);
    }

    private void BrowseForProfile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add an Azure VPN connection",
            Filter = "Azure VPN profile (*.xml;*.zip)|*.xml;*.zip|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) ImportFiles(dlg.FileNames);
    }

    private void ImportFiles(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count == 0) return;
        string? last = null;
        StoredProfile? toRename = null;
        var errors = new List<string>();
        foreach (var f in list)
        {
            try
            {
                var added = ProfileStore.Import(f);
                last = added.Last().Id;
                toRename ??= added.FirstOrDefault(a => a.SuggestRename);
            }
            catch (Exception e) when (e is ProfileException or IOException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(f)}: {e.Message}");
            }
        }
        ReloadProfiles(toRename?.Id ?? last);
        if (errors.Count > 0)
            ConfirmDialog.Ask(this, "Could not add", "Some files could not be added", string.Join("\n\n", errors), "OK");
        if (toRename is not null)
            RenameSelected($"You already have a connection called \"{toRename.Profile.VpnName}\". " +
                           $"This one uses a different gateway ({toRename.Profile.Gateways[0]}), so give it a name you'll recognise.");
    }

    private void RenameSelected(string? why = null)
    {
        if (Selected() is not { } p) return;
        var detail = why ?? $"Choose a name for this connection. The network in the profile is {p.Profile.VpnName}.";
        var name = RenameDialog.Ask(this, p.DisplayName, detail, n => ProfileStore.Rename(p.Id, n));
        if (name is not null) ReloadProfiles(p.Id);
    }

    private async Task RemoveAsync(StoredProfile? p)
    {
        if (p is null) return;
        var running = IsRunning(p.Id);
        if (!ConfirmDialog.Ask(this, "Remove connection", $"Remove \"{p.DisplayName}\"?",
                (running ? "It is connected now and will be disconnected first.\n\n" : "") +
                "The profile is deleted from this PC. You can add it again any time from its azurevpnconfig.xml.",
                "Remove", danger: true)) return;
        if (running) await DisconnectAsync(p);
        await Ipc.CallAsync(new IpcRequest { Cmd = "forget", Name = p.Id });
        _settings.SetAutoConnect(p.Id, false);
        ProfileStore.Remove(p.Id);
        _selectedId = null;
        ReloadProfiles();
    }

    private void ShowSettings()
    {
        using var d = new SettingsDialog(_settings, _serviceUp, Setup.Installer.Version);
        d.ShowDialog(this);
    }

    // ======================================================================== connect / disconnect
    private bool IsRunning(string id) => _status.GetValueOrDefault(id)?.Running == true;

    private async Task ToggleAsync(StoredProfile? p)
    {
        if (p is null || _busy.ContainsKey(p.Id)) return;
        if (IsRunning(p.Id)) await DisconnectAsync(p);
        else await ConnectAsync(p, interactive: true);
    }

    private async Task ConnectAsync(StoredProfile p, bool interactive)
    {
        if (_busy.ContainsKey(p.Id)) return;
        _busy[p.Id] = "Signing in";
        RenderAll();
        try
        {
            TokenResult? token;
            try
            {
                token = interactive
                    ? await EntraAuth.GetTokenAsync(p.Profile, allowInteractive: true, CancellationToken.None)
                    : await EntraAuth.TrySilentAsync(p.Profile, CancellationToken.None);
            }
            catch (Exception e) when (e is AuthException or HttpRequestException or TaskCanceledException)
            {
                if (interactive)
                {
                    ShowMain();
                    ConfirmDialog.Ask(this, "Sign-in failed", "Could not sign in", e.Message, "OK");
                }
                return;
            }
            if (token is null)
            {
                // Auto-connect at startup without a cached session: never open a browser unasked.
                _signInNeeded.Add(p.Id);
                Notify($"{p.DisplayName}: sign in to connect", "Open P2S VPN and turn the connection on to sign in.", ToolTipIcon.Info);
                return;
            }
            _signInNeeded.Remove(p.Id);
            _busy[p.Id] = "Starting";
            RenderAll();
            var r = await Ipc.CallAsync(new IpcRequest { Cmd = "connect", Name = p.Id, ProfileXml = p.Xml, Token = token.AccessToken }, 90000);
            if (!r.Ok)
            {
                if (interactive) { ShowMain(); ConfirmDialog.Ask(this, "Could not connect", $"{p.DisplayName} did not connect", ServiceError(r.Error), "OK"); }
                else Notify($"{p.DisplayName} did not connect", ServiceError(r.Error), ToolTipIcon.Warning);
            }
        }
        finally
        {
            _busy.Remove(p.Id);
            await PollAsync();
        }
    }

    private async Task DisconnectAsync(StoredProfile p)
    {
        _busy[p.Id] = "Disconnecting";
        RenderAll();
        var r = await Ipc.CallAsync(new IpcRequest { Cmd = "disconnect", Name = p.Id });
        if (!r.Ok && r.Error != "That VPN is not running.")
            ConfirmDialog.Ask(this, AppPaths.AppName, "Could not disconnect", ServiceError(r.Error), "OK");
        _busy.Remove(p.Id);
        await PollAsync();
    }

    private async Task SignInAsync(StoredProfile p)
    {
        try
        {
            var t = await EntraAuth.InteractiveAsync(p.Profile, CancellationToken.None);
            _signInNeeded.Remove(p.Id);
            if (_status.GetValueOrDefault(p.Id) is { NeedToken: true })
                await Ipc.CallAsync(new IpcRequest { Cmd = "token", Name = p.Id, Token = t.AccessToken });
            ShowMain();
            RenderAll();
        }
        catch (Exception e) when (e is AuthException or HttpRequestException or TaskCanceledException)
        {
            ShowMain();
            ConfirmDialog.Ask(this, "Sign-in failed", "Could not sign in", e.Message, "OK");
        }
    }

    private static string ServiceError(string? e) => e == "SERVICE_UNAVAILABLE"
        ? "The P2S VPN service is not running. Restart the computer, or run the P2S VPN setup again.\n\n" + Setup.Installer.AntivirusHint
        : e ?? "Unknown error.";

    // ======================================================================== polling
    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var r = await Ipc.CallAsync(new IpcRequest { Cmd = "status" }, 4000);
            _serviceUp = r.Ok || r.Error != "SERVICE_UNAVAILABLE";
            if (r.Ok && r.Tunnels is not null)
            {
                _status = r.Tunnels.ToDictionary(t => t.Name);
                foreach (var t in r.Tunnels) await HandleTransitionsAsync(t);
                if (!_autoConnectDone) { _autoConnectDone = true; _ = AutoConnectAsync(); }
            }
            RenderAll();
            await RefreshLogAsync();
        }
        catch { }
        finally { _polling = false; }
    }

    private async Task AutoConnectAsync()
    {
        foreach (var p in _profiles.Where(p => _settings.IsAutoConnect(p.Id) && !IsRunning(p.Id)).ToList())
            await ConnectAsync(p, interactive: false);
    }

    private async Task HandleTransitionsAsync(TunnelStatus t)
    {
        var prev = _lastState.GetValueOrDefault(t.Name);
        var now = t.State + (t.LastError is null ? "" : "!");
        _lastState[t.Name] = now;
        var name = _profiles.FirstOrDefault(p => p.Id == t.Name)?.DisplayName ?? t.Name;
        if (prev is not null && prev != now)
        {
            if (t.State == "CONNECTED") Notify($"{name} is connected", $"Address {t.LocalIp}", ToolTipIcon.Info);
            else if (t.State is "DISCONNECTED" or "FAILED" && prev.StartsWith("CONNECTED"))
                Notify($"{name} disconnected", t.LastError ?? "The VPN connection was closed.", t.LastError is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
            else if (t.State == "FAILED") Notify($"{name} could not connect", t.LastError ?? "See the activity log.", ToolTipIcon.Error);
        }

        // A reconnect needs a fresh token: silently if possible, else ask the user.
        if (t.NeedToken && !_tokenInFlight.Contains(t.Name) && _profiles.FirstOrDefault(p => p.Id == t.Name) is { } prof)
        {
            _tokenInFlight.Add(t.Name);
            try
            {
                var tok = await EntraAuth.TrySilentAsync(prof.Profile, CancellationToken.None);
                if (tok is not null) await Ipc.CallAsync(new IpcRequest { Cmd = "token", Name = t.Name, Token = tok.AccessToken });
                else if (_signInNeeded.Add(t.Name))
                    Notify($"{name}: sign-in needed", "Click to open P2S VPN and sign in again to reconnect.", ToolTipIcon.Warning);
            }
            catch { }
            finally { _tokenInFlight.Remove(t.Name); }
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (!_settings.Notifications) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(4000);
    }

    // ======================================================================== rendering
    private (Health health, string text) StateOf(string id)
    {
        var s = _status.GetValueOrDefault(id);
        if (_busy.TryGetValue(id, out var msg)) return (Health.Busy, msg);
        if (_signInNeeded.Contains(id) && (s is null || !s.Running || s.NeedToken)) return (Health.Error, "Sign-in needed");
        return (UiKit.HealthOf(s), UiKit.Friendly(s));
    }

    private void RenderAll()
    {
        int up = 0, busy = 0;
        foreach (var card in _cards.Controls.OfType<ConnectionCard>())
        {
            var p = _profiles.FirstOrDefault(x => x.Id == card.Profile.Id) ?? card.Profile;
            var s = _status.GetValueOrDefault(p.Id);
            var (h, text) = StateOf(p.Id);
            var isBusy = h == Health.Busy;
            card.Update(p, h, text, s?.State == "CONNECTED" ? s.LocalIp : null,
                on: s?.Running == true && !isBusy, busy: isBusy, auto: _settings.IsAutoConnect(p.Id), selected: p.Id == _selectedId);
            if (h == Health.Up) up++;
            if (isBusy) busy++;
        }

        _cardsCaption.Text = $"CONNECTIONS  ·  {_profiles.Count}";
        _summary.Text = _profiles.Count == 0 ? "Add a connection to get started"
            : up == 0 ? "Not connected" : $"{up} of {_profiles.Count} connected";
        _tray.Icon = UiKit.MakeIcon(up > 0 ? Theme.Green : busy > 0 ? Theme.Amber : Theme.Gray);
        var tip = up > 0 ? $"{AppPaths.AppName}: {up} connected" : AppPaths.AppName;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _svcLabel.Text = _serviceUp ? "●  Service running"
            : "●  The P2S VPN service is not running. Antivirus (e.g. Kaspersky) may be blocking it: allow C:\\Program Files\\P2SVpn, then run setup again.";
        _svcLabel.ForeColor = _serviceUp ? Theme.Muted : Theme.Red;

        RenderDetail();
    }

    private void RenderDetail()
    {
        if (Selected() is not { } p) return;
        var s = _status.GetValueOrDefault(p.Id);
        var (h, text) = StateOf(p.Id);
        var running = s?.Running == true;
        var busy = _busy.ContainsKey(p.Id);

        if (_name.Text != p.DisplayName) _name.Text = p.DisplayName;
        _statusPill.Set(text, Theme.Soft(h), Theme.Strong(h) == Theme.Gray ? Theme.Muted : Theme.Strong(h), Theme.Strong(h));
        _rename.Left = _statusPill.Right + Theme.S(this, 8);

        _primary.Text = running ? "Disconnect" : "Connect";
        _primary.Kind = running ? ButtonKind.Danger : ButtonKind.Primary;
        _primary.Enabled = _serviceUp && !busy;

        var sub = h switch
        {
            Health.Up => "Connected. The networks behind this VPN are reachable from every app on this PC.",
            Health.Busy => s?.Detail is { Length: > 0 } d && !d.StartsWith("ERROR") ? $"Working on it: {d}" : "Working on it...",
            Health.Error => _signInNeeded.Contains(p.Id) ? "Your sign-in expired. Click Connect (or Sign in again) to continue."
                          : s?.LastError ?? "Something went wrong. The activity log below has the details.",
            _ => "Turn this connection on to sign in with your Microsoft work account and connect.",
        };
        if (_subline.Text != sub) _subline.Text = sub;

        _tAddress.SetValue(s?.State == "CONNECTED" ? s.LocalIp : null);
        _tSince.SetValue(s?.ConnectedSinceUtc is { } since ? UiKit.Duration(DateTime.UtcNow - since) : null);
        _tTraffic.SetValue(s is { BytesIn: > 0 } || s is { BytesOut: > 0 } ? $"↓ {UiKit.Bytes(s.BytesIn)}   ↑ {UiKit.Bytes(s.BytesOut)}" : null);
        _tAccount.SetValue(EntraAuth.CachedAccount(p.Profile) ?? "Not signed in yet");
        _tDns.SetValue(s is { Dns.Count: > 0 } ? $"{string.Join(", ", s.Dns)}  ({s.DnsRules} domains)" : s?.State == "CONNECTED" ? "None from this VPN" : null);
        _tGateway.SetValue(p.Profile.Gateways[0], $"{p.Profile.VpnName}\n{p.Profile.Gateways[0]}");

        var routes = s is { Routes.Count: > 0 } ? s.Routes : [];
        var current = _routes.Controls.OfType<Pill>().Select(c => c.Text).ToList();
        if (!current.SequenceEqual(routes.Count > 0 ? routes : ["Shown once connected"]))
        {
            _routes.SuspendLayout();
            foreach (Control c in _routes.Controls.Cast<Control>().ToList()) { _routes.Controls.Remove(c); c.Dispose(); }
            if (routes.Count == 0)
            {
                var pill = new Pill();
                pill.Set("Shown once connected", Theme.GraySoft, Theme.Subtle, Color.Empty);
                _routes.Controls.Add(pill);
            }
            foreach (var r in routes)
            {
                var pill = new Pill { Font = Theme.Mono };
                pill.Set(r, Theme.AccentSoft, Theme.Accent, Color.Empty);
                _routes.Controls.Add(pill);
            }
            _routes.ResumeLayout();
        }

        _autoSwitch.SetSilently(_settings.IsAutoConnect(p.Id));
        _signIn.Enabled = !busy;
    }

    private async Task RefreshLogAsync()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || Selected() is not { } p) return;
        if (++_logTick < 2) return;
        _logTick = 0;
        var r = await Ipc.CallAsync(new IpcRequest { Cmd = "log", Name = p.Id, Lines = 400 });
        var text = r.Ok && r.Log is { Count: > 0 } ? string.Join("\r\n", r.Log)
            : r.Ok ? "Nothing here yet. The OpenVPN log appears once you connect." : ServiceError(r.Error);
        if (_log.Text == text) return;
        _log.Text = text;
        if (_follow.Checked) { _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
    }

    // ======================================================================== window / tray
    public void ShowMain()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Notify(AppPaths.AppName, "Still running in the notification area. Your connections stay up.", ToolTipIcon.Info);
        }
    }

    private async Task ExitAsync()
    {
        var running = _status.Values.Where(s => s.Running).ToList();
        if (running.Count > 0)
        {
            ShowMain();
            if (ConfirmDialog.Ask(this, "Exit P2S VPN", $"Disconnect {running.Count} connection(s) and exit?",
                    "Without the app, a VPN that drops cannot renew your sign-in and reconnect. " +
                    "To keep connections up, just close the window instead - P2S VPN stays in the notification area.",
                    "Disconnect and exit", danger: true))
            {
                foreach (var s in running) await Ipc.CallAsync(new IpcRequest { Cmd = "disconnect", Name = s.Name });
            }
            else return;
        }
        _exiting = true;
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}
