using AppWatcher.Core.Snapshots;
using Microsoft.Data.Sqlite;

namespace AppWatcher.Core.Sources;

/// <summary>Dumps every table of a SQLite file, opened read-only (same as snapshot_diff.py with ?mode=ro).</summary>
public sealed class SqliteSource : ISnapshotSource
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
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

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables)
    {
        var tables = new Dictionary<string, TableSnapshot>();
        into.Databases[Id] = tables;
        if (!File.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        var size = new FileInfo(Path).Length;
        var onlyHot = hotTables is not null && size > settings.MaxDbBytes;

        var cs = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var con = new SqliteConnection(cs);
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
            if (onlyHot && !hotTables!.Contains(name))
            {
                ts.Skipped = true; ts.Note = "database over size cap; table not hot";
                continue;
            }
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")";
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
                cmd.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
                ts.RowCount = Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (ts.RowCount > settings.MaxRowsPerTable)
            {
                ts.Skipped = true; ts.Note = $"{ts.RowCount} rows is over the row cap ({settings.MaxRowsPerTable})";
                continue;
            }
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\"";
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
}
