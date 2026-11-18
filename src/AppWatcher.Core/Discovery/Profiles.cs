using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppWatcher.Core.Discovery;

/// <summary>
/// An app profile: what to expect from one program or one toolkit, so the next site is a one-click setup.
/// Built-in profiles are embedded (profiles/*.json in the repository); users add their own under
/// %ProgramData%\AppWatcher\profiles\ or export the current settings as one.
/// </summary>
public sealed class AppProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ProfileMatch Match { get; set; } = new();
    public List<string> EffectTriggers { get; set; } = new();
    public List<string> IgnoreWindows { get; set; } = new();
    public List<string> SessionWindows { get; set; } = new();
    public List<string> DialogWindows { get; set; } = new();
    public List<string> ExcludeWindows { get; set; } = new();
    public bool? TabDocuments { get; set; }
    public bool? MsaaFallback { get; set; }
    public bool? PressInference { get; set; }
    public int? DumpDepth { get; set; }
    public int? DumpRowCap { get; set; }
    public int? GridRowCap { get; set; }
    public bool? MaskTitles { get; set; }
    /// <summary>Plain-words note shown at setup.</summary>
    public string? Note { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true };

    /// <summary>Applies the profile onto a config (profile values win where set; lists are merged).</summary>
    public void ApplyTo(WatcherConfig cfg)
    {
        cfg.Profile = Name;
        foreach (var t in EffectTriggers) if (!cfg.EffectTriggers.Contains(t, StringComparer.OrdinalIgnoreCase)) cfg.EffectTriggers.Add(t);
        foreach (var w in IgnoreWindows) if (!cfg.Collector.IgnoreWindows.Contains(w)) cfg.Collector.IgnoreWindows.Add(w);
        foreach (var w in SessionWindows) if (!cfg.Collector.SessionWindows.Contains(w)) cfg.Collector.SessionWindows.Add(w);
        foreach (var w in DialogWindows) if (!cfg.Collector.DialogWindows.Contains(w)) cfg.Collector.DialogWindows.Add(w);
        foreach (var w in ExcludeWindows) if (!cfg.Privacy.ExcludeWindows.Contains(w)) cfg.Privacy.ExcludeWindows.Add(w);
        if (TabDocuments is not null) cfg.Collector.TabDocuments = TabDocuments.Value;
        if (MsaaFallback is not null) cfg.Collector.MsaaFallback = MsaaFallback.Value;
        if (PressInference is not null) cfg.Collector.PressInference = PressInference.Value;
        if (DumpDepth is not null) cfg.Collector.DumpDepth = DumpDepth.Value;
        if (DumpRowCap is not null) cfg.Collector.DumpRowCap = DumpRowCap.Value;
        if (GridRowCap is not null) cfg.Collector.GridRowCap = GridRowCap.Value;
        if (MaskTitles is not null) cfg.Privacy.MaskTitles = MaskTitles.Value;
    }

    /// <summary>The current settings as a profile the user can share.</summary>
    public static AppProfile FromConfig(WatcherConfig cfg, string name, string? toolkit)
    {
        var p = new AppProfile
        {
            Name = name, Description = $"Exported from App Watcher settings for {cfg.App?.DisplayName}",
            EffectTriggers = cfg.EffectTriggers.Where(t => !WatcherConfig.DefaultTriggers.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList(),
            IgnoreWindows = cfg.Collector.IgnoreWindows.ToList(), SessionWindows = cfg.Collector.SessionWindows.ToList(), DialogWindows = cfg.Collector.DialogWindows.ToList(),
            ExcludeWindows = cfg.Privacy.ExcludeWindows.ToList(),
            TabDocuments = cfg.Collector.TabDocuments, MsaaFallback = cfg.Collector.MsaaFallback, PressInference = cfg.Collector.PressInference,
            DumpDepth = cfg.Collector.DumpDepth, DumpRowCap = cfg.Collector.DumpRowCap, GridRowCap = cfg.Collector.GridRowCap, MaskTitles = cfg.Privacy.MaskTitles,
        };
        if (cfg.App is not null) p.Match.Exe.Add(cfg.App.ProcessName.ToLowerInvariant());
        if (toolkit is not null) p.Match.Toolkit.Add(toolkit);
        return p;
    }
}

public sealed class ProfileMatch
{
    /// <summary>Process names without .exe, lower case.</summary>
    public List<string> Exe { get; set; } = new();
    /// <summary>Product names (file version info), substring match.</summary>
    public List<string> Product { get; set; } = new();
    /// <summary>Toolkit names from ToolkitDetector, substring match.</summary>
    public List<string> Toolkit { get; set; } = new();
}

public static class ProfileStore
{
    public static string UserFolder => Path.Combine(Paths.Root, "profiles");

    /// <summary>Built-in profiles (embedded) plus the user's own folder; user profiles win on equal names.</summary>
    public static List<AppProfile> All()
    {
        var list = new List<AppProfile>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("profiles/") && n.EndsWith(".json")))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(res)!;
                var p = JsonSerializer.Deserialize<AppProfile>(s, AppProfile.JsonOptions);
                if (p is not null && p.Name.Length > 0) list.Add(p);
            }
            catch (Exception ex) { Log.Warn($"built-in profile {res} unreadable: {ex.Message}"); }
        }
        try
        {
            if (Directory.Exists(UserFolder))
                foreach (var f in Directory.GetFiles(UserFolder, "*.json"))
                {
                    var p = JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(f), AppProfile.JsonOptions);
                    if (p is null || p.Name.Length == 0) continue;
                    list.RemoveAll(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    list.Add(p);
                }
        }
        catch (Exception ex) { Log.Warn("user profiles unreadable: " + ex.Message); }
        return list;
    }

    public static AppProfile? ByName(string? name) => name is null ? null : All().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Best profile for an exe: exact process name, then product name, then toolkit, then the generic one.</summary>
    public static AppProfile? Match(string? exePath, string processName, string? product, string? toolkit)
    {
        var all = All();
        var proc = processName.ToLowerInvariant();
        var hit = all.FirstOrDefault(p => p.Match.Exe.Any(e => e.Equals(proc, StringComparison.OrdinalIgnoreCase)));
        if (hit is not null) return hit;
        if (!string.IsNullOrEmpty(product)) hit = all.FirstOrDefault(p => p.Match.Product.Any(x => product.Contains(x, StringComparison.OrdinalIgnoreCase)));
        if (hit is not null) return hit;
        if (!string.IsNullOrEmpty(toolkit)) hit = all.FirstOrDefault(p => p.Match.Toolkit.Any(x => toolkit.Contains(x, StringComparison.OrdinalIgnoreCase)));
        return hit ?? all.FirstOrDefault(p => p.Name == "generic");
    }

    public static string Save(AppProfile p)
    {
        Directory.CreateDirectory(UserFolder);
        var path = Path.Combine(UserFolder, Paths.SafeName(p.Name) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, AppProfile.JsonOptions));
        return path;
    }
}
