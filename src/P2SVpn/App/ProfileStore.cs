using System.IO.Compression;

namespace P2SVpn.App;

internal sealed class StoredProfile
{
    public required string Id { get; init; }
    public required string Xml { get; init; }
    public required AzureProfile Profile { get; init; }
    /// <summary>User-chosen name (defaults to the VPN name in the profile).</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>Set on import when the name clashed with an existing profile.</summary>
    public bool SuggestRename { get; init; }
}

/// <summary>Imported profiles live in %LOCALAPPDATA%\P2SVpn\profiles\&lt;id&gt;.xml (original file kept as is).</summary>
internal static class ProfileStore
{
    private static string NamesFile => Path.Combine(AppPaths.ProfilesDir, "names.json");

    private static Dictionary<string, string> LoadNames()
    {
        try
        {
            return File.Exists(NamesFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NamesFile)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static void SaveNames(Dictionary<string, string> names)
    {
        Directory.CreateDirectory(AppPaths.ProfilesDir);
        File.WriteAllText(NamesFile, System.Text.Json.JsonSerializer.Serialize(names, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public const int MaxNameLength = 60;

    /// <summary>Returns an error message, or null when the name was saved.</summary>
    public static string? Rename(string id, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return "The name cannot be empty.";
        if (newName.Length > MaxNameLength) return $"Keep the name under {MaxNameLength} characters.";
        if (newName.Any(char.IsControl)) return "The name contains invalid characters.";
        if (Load().Any(p => p.Id != id && p.DisplayName.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            return $"Another connection is already called \"{newName}\".";
        var names = LoadNames();
        names[id] = newName;
        SaveNames(names);
        return null;
    }

    public static List<StoredProfile> Load()
    {
        var list = new List<StoredProfile>();
        if (!Directory.Exists(AppPaths.ProfilesDir)) return list;
        var names = LoadNames();
        foreach (var f in Directory.GetFiles(AppPaths.ProfilesDir, "*.xml"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (!AppPaths.IsValidProfileName(id)) continue;
            try
            {
                var xml = File.ReadAllText(f);
                var profile = AzureProfile.Parse(xml);
                list.Add(new StoredProfile
                {
                    Id = id, Xml = xml, Profile = profile,
                    DisplayName = names.TryGetValue(id, out var n) && n.Length > 0 ? n : profile.VpnName,
                });
            }
            catch { /* skip broken files */ }
        }
        return list.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Accepts an azurevpnconfig*.xml, or the profile .zip downloaded from Azure.</summary>
    public static List<StoredProfile> Import(string path)
    {
        var xmls = new List<string>();
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries)
            {
                // The Azure download carries the Entra profile under AzureVPN\.
                if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Name.StartsWith("azurevpnconfig", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Length > 512 * 1024) continue;
                using var sr = new StreamReader(e.Open());
                xmls.Add(sr.ReadToEnd());
            }
            if (xmls.Count == 0) throw new ProfileException("No azurevpnconfig.xml found in that zip.");
        }
        else
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new ProfileException("That file is too large to be an Azure VPN profile.");
            xmls.Add(File.ReadAllText(path));
        }

        var result = new List<StoredProfile>();
        Exception? last = null;
        foreach (var xml in xmls)
        {
            try { result.Add(Save(xml)); }
            catch (ProfileException e) { last = e; }
        }
        if (result.Count == 0) throw last ?? new ProfileException("No usable profile in that file.");
        return result;
    }

    private static StoredProfile Save(string xml)
    {
        var profile = AzureProfile.Parse(xml);
        Directory.CreateDirectory(AppPaths.ProfilesDir);

        // Same gateway already imported: replace it in place, keep its name.
        var all = Load();
        foreach (var existing in all)
        {
            if (existing.Profile.Gateways.SequenceEqual(profile.Gateways))
            {
                File.WriteAllText(Path.Combine(AppPaths.ProfilesDir, existing.Id + ".xml"), xml);
                return new StoredProfile { Id = existing.Id, Xml = xml, Profile = profile, DisplayName = existing.DisplayName };
            }
        }

        var baseId = profile.SuggestedId();
        var id = baseId;
        for (int i = 2; File.Exists(Path.Combine(AppPaths.ProfilesDir, id + ".xml")); i++) id = $"{baseId}-{i}";
        File.WriteAllText(Path.Combine(AppPaths.ProfilesDir, id + ".xml"), xml);

        // A different gateway with the same VPN name: give it a distinct name and
        // let the user pick a better one.
        var name = profile.VpnName;
        var clash = all.Any(p => p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            name = Enumerable.Range(2, 100).Select(i => $"{profile.VpnName} ({i})")
                .First(n => !all.Any(p => p.DisplayName.Equals(n, StringComparison.OrdinalIgnoreCase)));
            var names = LoadNames();
            names[id] = name;
            SaveNames(names);
        }
        return new StoredProfile { Id = id, Xml = xml, Profile = profile, DisplayName = name, SuggestRename = clash };
    }

    public static void Remove(string id)
    {
        if (!AppPaths.IsValidProfileName(id)) return;
        try { File.Delete(Path.Combine(AppPaths.ProfilesDir, id + ".xml")); } catch { }
        var names = LoadNames();
        if (names.Remove(id)) SaveNames(names);
    }
}
