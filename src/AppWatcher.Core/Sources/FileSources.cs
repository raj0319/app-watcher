using System.Security.Cryptography;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Sources;

/// <summary>A single data file: size + SHA-256. SQLite files are dumped as tables instead.</summary>
public sealed class FileSource : ISnapshotSource
{
    public string Path { get; }
    public string Id => Path;
    public IEnumerable<string> WatchPaths => new[] { Path };

    private FileSource(string path) => Path = System.IO.Path.GetFullPath(path);

    public static ISnapshotSource ForPath(string path)
    {
        if (Directory.Exists(path)) return new FolderSource(path);
        if (File.Exists(path) && SqliteSource.IsSqlite(path)) return new SqliteSource(path);
        return new FileSource(path);
    }

    public bool? ChangedSince(DateTime t)
    {
        try { var fi = new FileInfo(Path); return fi.Exists ? fi.LastWriteTime > t : null; } catch { return null; }
    }

    public ChangeSignal Probe(string? token)
    {
        var now = SourceFactory.FileToken(Path) ?? "missing";
        return new ChangeSignal(token is null ? null : token != now, null, now);
    }

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null)
    {
        if (!File.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        var e = Entry(Path, settings);
        if (e is not null) into.Files[Path] = e;
    }

    internal static FileEntry? Entry(string path, SnapshotSettings settings)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > settings.MaxFileBytesToHash)
                return new FileEntry { Size = fi.Length, Sha256 = "skipped:too-large", Modified = fi.LastWriteTime };
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var hash = SHA256.HashData(fs);
            return new FileEntry { Size = fi.Length, Sha256 = Convert.ToHexString(hash).ToLowerInvariant(), Modified = fi.LastWriteTime };
        }
        catch (IOException)
        {
            try { var fi = new FileInfo(path); return new FileEntry { Size = fi.Length, Sha256 = "locked:" + fi.LastWriteTime.Ticks, Modified = fi.LastWriteTime }; }
            catch { return null; }
        }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Walks a folder: SQLite files become tables, everything else is hashed. Mirrors snapshot_diff.py.</summary>
public sealed class FolderSource : ISnapshotSource
{
    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".pdb", ".tmp", ".lock", ".log", ".bak~", ".ocx", ".sys" };
    public string Path { get; }
    public string Id => Path;
    public IEnumerable<string> WatchPaths => new[] { Path };
    public int MaxFiles { get; set; } = 2000;

    public FolderSource(string path) => Path = System.IO.Path.GetFullPath(path);

    public bool? ChangedSince(DateTime t)
    {
        try
        {
            if (!Directory.Exists(Path)) return null;
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                if (++n > MaxFiles) return true; // too big to tell cheaply; assume changed
                if (File.GetLastWriteTime(f) > t) return true;
            }
            return false;
        }
        catch { return null; }
    }

    /// <summary>Token = newest mtime and total size over the folder (bounded walk).</summary>
    public ChangeSignal Probe(string? token)
    {
        try
        {
            if (!Directory.Exists(Path)) return new ChangeSignal(null, null, "missing");
            long newest = 0, size = 0; var n = 0;
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                if (++n > MaxFiles) return new ChangeSignal(null, null, null);
                var fi = new FileInfo(f);
                newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
                size += fi.Length;
            }
            var now = $"{newest}:{size}:{n}";
            return new ChangeSignal(token is null ? null : token != now, null, now);
        }
        catch { return new ChangeSignal(null, null, null); }
    }

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null)
    {
        if (!Directory.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        var n = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories); }
        catch (Exception ex) { into.Warnings.Add($"cannot list {Path}: {ex.Message}"); return; }
        foreach (var f in files)
        {
            if (++n > MaxFiles) { into.Warnings.Add($"{Path}: more than {MaxFiles} files, rest skipped"); break; }
            if (SkipExt.Contains(System.IO.Path.GetExtension(f))) continue;
            if (f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || f.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (SqliteSource.IsSqlite(f)) { using var s = new SqliteSource(f); s.Snapshot(into, settings, hotTables, onlyTables); }
                else
                {
                    var e = FileSource.Entry(f, settings);
                    if (e is not null) into.Files[System.IO.Path.GetFullPath(f)] = e;
                }
            }
            catch (Exception ex) { into.Warnings.Add($"{f}: {ex.Message}"); }
        }
    }
}
