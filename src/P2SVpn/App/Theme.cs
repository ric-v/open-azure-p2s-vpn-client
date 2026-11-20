using System.Drawing.Drawing2D;

namespace P2SVpn.App;

/// <summary>Colours, fonts and drawing helpers for the whole app.</summary>
internal static class Theme
{
    static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static readonly Color Bg = Hex("#F4F6FA");
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceHover = Hex("#F8FAFC");
    public static readonly Color Border = Hex("#E2E8F0");
    public static readonly Color BorderStrong = Hex("#CBD5E1");
    public static readonly Color Text = Hex("#0F172A");
    public static readonly Color Muted = Hex("#64748B");
    public static readonly Color Subtle = Hex("#94A3B8");
    public static readonly Color Accent = Hex("#2563EB");
    public static readonly Color AccentHover = Hex("#1D4ED8");
    public static readonly Color AccentSoft = Hex("#EFF6FF");
    public static readonly Color Green = Hex("#16A34A");
    public static readonly Color GreenSoft = Hex("#DCFCE7");
    public static readonly Color Amber = Hex("#D97706");
    public static readonly Color AmberSoft = Hex("#FEF3C7");
    public static readonly Color Red = Hex("#DC2626");
    public static readonly Color RedHover = Hex("#B91C1C");
    public static readonly Color RedSoft = Hex("#FEE2E2");
    public static readonly Color Gray = Hex("#94A3B8");
    public static readonly Color GraySoft = Hex("#F1F5F9");
    public static readonly Color Track = Hex("#CBD5E1");

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Caption = new("Segoe UI", 8.25f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Value = new("Segoe UI Semibold", 10.5f);
    public static readonly Font H1 = new("Segoe UI Semibold", 18f);
    public static readonly Font H2 = new("Segoe UI Semibold", 13f);
    public static readonly Font AppTitle = new("Segoe UI Semibold", 14f);
    public static readonly Font Mono = new("Consolas", 9f);

    public static Color Strong(Health h) => h switch
    {
        Health.Up => Green, Health.Busy => Amber, Health.Error => Red, _ => Gray,
    };

    public static Color Soft(Health h) => h switch
    {
        Health.Up => GreenSoft, Health.Busy => AmberSoft, Health.Error => RedSoft, _ => GraySoft,
    };

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>The first solid background up the parent chain (Transparent would paint black).</summary>
    public static Color BackOf(Control? c)
    {
        for (; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Bg;
    }

    /// <summary>Scale a 96-dpi length to the control's DPI.</summary>
    public static int S(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96f);
    public static float S(Control c, float v) => v * c.DeviceDpi / 96f;
}
