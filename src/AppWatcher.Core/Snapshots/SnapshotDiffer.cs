using System.Text;
using System.Text.Json;

namespace AppWatcher.Core.Snapshots;

public sealed class ChangedValue
{
    public string? Old { get; set; }
    public string? New { get; set; }
}

public sealed class TableDiff
{
    public string Db { get; set; } = "";
    public string Table { get; set; } = "";
    public Dictionary<string, Dictionary<string, string?>> Added { get; set; } = new();
    public Dictionary<string, Dictionary<string, string?>> Removed { get; set; } = new();
    /// <summary>key -> column -> (old, new)</summary>
    public Dictionary<string, Dictionary<string, ChangedValue>> Changed { get; set; } = new();
    /// <summary>Full new row for every changed key, so learners can test formulas on it.</summary>
    public Dictionary<string, Dictionary<string, string?>> ChangedRows { get; set; } = new();
    public string DbName => Path.GetFileName(Db.Replace("sqlserver:", ""));
    public string QualifiedName => $"{DbName} / {Table}";
}

public sealed class FileChange
{
    public string Path { get; set; } = "";
    /// <summary>created | deleted | modified</summary>
    public string State { get; set; } = "";
}

public sealed class DiffResult
{
    public List<TableDiff> Tables { get; set; } = new();
    public List<FileChange> Files { get; set; } = new();
    public bool IsEmpty => Tables.Count == 0 && Files.Count == 0;

    /// <summary>Same text as `python scripts/snapshot_diff.py diff before after` prints.</summary>
    public string ToMarkdown(string before = "before", string after = "after")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Data changes: {before} -> {after}");
        sb.AppendLine();
        foreach (var t in Tables)
        {
            sb.AppendLine($"### {t.DbName} / {t.Table}: +{t.Added.Count} -{t.Removed.Count} ~{t.Changed.Count}");
            foreach (var (k, row) in t.Added.Take(20)) sb.AppendLine($"- added {k}: {Json(row)}");
            foreach (var (k, row) in t.Removed.Take(20)) sb.AppendLine($"- removed {k}: {Json(row)}");
            foreach (var (k, fields) in t.Changed.Take(20))
            {
                var pairs = fields.ToDictionary(f => f.Key, f => new[] { f.Value.Old, f.Value.New });
                sb.AppendLine($"- changed {k}: {JsonSerializer.Serialize(pairs)}");
            }
            sb.AppendLine();
        }
        foreach (var f in Files) sb.AppendLine($"- file {f.State}: {f.Path}");
        if (IsEmpty) sb.AppendLine("No data changes detected.");
        return sb.ToString();
    }

    private static string Json(Dictionary<string, string?> row) => JsonSerializer.Serialize(row);
}

/// <summary>Port of the diff() function in app-twin-kit/scripts/snapshot_diff.py.</summary>
public static class SnapshotDiffer
{
    public static DiffResult Diff(DataSnapshot a, DataSnapshot b)
    {
        var result = new DiffResult();
        foreach (var db in a.Databases.Keys.Union(b.Databases.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            a.Databases.TryGetValue(db, out var ta); ta ??= new();
            b.Databases.TryGetValue(db, out var tb); tb ??= new();
            foreach (var t in ta.Keys.Union(tb.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var ra = ta.TryGetValue(t, out var sa) ? sa.Rows : new();
                var rb = tb.TryGetValue(t, out var sb2) ? sb2.Rows : new();
                if ((sa?.Skipped ?? false) || (sb2?.Skipped ?? false)) continue;
                var diff = new TableDiff { Db = db, Table = t };
                foreach (var (k, row) in rb)
                {
                    if (!ra.TryGetValue(k, out var old)) { diff.Added[k] = row; continue; }
                    var changed = ChangedFields(old, row);
                    if (changed.Count > 0) { diff.Changed[k] = changed; diff.ChangedRows[k] = row; }
                }
                foreach (var (k, row) in ra)
                    if (!rb.ContainsKey(k)) diff.Removed[k] = row;
                if (diff.Added.Count + diff.Removed.Count + diff.Changed.Count > 0) result.Tables.Add(diff);
            }
        }
        foreach (var f in a.Files.Keys.Union(b.Files.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            a.Files.TryGetValue(f, out var fa);
            b.Files.TryGetValue(f, out var fb);
            if (fa is not null && fb is not null && fa.Size == fb.Size && fa.Sha256 == fb.Sha256) continue;
            result.Files.Add(new FileChange { Path = f, State = fa is null ? "created" : fb is null ? "deleted" : "modified" });
        }
        return result;
    }

    private static Dictionary<string, ChangedValue> ChangedFields(Dictionary<string, string?> old, Dictionary<string, string?> row)
    {
        var d = new Dictionary<string, ChangedValue>();
        foreach (var (c, v) in row)
        {
            old.TryGetValue(c, out var ov);
            if (ov != v) d[c] = new ChangedValue { Old = ov, New = v };
        }
        foreach (var c in old.Keys)
            if (!row.ContainsKey(c)) d[c] = new ChangedValue { Old = old[c], New = null };
        return d;
    }
}
