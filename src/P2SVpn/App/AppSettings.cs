using System.Text.Json;
using Microsoft.Win32;

namespace P2SVpn.App;

/// <summary>Per-user preferences in %LOCALAPPDATA%\P2SVpn\settings.json (start-at-sign-in lives in the Run key).</summary>
internal sealed class AppSettings
{
    public bool StartMinimized { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public List<string> AutoConnect { get; set; } = [];
    public bool FirstRunDone { get; set; }

    private static string File => Path.Combine(AppPaths.UserDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public bool IsAutoConnect(string id) => AutoConnect.Contains(id);

    public void SetAutoConnect(string id, bool on)
    {
        AutoConnect.Remove(id);
        if (on) AutoConnect.Add(id);
        Save();
    }

    // ---- start when the user signs in (HKCU Run) -------------------------------
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(AppPaths.AppName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(AppPaths.AppName, $"\"{AppPaths.InstalledExe}\" --autostart");
            else k.DeleteValue(AppPaths.AppName, false);
        }
    }

    /// <summary>First launch for this user: start-at-sign-in is on by default.</summary>
    public void ApplyFirstRunDefaults()
    {
        if (FirstRunDone) return;
        try { StartWithWindows = true; } catch { }
        FirstRunDone = true;
        Save();
    }
}
