using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace P2SVpn.App;

internal enum Health { Off, Busy, Up, Error }

internal static class UiKit
{
    public static readonly Color Accent = Color.FromArgb(0, 103, 192);
    public static readonly Color Green = Color.FromArgb(16, 124, 16);
    public static readonly Color Amber = Color.FromArgb(202, 129, 0);
    public static readonly Color Red = Color.FromArgb(196, 43, 28);
    public static readonly Color Gray = Color.FromArgb(118, 118, 118);
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);

    public static Color ColorOf(Health h) => h switch
    {
        Health.Up => Green, Health.Busy => Amber, Health.Error => Red, _ => Gray,
    };

    public static Health HealthOf(TunnelStatus? s)
    {
        if (s is null) return Health.Off;
        return s.State switch
        {
            "CONNECTED" => Health.Up,
            "FAILED" => Health.Error,
            "DISCONNECTED" => s.LastError is null ? Health.Off : Health.Error,
            _ => s.Running ? Health.Busy : Health.Off,
        };
    }

    public static string Friendly(TunnelStatus? s)
    {
        if (s is null) return "Disconnected";
        if (s.NeedToken) return "Waiting for sign-in";
        return s.State switch
        {
            "STARTING" => "Starting",
            "CONNECTING" or "TCP_CONNECT" => "Connecting",
            "RESOLVE" => "Resolving gateway",
            "WAIT" => "Waiting for gateway",
            "AUTH" => "Authenticating",
            "GET_CONFIG" => "Getting configuration",
            "ASSIGN_IP" => "Assigning address",
            "ADD_ROUTES" => "Adding routes",
            "CONNECTED" => "Connected",
            "RECONNECTING" => "Reconnecting",
            "EXITING" => "Disconnecting",
            "FAILED" => "Failed",
            _ => "Disconnected",
        };
    }

    public static string Bytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.0} {u[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    // ---- application icon (app.ico, embedded) -----------------------------------
    private static Icon? _appIcon;

    private static Stream? AppIconStream() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");

    /// <summary>The multi-size app icon for windows and dialogs.</summary>
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;
            using var s = AppIconStream();
            _appIcon = s is null ? MakeIcon(Theme.Accent) : new Icon(s);
            return _appIcon;
        }
    }

    public static Bitmap AppIconBitmap(int size)
    {
        using var s = AppIconStream();
        if (s is null) return MakeIcon(Theme.Accent, size).ToBitmap();
        using var ic = new Icon(s, size, size);
        return ic.ToBitmap();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static readonly Dictionary<(Color, int), Icon> IconCache = new();

    /// <summary>A shield with a check-ish keyhole, drawn at runtime (no icon files to ship).</summary>
    public static Icon MakeIcon(Color color, int size = 32)
    {
        if (IconCache.TryGetValue((color, size), out var cached)) return cached;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            using var path = new GraphicsPath();
            path.AddLine(s * 0.50f, s * 0.04f, s * 0.92f, s * 0.20f);
            path.AddBezier(s * 0.92f, s * 0.20f, s * 0.92f, s * 0.62f, s * 0.75f, s * 0.82f, s * 0.50f, s * 0.97f);
            path.AddBezier(s * 0.50f, s * 0.97f, s * 0.25f, s * 0.82f, s * 0.08f, s * 0.62f, s * 0.08f, s * 0.20f);
            path.CloseFigure();
            using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, s * 0.38f, s * 0.30f, s * 0.24f, s * 0.24f);
            g.FillRectangle(white, s * 0.45f, s * 0.48f, s * 0.10f, s * 0.24f);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        IconCache[(color, size)] = icon;
        return icon;
    }

    public static Bitmap Dot(Color c, int size = 12)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        g.FillEllipse(b, 1, 1, size - 2, size - 2);
        return bmp;
    }

    public static Button FlatButton(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        if (primary)
        {
            b.BackColor = Accent; b.ForeColor = Color.White; b.FlatAppearance.BorderColor = Accent;
        }
        else
        {
            b.BackColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        }
        return b;
    }
}
