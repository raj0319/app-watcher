using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Finds foreign-key candidates: values written to one table that exist as keys in another, plus naming hints.</summary>
public static class RelationFinder
{
    private const int SampleRows = 2000;

    public static List<Relation> Learn(DataSnapshot? latest, List<EffectRecord> effects)
    {
        var result = new Dictionary<(string, string, string, string), Relation>();
        if (latest is null) return new();

        // key index: value -> set of (table, pk column)
        var index = new Dictionary<string, HashSet<(string table, string col)>>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, TableSnapshot>();
        var entries = 0;
        foreach (var (db, ts) in latest.Databases)
            foreach (var (name, t) in ts)
            {
                var q = Qualified(db, name);
                tables[q] = t;
                if (t.Skipped || t.PrimaryKey.Count != 1) continue;
                var pk = t.PrimaryKey[0];
                foreach (var row in t.Rows.Values)
                {
                    if (!row.TryGetValue(pk, out var v) || string.IsNullOrEmpty(v) || v.Length > 64) continue;
                    if (!index.TryGetValue(v, out var set)) index[v] = set = new();
                    set.Add((q, pk));
                    if (++entries > 500_000) break;
                }
            }

        // 1. snapshot-wide match ratio per column
        foreach (var (q, t) in tables)
        {
            if (t.Skipped) continue;
            var pkSet = new HashSet<string>(t.PrimaryKey);
            var rows = t.Rows.Values.Take(SampleRows).ToList();
            foreach (var col in t.Columns)
            {
                if (pkSet.Contains(col) && t.PrimaryKey.Count == 1) continue;
                var nonNull = 0;
                var matched = new Dictionary<(string table, string col), (int n, HashSet<string> distinct)>();
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(col, out var v) || string.IsNullOrEmpty(v)) continue;
                    nonNull++;
                    if (!index.TryGetValue(v, out var targets)) continue;
                    foreach (var tgt in targets)
                    {
                        if (tgt.table == q) continue;
                        if (!matched.TryGetValue(tgt, out var m)) matched[tgt] = m = (0, new HashSet<string>());
                        m.distinct.Add(v);
                        matched[tgt] = (m.n + 1, m.distinct);
                    }
                }
                if (nonNull < 2) continue;
                foreach (var (tgt, m) in matched)
                {
                    var ratio = (double)m.n / nonNull;
                    var hint = NameMatches(col, tgt.table);
                    var ok = (ratio >= 0.8 && m.distinct.Count >= 2 && (hint || m.distinct.Count >= 5)) || (hint && ratio >= 0.5);
                    if (!ok) continue;
                    var r = Get(result, q, col, tgt.table, tgt.col);
                    r.SnapshotMatches = m.n; r.SnapshotRows = nonNull; r.NameHint = hint;
                    r.Evidence.Inferred = true;
                    r.Evidence.Files.Add("snapshots/before.json");
                }
            }
        }

        // 2. seen writes: added rows whose values are keys elsewhere
        foreach (var fx in effects)
            foreach (var td in fx.Diff.Tables)
            {
                var q = td.QualifiedName;
                foreach (var row in td.Added.Values)
                    foreach (var (col, v) in row)
                    {
                        if (string.IsNullOrEmpty(v) || !index.TryGetValue(v, out var targets)) continue;
                        foreach (var tgt in targets)
                        {
                            if (tgt.table == q) continue;
                            var key = (q, col, tgt.table, tgt.col);
                            if (!result.TryGetValue(key, out var r))
                            {
                                // only count writes for pairs that also pass the snapshot test or a name hint
                                if (!NameMatches(col, tgt.table)) continue;
                                r = Get(result, q, col, tgt.table, tgt.col);
                                r.NameHint = true;
                            }
                            r.Evidence.Inferred = false;
                            r.Evidence.Add(fx.Source);
                        }
                    }
            }

        // 3. naming only, when nothing else was found for the column
        foreach (var (q, t) in tables)
        {
            if (t.Skipped) continue;
            foreach (var col in t.Columns)
            {
                if (result.Keys.Any(k => k.Item1 == q && k.Item2 == col)) continue;
                if (!LooksLikeId(col)) continue;
                foreach (var (tq, tt) in tables)
                {
                    if (tq == q || tt.PrimaryKey.Count != 1 || !NameMatches(col, tq)) continue;
                    var r = Get(result, q, col, tq, tt.PrimaryKey[0]);
                    r.NameHint = true; r.Evidence.Inferred = true;
                    r.Evidence.Files.Add("(column name only)");
                }
            }
        }

        return result.Values.OrderBy(r => r.FromTable).ThenBy(r => r.FromColumn).ToList();
    }

    private static Relation Get(Dictionary<(string, string, string, string), Relation> d, string ft, string fc, string tt, string tc)
    {
        var k = (ft, fc, tt, tc);
        if (!d.TryGetValue(k, out var r)) d[k] = r = new Relation { FromTable = ft, FromColumn = fc, ToTable = tt, ToColumn = tc };
        return r;
    }

    public static string Qualified(string db, string table) => $"{Path.GetFileName(db.Replace("sqlserver:", ""))} / {table}";

    public static bool LooksLikeId(string col)
    {
        var c = col.ToLowerInvariant();
        return c.EndsWith("id") || c.EndsWith("_no") || c.EndsWith("_key") || c.EndsWith("_code") || c.EndsWith("code") || c.EndsWith("ref");
    }

    /// <summary>customer_id / CustomerID / cust_id vs customers / tblCustomer.</summary>
    public static bool NameMatches(string column, string qualifiedTable)
    {
        var table = qualifiedTable.Contains(" / ") ? qualifiedTable[(qualifiedTable.IndexOf(" / ") + 3)..] : qualifiedTable;
        var tn = Norm(table);
        if (tn.StartsWith("tbl")) tn = tn[3..];
        if (tn.StartsWith("tb")) tn = tn[2..];
        tn = Singular(tn);
        var cn = Norm(column);
        foreach (var suffix in new[] { "id", "no", "key", "code", "ref" })
            if (cn.EndsWith(suffix) && cn.Length > suffix.Length) { cn = cn[..^suffix.Length]; break; }
        cn = Singular(cn);
        if (cn.Length < 3 || tn.Length < 3) return false;
        return cn == tn || tn.EndsWith(cn) || cn.EndsWith(tn) || (cn.Length >= 4 && tn.StartsWith(cn));
    }

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static string Singular(string s) => s.EndsWith("ies") ? s[..^3] + "y" : s.EndsWith("ses") ? s[..^2] : s.EndsWith("s") && !s.EndsWith("ss") ? s[..^1] : s;
}
