using AppWatcher.Core.Sources;

namespace AppWatcher.Core.Discovery;

public sealed class DataCandidate
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "file";
    public long Size { get; set; }
    public string Why { get; set; } = "";
}

/// <summary>Looks for the watched app's data files in the usual places. Read only, bounded, never opens other apps' data.</summary>
public static class DataSourceScanner
{
    public static readonly string[] DataExtensions =
        { ".db", ".sqlite", ".sqlite3", ".db3", ".s3db", ".mdb", ".accdb", ".fdb", ".gdb", ".mdf", ".dbf", ".xml", ".json", ".csv", ".ini", ".dat" };

    /// <summary>Files that describe how a program starts (.NET deps and runtimeconfig, app manifests), not its business data.</summary>
    public static bool IsProgramFile(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        return n.EndsWith(".deps.json") || n.EndsWith(".runtimeconfig.json") || n.EndsWith(".runtimeconfig.dev.json")
            || n.EndsWith(".exe.manifest") || n.EndsWith(".dll.manifest") || n == "web.config";
    }

    public static List<DataCandidate> Scan(string? exePath, string processName, string? displayName, int maxEntries = 30_000)
    {
        var found = new List<DataCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new[] { processName, displayName ?? "" }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.ToLowerInvariant()).Distinct().ToList();
        var budget = maxEntries;

        var roots = new List<(string path, string why, bool mustMatchName)>();
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            var dir = Path.GetDirectoryName(exePath)!;
            if (!IsSharedFolder(dir)) roots.Add((dir, "install folder", false));
        }
        foreach (var sf in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData,
                                   Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments })
        {
            var p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add((p, Path.GetFileName(p), true));
        }

        foreach (var (root, why, mustMatch) in roots)
        {
            IEnumerable<string> subdirs;
            try
            {
                subdirs = mustMatch
                    ? Directory.EnumerateDirectories(root).Where(d => names.Any(n => Path.GetFileName(d).ToLowerInvariant().Contains(n)))
                    : new[] { root };
            }
            catch { continue; }
            foreach (var d in subdirs)
            {
                Walk(d, 0, mustMatch ? 6 : 4, ref budget, found, seen, why);
                if (budget <= 0) break;
            }
            if (budget <= 0) break;
        }
        return found.OrderByDescending(f => f.Kind is "sqlite" or "access").ThenByDescending(f => f.Size).ToList();
    }

    private static void Walk(string dir, int depth, int maxDepth, ref int budget, List<DataCandidate> found, HashSet<string> seen, string why)
    {
        if (depth > maxDepth || budget <= 0) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }
        foreach (var e in entries)
        {
            if (--budget <= 0) return;
            try
            {
                if (Directory.Exists(e))
                {
                    var n = Path.GetFileName(e).ToLowerInvariant();
                    if (n is "node_modules" or "cache" or "caches" or "temp" or "tmp" or "logs" or "__pycache__") continue;
                    Walk(e, depth + 1, maxDepth, ref budget, found, seen, why);
                    continue;
                }
                var ext = Path.GetExtension(e);
                if (!DataExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                if (IsProgramFile(e)) continue;
                if (!seen.Add(e)) continue;
                var fi = new FileInfo(e);
                if (fi.Length == 0) continue;
                var kind = ext.ToLowerInvariant() switch
                {
                    ".mdb" or ".accdb" => "access",
                    ".mdf" => "sqlserver-file",
                    _ => SqliteSource.IsSqlite(e) ? "sqlite" : "file"
                };
                found.Add(new DataCandidate { Path = e, Kind = kind, Size = fi.Length, Why = why });
            }
            catch { }
        }
    }

    /// <summary>
    /// Folders many programs share (Windows, System32, Program Files itself, a drive root): their files are not this
    /// program's data, so they are never scanned as its install folder.
    /// </summary>
    public static bool IsSharedFolder(string dir)
    {
        try
        {
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Norm(dir);
            var rootOf = Path.GetPathRoot(full);
            if (rootOf is not null && Norm(rootOf) == full) return true;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && full.StartsWith(Norm(windows), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles })
            {
                var p = Environment.GetFolderPath(sf);
                if (p.Length > 0 && string.Equals(full, Norm(p), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Turns a candidate into a config entry.</summary>
    public static DataSourceConfig ToConfig(DataCandidate c) => new()
    {
        Kind = c.Kind == "sqlite" ? "sqlite" : c.Kind == "access" ? "access" : "file",
        Path = c.Path,
        Label = Path.GetFileName(c.Path),
    };
}
