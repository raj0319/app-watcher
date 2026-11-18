using System.Text;

namespace AppWatcher.Core;

/// <summary>Where App Watcher keeps its own files. Never points into the watched app's folders.</summary>
public static class Paths
{
    /// <summary>Tests and portable runs can redirect the whole store.</summary>
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppWatcher");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string LogFile => Path.Combine(Root, "watcher.log");
    public static string DataRoot => Path.Combine(Root, "data");

    public static AppPaths ForApp(string appName) => new(Path.Combine(DataRoot, SafeName(appName)));

    /// <summary>Turns any title or name into a safe file name.</summary>
    public static string SafeName(string? s, int maxLen = 80)
    {
        if (string.IsNullOrWhiteSpace(s)) return "untitled";
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        var name = sb.ToString().Trim('-');
        while (name.Contains("--")) name = name.Replace("--", "-");
        if (name.Length == 0) name = "untitled";
        if (name.Length > maxLen) name = name[..maxLen].TrimEnd('-');
        return name;
    }
}

public sealed record AppPaths(string Root)
{
    public string Events => Path.Combine(Root, "events");
    public string Screens => Path.Combine(Root, "screens");
    public string Effects => Path.Combine(Root, "effects");
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Memory => Path.Combine(Root, "memory");
    public string Export => Path.Combine(Root, "export");
    public string BeforeSnapshot => Path.Combine(Snapshots, "before.json");
    public string HotTables => Path.Combine(Snapshots, "hot-tables.json");

    public void EnsureDirectories()
    {
        foreach (var d in new[] { Root, Events, Screens, Effects, Snapshots, Memory, Export })
            Directory.CreateDirectory(d);
    }
}
