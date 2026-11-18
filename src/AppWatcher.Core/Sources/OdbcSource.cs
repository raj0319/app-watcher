using System.Data;
using System.Data.Odbc;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Sources;

/// <summary>
/// Any database that has an ODBC driver installed for the app (UniVerse/U2, Pervasive, Progress, Firebird,
/// MySQL, PostgreSQL, Oracle, DB2). Only SELECT statements are sent; tables come from the driver's catalog.
/// The client supplies a DSN or a connection string with a read-only login.
/// </summary>
public sealed class OdbcSource : ISnapshotSource
{
    private readonly string _cs;
    public string Label { get; }
    public string Id => "odbc:" + Label;
    public IEnumerable<string> WatchPaths => Array.Empty<string>();
    /// <summary>Optional list of tables to read (the catalog of a big ERP has thousands); empty = all.</summary>
    public List<string> OnlyTables { get; }

    public OdbcSource(string connectionString, string label, IEnumerable<string>? onlyTables = null)
    {
        _cs = connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase) ? connectionString : connectionString.TrimEnd(';') + ";Connection Timeout=10";
        Label = label;
        OnlyTables = onlyTables?.Where(t => t.Trim().Length > 0).Select(t => t.Trim()).ToList() ?? new();
    }

    public bool? ChangedSince(DateTime t) => null;
    public ChangeSignal Probe(string? token) => new(null, null, null);

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null)
    {
        try
        {
            using var con = new OdbcConnection(_cs);
            con.Open();
            var tables = new List<(string schema, string name)>();
            try
            {
                var schema = con.GetSchema("Tables");
                foreach (DataRow r in schema.Rows)
                {
                    var type = r.Table.Columns.Contains("TABLE_TYPE") ? r["TABLE_TYPE"]?.ToString() ?? "" : "TABLE";
                    if (!type.Contains("TABLE", StringComparison.OrdinalIgnoreCase) || type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = r["TABLE_NAME"]?.ToString() ?? "";
                    var sch = r.Table.Columns.Contains("TABLE_SCHEM") ? r["TABLE_SCHEM"]?.ToString() ?? "" : "";
                    if (name.Length == 0) continue;
                    tables.Add((sch, name));
                }
            }
            catch (Exception ex) { into.Warnings.Add($"{Id}: table list failed: {ex.Message}"); }
            if (OnlyTables.Count > 0) tables = tables.Where(t => OnlyTables.Contains(t.name, StringComparer.OrdinalIgnoreCase)).ToList();
            else if (tables.Count > 400) { into.Warnings.Add($"{Id}: {tables.Count} tables; only the first 400 are read. Name the tables you care about in the source's table list."); tables = tables.Take(400).ToList(); }
            var onlyHot = hotTables is not null && tables.Count > 50;
            List<string> Pk(string s, string n)
            {
                try
                {
                    var pk = con.GetSchema("Indexes", new[] { null, s.Length > 0 ? s : null, n });
                    return pk.Rows.Cast<DataRow>().Where(r => r.Table.Columns.Contains("PRIMARY_KEY") && r["PRIMARY_KEY"] is true or 1).Select(r => r["COLUMN_NAME"]!.ToString()!).ToList();
                }
                catch { return new(); }
            }
            string Quote(string s, string n) => s.Length > 0 ? $"\"{s}\".\"{n}\"" : $"\"{n}\"";
            AdoDumper.Dump(con, Id, into, settings, hotTables, onlyHot, tables, Pk, Quote, onlyTables);
        }
        catch (Exception ex)
        {
            into.Warnings.Add($"ODBC read failed ({Label}): {ex.Message}");
            into.Databases[Id] = new();
        }
    }

    public static string? TestConnection(string connectionString)
    {
        try
        {
            using var con = new OdbcConnection(connectionString.TrimEnd(';') + ";Connection Timeout=8");
            con.Open();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
