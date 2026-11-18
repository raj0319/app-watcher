using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using AppWatcher.Core.Snapshots;
using Microsoft.Data.SqlClient;

namespace AppWatcher.Core.Sources;

/// <summary>Shared table dumper for ADO.NET engines. Read only: only SELECT statements are ever sent.</summary>
internal static class AdoDumper
{
    public static void Dump(DbConnection con, string sourceId, DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables,
        bool onlyHot, IEnumerable<(string schema, string name)> tables, Func<string, string, List<string>> primaryKey, Func<string, string, string> quote)
    {
        var dict = new Dictionary<string, TableSnapshot>();
        into.Databases[sourceId] = dict;
        foreach (var (schema, name) in tables)
        {
            var display = string.IsNullOrEmpty(schema) || schema == "dbo" ? name : $"{schema}.{name}";
            var ts = new TableSnapshot();
            dict[display] = ts;
            if (onlyHot && !hotTables!.Contains(display)) { ts.Skipped = true; ts.Note = "database over size cap; table not hot"; continue; }
            try
            {
                ts.PrimaryKey = primaryKey(schema, name);
                using (var count = con.CreateCommand())
                {
                    count.CommandText = $"SELECT COUNT(*) FROM {quote(schema, name)}";
                    ts.RowCount = Convert.ToInt64(count.ExecuteScalar());
                }
                if (ts.RowCount > settings.MaxRowsPerTable) { ts.Skipped = true; ts.Note = $"{ts.RowCount} rows is over the row cap"; continue; }
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT * FROM {quote(schema, name)}";
                using var r = cmd.ExecuteReader(CommandBehavior.SequentialAccess);
                for (var i = 0; i < r.FieldCount; i++) ts.Columns.Add(r.GetName(i));
                while (r.Read())
                {
                    var row = new Dictionary<string, string?>(r.FieldCount);
                    for (var i = 0; i < r.FieldCount; i++) row[ts.Columns[i]] = Values.Normalize(r.IsDBNull(i) ? null : r.GetValue(i));
                    ts.Rows[Values.RowKey(row, ts.PrimaryKey)] = row;
                }
            }
            catch (Exception ex)
            {
                ts.Skipped = true; ts.Note = "read failed: " + ex.Message;
                into.Warnings.Add($"{display}: {ex.Message}");
            }
        }
    }
}

/// <summary>Access .mdb/.accdb through the ACE OLE DB provider, working on a temporary copy so the app is never locked.</summary>
public sealed class AccessSource : ISnapshotSource
{
    public string Path { get; }
    public string Id => Path;
    public IEnumerable<string> WatchPaths => new[] { Path };

    public AccessSource(string path) => Path = System.IO.Path.GetFullPath(path);

    public static bool ProviderAvailable()
    {
        try
        {
            using var e = OleDbEnumerator.GetRootEnumerator();
            while (e.Read())
                if (e.GetValue(0)?.ToString()?.StartsWith("Microsoft.ACE.OLEDB", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        catch { }
        return false;
    }

    public bool? ChangedSince(DateTime t)
    {
        try { var fi = new FileInfo(Path); return fi.Exists ? fi.LastWriteTime > t : null; } catch { return null; }
    }

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables)
    {
        if (!File.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        if (!ProviderAvailable()) { into.Warnings.Add("Access driver (Microsoft.ACE.OLEDB) is not installed; file hashed only"); into.Files[Path] = FileSource.Entry(Path, settings)!; return; }
        var size = new FileInfo(Path).Length;
        var onlyHot = hotTables is not null && size > settings.MaxDbBytes;
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appwatcher-" + Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(Path));
        try
        {
            File.Copy(Path, temp, true);
            using var con = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={temp};Mode=Read;Persist Security Info=False;");
            con.Open();
            var schema = con.GetSchema("Tables");
            var tables = schema.Rows.Cast<DataRow>()
                .Where(r => r["TABLE_TYPE"]?.ToString() == "TABLE")
                .Select(r => ("", r["TABLE_NAME"]!.ToString()!)).ToList();
            var pkSchema = con.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, null);
            List<string> Pk(string s, string n) => pkSchema is null ? new() : pkSchema.Rows.Cast<DataRow>()
                .Where(r => r["TABLE_NAME"]?.ToString() == n).OrderBy(r => r["ORDINAL"]).Select(r => r["COLUMN_NAME"]!.ToString()!).ToList();
            AdoDumper.Dump(con, Id, into, settings, hotTables, onlyHot, tables, Pk, (s, n) => $"[{n}]");
        }
        catch (Exception ex)
        {
            into.Warnings.Add($"Access read failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

/// <summary>SQL Server (Express) through a connection string the client supplies. ApplicationIntent=ReadOnly is added.</summary>
public sealed class SqlServerSource : ISnapshotSource
{
    private readonly string _cs;
    public string Label { get; }
    public string Id => "sqlserver:" + Label;
    public IEnumerable<string> WatchPaths => Array.Empty<string>();

    public SqlServerSource(string connectionString, string label)
    {
        var b = new SqlConnectionStringBuilder(connectionString) { ApplicationName = "AppWatcher (read only)" };
        if (!connectionString.Contains("ApplicationIntent", StringComparison.OrdinalIgnoreCase)) b.ApplicationIntent = ApplicationIntent.ReadOnly;
        if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)) b.ConnectTimeout = 10;
        _cs = b.ToString();
        Label = label;
    }

    public bool? ChangedSince(DateTime t) => null;

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables)
    {
        try
        {
            using var con = new SqlConnection(_cs);
            con.Open();
            long dbBytes = 0;
            using (var size = con.CreateCommand())
            {
                size.CommandText = "SELECT SUM(CAST(size AS bigint)) * 8192 FROM sys.database_files WHERE type = 0";
                dbBytes = Convert.ToInt64(size.ExecuteScalar() ?? 0L);
            }
            var onlyHot = hotTables is not null && dbBytes > settings.MaxDbBytes;
            var tables = new List<(string, string)>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='BASE TABLE' ORDER BY 1,2";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add((r.GetString(0), r.GetString(1)));
            }
            List<string> Pk(string s, string n)
            {
                var pk = new List<string>();
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"SELECT k.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS c
                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k ON c.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND c.TABLE_SCHEMA = k.TABLE_SCHEMA AND c.TABLE_NAME = k.TABLE_NAME
                    WHERE c.CONSTRAINT_TYPE='PRIMARY KEY' AND c.TABLE_SCHEMA=@s AND c.TABLE_NAME=@n ORDER BY k.ORDINAL_POSITION";
                cmd.Parameters.AddWithValue("@s", s); cmd.Parameters.AddWithValue("@n", n);
                using var r = cmd.ExecuteReader();
                while (r.Read()) pk.Add(r.GetString(0));
                return pk;
            }
            AdoDumper.Dump(con, Id, into, settings, hotTables, onlyHot, tables, Pk, (s, n) => $"[{s}].[{n}]");
        }
        catch (Exception ex)
        {
            into.Warnings.Add($"SQL Server read failed ({Label}): {ex.Message}");
            into.Databases[Id] = new();
        }
    }

    public static string? TestConnection(string connectionString)
    {
        try
        {
            using var con = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 8 }.ToString());
            con.Open();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
