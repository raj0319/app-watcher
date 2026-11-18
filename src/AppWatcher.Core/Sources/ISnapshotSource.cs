using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Sources;

/// <summary>A read-only view on one place where the watched app keeps data.</summary>
public interface ISnapshotSource
{
    /// <summary>Stable id used as the database key in snapshots (file path or "sqlserver:label").</summary>
    string Id { get; }

    /// <summary>Folders or files a FileSystemWatcher should watch to notice changes cheaply. Empty for servers.</summary>
    IEnumerable<string> WatchPaths { get; }

    /// <summary>Reads into the snapshot. Must never write. hotTables = null means "all tables".</summary>
    void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables);

    /// <summary>Cheap check: did anything change since the given time? Null when unknown.</summary>
    bool? ChangedSince(DateTime t);
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
}
