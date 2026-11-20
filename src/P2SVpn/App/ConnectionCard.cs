namespace P2SVpn.App;

/// <summary>One VPN connection in the list: status dot, name, status line and an on/off switch.</summary>
internal sealed class ConnectionCard : Control
{
    private bool _hover, _hoverToggle;

    public StoredProfile Profile { get; private set; }
    public Health Health { get; private set; } = Health.Off;
    public string StatusText { get; private set; } = "Disconnected";
    public string? Address { get; private set; }
    public bool On { get; private set; }
    public bool Busy { get; private set; }
    public bool AutoConnect { get; private set; }
    public bool Selected { get; private set; }

    public event EventHandler? CardClicked;
    public event EventHandler? ToggleClicked;
    public event EventHandler<Point>? MenuRequested;

    public ConnectionCard(StoredProfile profile)
    {
        Profile = profile;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 0, 0, 8);
        Height = 72;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = Theme.S(this, 72);
    }

    public void Update(StoredProfile profile, Health h, string status, string? address, bool on, bool busy, bool auto, bool selected)
    {
        if (ReferenceEquals(Profile, profile) && Health == h && StatusText == status && Address == address &&
            On == on && Busy == busy && AutoConnect == auto && Selected == selected) return;
        Profile = profile; Health = h; StatusText = status; Address = address; On = on; Busy = busy; AutoConnect = auto; Selected = selected;
        AccessibleName = $"{profile.DisplayName}, {status}";
        Invalidate();
    }

    private Rectangle ToggleRect()
    {
        int w = Theme.S(this, 44), h = Theme.S(this, 24);
        return new Rectangle(Width - w - Theme.S(this, 16), (Height - h) / 2, w, h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var t = ToggleRect().Contains(e.Location);
        if (!_hover || t != _hoverToggle) { _hover = true; _hoverToggle = t; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = _hoverToggle = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Focus();
        if (e.Button == MouseButtons.Right) { CardClicked?.Invoke(this, EventArgs.Empty); MenuRequested?.Invoke(this, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        CardClicked?.Invoke(this, EventArgs.Empty);
        if (ToggleRect().Contains(e.Location)) ToggleClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { ToggleClicked?.Invoke(this, EventArgs.Empty); e.Handled = true; }
        else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift)) MenuRequested?.Invoke(this, new Point(Width / 2, Height / 2));
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { CardClicked?.Invoke(this, EventArgs.Empty); Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        Theme.Smooth(g);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = Selected ? Theme.AccentSoft : _hover ? Theme.SurfaceHover : Theme.Surface;
        var stroke = Selected ? Theme.Accent : _hover ? Theme.BorderStrong : Theme.Border;
        using (var path = Theme.Round(r, Theme.S(this, 12f)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(stroke, Selected ? 1.6f : 1f)) g.DrawPath(p, path);
        }

        // Status dot with a soft halo when connected / working.
        float cx = Theme.S(this, 22f), cy = Height / 2f;
        var strong = Theme.Strong(Health);
        if (Health is Health.Up or Health.Busy)
        {
            float hr = Theme.S(this, 9f);
            using var halo = new SolidBrush(Color.FromArgb(70, strong));
            g.FillEllipse(halo, cx - hr, cy - hr, hr * 2, hr * 2);
        }
        float dr = Theme.S(this, 5f);
        using (var dot = new SolidBrush(strong)) g.FillEllipse(dot, cx - dr, cy - dr, dr * 2, dr * 2);

        // Text.
        int left = Theme.S(this, 42);
        var toggle = ToggleRect();
        int textW = toggle.Left - left - Theme.S(this, 12);
        int titleH = Theme.CardTitle.Height;
        int subH = Theme.Small.Height;
        int top = (Height - titleH - subH - Theme.S(this, 2)) / 2;
        TextRenderer.DrawText(g, Profile.DisplayName, Theme.CardTitle, new Rectangle(left, top, textW, titleH), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var statusColor = Health switch { Health.Up => Theme.Green, Health.Error => Theme.Red, Health.Busy => Theme.Amber, _ => Theme.Muted };
        var line = StatusText + (Address is null ? "" : $"  ·  {Address}") + (AutoConnect ? "  ·  Auto" : "");
        TextRenderer.DrawText(g, line, Theme.Small, new Rectangle(left, top + titleH + Theme.S(this, 2), textW, subH), statusColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        // Switch: green when up, amber while working, grey when off.
        var track = Busy ? Theme.Amber : On ? Theme.Green : _hoverToggle ? Theme.Subtle : Theme.Track;
        ToggleSwitch.Draw(g, toggle, On || Busy, track, this);

        if (Focused && ShowFocusCues)
            using (var p = new Pen(Color.FromArgb(90, Theme.Accent), 2f))
            using (var path = Theme.Round(new RectangleF(2, 2, Width - 5, Height - 5), Theme.S(this, 10f)))
                g.DrawPath(p, path);
    }
}

/// <summary>Dashed drop target: empty state, and the overlay while dragging a file.</summary>
internal sealed class DropZone : Control
{
    public string Title { get; set; } = "Add your first VPN connection";
    public string Subtitle { get; set; } = "Drag the azurevpnconfig.xml, or the profile .zip from the Azure portal, here.";
    public bool Active { get; set; }
    public ModernButton Browse { get; } = new("Browse for a file", ButtonKind.Primary);

    public DropZone()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(Browse);
        Resize += (_, _) => PlaceButton();
    }

    private void PlaceButton() =>
        Browse.Location = new Point((Width - Browse.Width) / 2, Height / 2 + Theme.S(this, 56));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        Theme.Smooth(g);
        var pad = Theme.S(this, 8f);
        var r = new RectangleF(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
        using (var path = Theme.Round(r, Theme.S(this, 16f)))
        {
            using (var b = new SolidBrush(Active ? Theme.AccentSoft : Theme.Surface)) g.FillPath(b, path);
            using var p = new Pen(Active ? Theme.Accent : Theme.BorderStrong, Theme.S(this, 2f)) { DashPattern = [4f, 3f] };
            g.DrawPath(p, path);
        }

        // Icon: shield inside a soft circle.
        int cx = Width / 2, cy = Height / 2 - Theme.S(this, 50);
        int cr = Theme.S(this, 34);
        using (var circle = new SolidBrush(Theme.AccentSoft)) g.FillEllipse(circle, cx - cr, cy - cr, cr * 2, cr * 2);
        using (var icon = UiKit.MakeIcon(Theme.Accent, Theme.S(this, 40)).ToBitmap())
            g.DrawImage(icon, cx - icon.Width / 2, cy - icon.Height / 2);

        var titleRect = new Rectangle(0, cy + cr + Theme.S(this, 14), Width, Theme.H2.Height + 4);
        TextRenderer.DrawText(g, Title, Theme.H2, titleRect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        var subRect = new Rectangle(Theme.S(this, 40), titleRect.Bottom + Theme.S(this, 4), Width - Theme.S(this, 80), Theme.Body.Height * 2 + 4);
        TextRenderer.DrawText(g, Subtitle, Theme.Body, subRect, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        PlaceButton();
    }
}
