using AppWatcher.Core.Snapshots;
using Microsoft.Data.Sqlite;

namespace AppWatcher.Core.Sources;

/// <summary>Dumps every table of a SQLite file, opened read-only (same as snapshot_diff.py with ?mode=ro).</summary>
public sealed class SqliteSource : ISnapshotSource, IDisposable
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
    private SqliteConnection? _probeConnection;
    public string Path { get; }
    public string Id => Path;
    public IEnumerable<string> WatchPaths => new[] { Path };

    public SqliteSource(string path) => Path = System.IO.Path.GetFullPath(path);

    public static bool IsSqlite(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[16];
            return fs.Read(buf, 0, 16) == 16 && buf.AsSpan().SequenceEqual(Magic);
        }
        catch { return false; }
    }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

    public bool? ChangedSince(DateTime t)
    {
        try
        {
            var fi = new FileInfo(Path);
            if (!fi.Exists) return null;
            if (fi.LastWriteTime > t) return true;
            var wal = new FileInfo(Path + "-wal");
            return wal.Exists && wal.LastWriteTime > t;
        }
        catch { return null; }
    }

    /// <summary>
    /// PRAGMA data_version changes whenever another connection committed; it is cheap and does not read
    /// the tables. It is relative to one connection, so a read-only connection is kept open for probing.
    /// File mtime/size is the fallback (and the only signal when the connection cannot be kept).
    /// </summary>
    public ChangeSignal Probe(string? token)
    {
        var fileToken = SourceFactory.FileToken(Path) ?? "";
        var walToken = SourceFactory.FileToken(Path + "-wal") ?? "";
        string dv = "";
        try
        {
            if (_probeConnection is null) { _probeConnection = new SqliteConnection(ConnectionString); _probeConnection.Open(); }
            using var cmd = _probeConnection.CreateCommand();
            cmd.CommandText = "PRAGMA data_version;";
            dv = Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }
        catch { try { _probeConnection?.Dispose(); } catch { } _probeConnection = null; }
        var now = $"{fileToken}|{walToken}|{dv}";
        if (token is null) return new ChangeSignal(null, null, now);
        return new ChangeSignal(token != now, null, now);
    }

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null)
    {
        var tables = new Dictionary<string, TableSnapshot>();
        into.Databases[Id] = tables;
        if (!File.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        var size = new FileInfo(Path).Length;
        var onlyHot = hotTables is not null && size > settings.MaxDbBytes;

        using var con = new SqliteConnection(ConnectionString);
        con.Open();
        using (var pragma = con.CreateCommand()) { pragma.CommandText = "PRAGMA query_only=1;"; pragma.ExecuteNonQuery(); }

        var names = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }

        foreach (var name in names)
        {
            var ts = new TableSnapshot();
            tables[name] = ts;
            if (onlyTables is not null && !onlyTables.Contains(name)) { ts.Skipped = true; ts.Note = "unchanged"; continue; }
            if (onlyHot && !hotTables!.Contains(name))
            {
                ts.Skipped = true; ts.Note = "database over size cap; table not hot";
                continue;
            }
            var quoted = name.Replace("\"", "\"\"");
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{quoted}\")";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var col = r.GetString(1);
                    ts.Columns.Add(col);
                    if (r.GetInt32(5) > 0) ts.PrimaryKey.Add(col);
                }
            }
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{quoted}\"";
                ts.RowCount = Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (ts.RowCount > settings.MaxRowsPerTable)
            {
                ts.Skipped = true; ts.Note = $"{ts.RowCount} rows is over the row cap ({settings.MaxRowsPerTable})";
                continue;
            }
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM \"{quoted}\"";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var row = new Dictionary<string, string?>(ts.Columns.Count);
                    for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = Values.Normalize(r.IsDBNull(i) ? null : r.GetValue(i));
                    ts.Rows[Values.RowKey(row, ts.PrimaryKey)] = row;
                }
            }
        }
    }

    public void Dispose()
    {
        try { _probeConnection?.Dispose(); } catch { }
        _probeConnection = null;
    }
}
