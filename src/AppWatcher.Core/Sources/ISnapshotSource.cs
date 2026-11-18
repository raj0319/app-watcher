using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Sources;

/// <summary>Cheap answer to "did anything change?" without reading the data. Tables = null means unknown (read everything).</summary>
public sealed record ChangeSignal(bool? Changed, ISet<string>? Tables, string? Token);

/// <summary>A read-only view on one place where the watched app keeps data.</summary>
public interface ISnapshotSource
{
    /// <summary>Stable id used as the database key in snapshots (file path or "sqlserver:label").</summary>
    string Id { get; }

    /// <summary>Folders or files a FileSystemWatcher should watch to notice changes cheaply. Empty for servers.</summary>
    IEnumerable<string> WatchPaths { get; }

    /// <summary>
    /// Reads into the snapshot. Must never write. hotTables = null means "all tables" (used when the database is over the size cap).
    /// onlyTables, when given, limits the read to those tables; others are marked Skipped with Note "unchanged" and the caller copies them from the previous snapshot.
    /// </summary>
    void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null);

    /// <summary>Cheap check: did anything change since the given time? Null when unknown.</summary>
    bool? ChangedSince(DateTime t);

    /// <summary>Cheap change probe with an opaque token from the previous probe (mtime/size, PRAGMA data_version, change tracking version).</summary>
    ChangeSignal Probe(string? token);
}

public static class SourceFactory
{
    public static ISnapshotSource? Create(DataSourceConfig c)
    {
        if (!c.Enabled) return null;
        switch (c.Kind)
        {
            case "sqlite": return c.Path is null ? null : new SqliteSource(c.Path);
            case "access": return c.Path is null ? null : new AccessSource(c.Path);
            case "sqlserver":
                var cs = Dpapi.Unprotect(c.ConnectionStringProtected);
                return cs is null ? null : new SqlServerSource(cs, c.Label ?? "sqlserver");
            case "folder": return c.Path is null ? null : new FolderSource(c.Path);
            case "file":
            default: return c.Path is null ? null : FileSource.ForPath(c.Path);
        }
    }

    public static List<ISnapshotSource> CreateAll(IEnumerable<DataSourceConfig> configs)
        => configs.Select(Create).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>mtime ticks and size of a file as a probe token.</summary>
    public static string? FileToken(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}" : null; } catch { return null; }
    }
}
