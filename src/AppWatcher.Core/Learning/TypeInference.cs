using System.Globalization;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

public static class TypeInference
{
    public static List<TableInfo> Describe(DataSnapshot? latest, Dictionary<string, Evidence> tableWrites)
    {
        var tables = new List<TableInfo>();
        if (latest is not null)
            foreach (var (db, ts) in latest.Databases)
                foreach (var (name, t) in ts)
                {
                    var ti = new TableInfo { Db = db, Table = name, PrimaryKey = t.PrimaryKey, RowCount = t.Skipped ? t.RowCount : t.Rows.Count, Skipped = t.Skipped, Note = t.Note };
                    var rows = t.Rows.Values.Take(5000).ToList();
                    foreach (var c in t.Columns) ti.Columns.Add(Column(c, rows, t.PrimaryKey.Contains(c)));
                    tables.Add(ti);
                }
        foreach (var (q, ev) in tableWrites)
        {
            var ti = tables.FirstOrDefault(x => x.Qualified == q);
            if (ti is null)
            {
                var idx = q.IndexOf(" / ", StringComparison.Ordinal);
                ti = new TableInfo { Db = idx < 0 ? "" : q[..idx], Table = idx < 0 ? q : q[(idx + 3)..], Note = "seen only in effects" };
                tables.Add(ti);
            }
            ti.WrittenEvidence = new Evidence { Seen = ev.Seen, Files = ev.Files.ToList() };
        }
        return tables.OrderBy(t => t.Qualified).ToList();
    }

    public static ColumnInfo Column(string name, List<Dictionary<string, string?>> rows, bool isPk)
    {
        int n = 0, nulls = 0, ints = 0, decs = 0, dates = 0, datetimes = 0, bools = 0, blobs = 0, maxLen = 0;
        var shapes = new Dictionary<string, int>();
        var distinct = new HashSet<string?>();
        foreach (var r in rows)
        {
            r.TryGetValue(name, out var v);
            n++;
            distinct.Add(v);
            if (v is null) { nulls++; continue; }
            maxLen = Math.Max(maxLen, v.Length);
            var sh = Masking.Shape(v);
            shapes[sh] = shapes.GetValueOrDefault(sh) + 1;
            if (v.StartsWith("blob:")) { blobs++; continue; }
            if (v is "true" or "false" or "0" or "1") bools++;
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) { ints++; continue; }
            if (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) { decs++; continue; }
            if (DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                if (v.Length <= 10) dates++; else datetimes++;
            }
        }
        var nonNull = n - nulls;
        string type;
        if (nonNull == 0) type = "empty";
        else if (blobs == nonNull) type = "blob";
        else if (bools == nonNull && distinct.Count <= 3) type = "boolean";
        else if (ints == nonNull) type = "integer";
        else if (ints + decs == nonNull) type = "decimal";
        else if (dates == nonNull) type = "date";
        else if (dates + datetimes == nonNull) type = "datetime";
        else type = $"text({maxLen})";
        return new ColumnInfo
        {
            Name = name, Type = type, Nullable = nulls > 0, IsPk = isPk,
            Distinct = distinct.Count,
            Shape = shapes.Count == 0 ? "" : shapes.OrderByDescending(s => s.Value).ThenBy(s => s.Key).First().Key,
        };
    }
}
