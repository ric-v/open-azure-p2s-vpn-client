using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace P2SVpn.App;

internal enum ButtonKind { Primary, Secondary, Danger, Ghost, DangerGhost }

/// <summary>Rounded, flat button with hover/pressed states.</summary>
internal sealed class ModernButton : Control, IButtonControl
{
    private bool _hover, _down;
    private ButtonKind _kind;
    public DialogResult DialogResult { get; set; }

    public ModernButton(string text, ButtonKind kind = ButtonKind.Secondary)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Theme.BodyBold;
        Cursor = Cursors.Hand;
        TabStop = true;
        _kind = kind;
        Text = text;
        Margin = new Padding(0, 0, 8, 0);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => base.Text; set { base.Text = value; Fit(); Invalidate(); } }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }

    private void Fit()
    {
        var sz = TextRenderer.MeasureText(Text ?? "", Font);
        Size = new Size(sz.Width + Theme.S(this, 32), Font.Height + Theme.S(this, 16));
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill, fg, border;
        switch (_kind)
        {
            case ButtonKind.Primary:
                fill = _down ? Theme.AccentHover : _hover ? Theme.AccentHover : Theme.Accent; fg = Color.White; border = fill; break;
            case ButtonKind.Danger:
                fill = _hover || _down ? Theme.RedHover : Theme.Red; fg = Color.White; border = fill; break;
            case ButtonKind.Ghost:
                fill = _hover ? Theme.AccentSoft : Color.Transparent; fg = Theme.Accent; border = Color.Transparent; break;
            case ButtonKind.DangerGhost:
                fill = _hover ? Theme.RedSoft : Color.Transparent; fg = Theme.Red; border = Color.Transparent; break;
            default:
                fill = _down ? Theme.GraySoft : _hover ? Theme.SurfaceHover : Theme.Surface; fg = Theme.Text; border = _hover ? Theme.BorderStrong : Theme.Border; break;
        }
        if (!Enabled)
        {
            fill = _kind is ButtonKind.Ghost or ButtonKind.DangerGhost ? Color.Transparent : Theme.GraySoft;
            fg = Theme.Subtle; border = _kind is ButtonKind.Ghost or ButtonKind.DangerGhost ? Color.Transparent : Theme.Border;
        }
        using (var path = Theme.Round(r, Theme.S(this, 8f)))
        {
            if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border.A > 0) using (var p = new Pen(border)) g.DrawPath(p, path);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Color.FromArgb(120, Theme.Accent), 2f)) g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>iOS-style on/off switch.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(44, 24);
        Margin = new Padding(0, 2, 10, 0);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Size = new Size(Theme.S(this, 44), Theme.S(this, 24)); }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Change the state without raising CheckedChanged.</summary>
    public void SetSilently(bool value) { if (_checked != value) { _checked = value; Invalidate(); } }

    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }

    protected override void OnPaint(PaintEventArgs e) =>
        Draw(e.Graphics, ClientRectangle, _checked, Enabled ? (_checked ? Theme.Green : Theme.Track) : Theme.Border, this);

    public static void Draw(Graphics g, Rectangle bounds, bool on, Color track, Control dpi)
    {
        Theme.Smooth(g);
        var r = new RectangleF(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        using (var path = Theme.Round(r, r.Height / 2))
        using (var b = new SolidBrush(track)) g.FillPath(b, path);
        float pad = Theme.S(dpi, 3f);
        float d = r.Height - pad * 2;
        float x = on ? r.Right - pad - d : r.X + pad;
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(shadow, x, r.Y + pad + 1, d, d);
        g.FillEllipse(Brushes.White, x, r.Y + pad, d, d);
    }
}

/// <summary>White card with rounded corners and a hairline border.</summary>
internal class RoundedPanel : Panel
{
    public Color Fill { get; set; } = Theme.Surface;
    public Color Stroke { get; set; } = Theme.Border;
    public int Radius { get; set; } = 12;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.BackOf(Parent));
        Theme.Smooth(e.Graphics);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, Theme.S(this, (float)Radius));
        using (var b = new SolidBrush(Fill)) e.Graphics.FillPath(b, path);
        if (Stroke.A > 0) using (var p = new Pen(Stroke)) e.Graphics.DrawPath(p, path);
    }
}

/// <summary>Small rounded "pill" with a dot: status badges and route chips.</summary>
internal sealed class Pill : Control
{
    private Color _fill = Theme.GraySoft, _fg = Theme.Text, _dot = Color.Empty;

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.Small;
        Margin = new Padding(0, 0, 6, 6);
    }

    public void Set(string text, Color fill, Color fg, Color dot)
    {
        if (Text == text && _fill == fill && _fg == fg && _dot == dot) return;
        Text = text; _fill = fill; _fg = fg; _dot = dot;
        var sz = TextRenderer.MeasureText(text, Font);
        int dotW = dot.IsEmpty ? 0 : Theme.S(this, 14);
        Size = new Size(sz.Width + Theme.S(this, 18) + dotW, Font.Height + Theme.S(this, 8));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, r.Height / 2))
        using (var b = new SolidBrush(_fill)) g.FillPath(b, path);
        int x = Theme.S(this, 9);
        if (!_dot.IsEmpty)
        {
            float d = Theme.S(this, 7f);
            using var db = new SolidBrush(_dot);
            g.FillEllipse(db, x, (Height - d) / 2f, d, d);
            x += Theme.S(this, 14);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), _fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>Caption + value tile used in the details grid.</summary>
internal sealed class InfoTile : RoundedPanel
{
    private readonly Label _caption = new() { AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Muted, BackColor = Color.Transparent };
    private readonly Label _value = new() { AutoEllipsis = true, Font = Theme.Value, ForeColor = Theme.Text, BackColor = Color.Transparent, UseMnemonic = false };
    private readonly ToolTip _tip = new();

    public InfoTile(string caption)
    {
        Fill = Theme.GraySoft;
        Stroke = Color.Transparent;
        Radius = 10;
        Padding = new Padding(12, 8, 12, 8);
        Margin = new Padding(0, 0, 10, 10);
        Dock = DockStyle.Fill;
        _caption.Text = caption.ToUpperInvariant();
        _caption.Location = new Point(12, 9);
        Controls.Add(_caption);
        Controls.Add(_value);
        Resize += (_, _) => Layout2();
        HandleCreated += (_, _) => Layout2();
    }

    private void Layout2()
    {
        _caption.Location = new Point(Theme.S(this, 12), Theme.S(this, 9));
        _value.Location = new Point(Theme.S(this, 12), _caption.Bottom + Theme.S(this, 3));
        _value.Size = new Size(Math.Max(10, Width - Theme.S(this, 24)), Theme.Value.Height + 4);
    }

    public void SetValue(string? value, string? tooltip = null)
    {
        var v = string.IsNullOrEmpty(value) ? "—" : value;
        if (_value.Text != v) _value.Text = v;
        _value.ForeColor = string.IsNullOrEmpty(value) ? Theme.Subtle : Theme.Text;
        _tip.SetToolTip(_value, tooltip ?? (v.Length > 24 ? v : null));
    }
}
