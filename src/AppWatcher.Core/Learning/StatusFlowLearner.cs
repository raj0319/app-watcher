using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Columns with a small set of values become state machines ordered by the transitions seen over time.</summary>
public static class StatusFlowLearner
{
    public const int MaxStates = 8;

    public static List<StatusFlow> Learn(DataSnapshot? latest, List<EffectRecord> effects)
    {
        var flows = new Dictionary<(string, string), StatusFlow>();

        if (latest is not null)
            foreach (var (db, ts) in latest.Databases)
                foreach (var (name, t) in ts)
                {
                    if (t.Skipped || t.Rows.Count < 3) continue;
                    var q = RelationFinder.Qualified(db, name);
                    foreach (var col in t.Columns)
                    {
                        if (t.PrimaryKey.Contains(col)) continue;
                        var counts = new Dictionary<string, int>();
                        var tooMany = false;
                        foreach (var row in t.Rows.Values)
                        {
                            if (!row.TryGetValue(col, out var v) || string.IsNullOrEmpty(v)) continue;
                            if (v.Length > 24) { tooMany = true; break; }
                            counts[v] = counts.GetValueOrDefault(v) + 1;
                            if (counts.Count > MaxStates) { tooMany = true; break; }
                        }
                        if (tooMany || counts.Count < 2) continue;
                        if (counts.Values.Sum() < Math.Max(5, counts.Count * 2)) continue;
                        if (!LooksLikeStatus(col, counts.Keys)) continue;
                        var f = new StatusFlow { Table = q, Column = col, Distribution = counts };
                        f.Evidence.Inferred = true;
                        f.Evidence.Files.Add("snapshots/before.json");
                        flows[(q, col)] = f;
                    }
                }

        foreach (var fx in effects.OrderBy(f => f.T))
            foreach (var td in fx.Diff.Tables)
            {
                var q = td.QualifiedName;
                var touched = new Dictionary<string, (HashSet<string> vals, List<(string, string)> trans, List<string> init)>();
                foreach (var fields in td.Changed.Values)
                    foreach (var (col, cv) in fields)
                    {
                        if (string.IsNullOrEmpty(cv.Old) || string.IsNullOrEmpty(cv.New) || cv.Old.Length > 24 || cv.New.Length > 24) continue;
                        if (!touched.TryGetValue(col, out var tc)) touched[col] = tc = (new(), new(), new());
                        tc.vals.Add(cv.Old); tc.vals.Add(cv.New); tc.trans.Add((cv.Old, cv.New));
                    }
                foreach (var row in td.Added.Values)
                    foreach (var (col, v) in row)
                    {
                        if (string.IsNullOrEmpty(v) || v.Length > 24) continue;
                        if (!touched.TryGetValue(col, out var tc)) continue; // only columns we already treat as status-ish
                        tc.init.Add(v);
                    }
                foreach (var (col, tc) in touched)
                {
                    if (!flows.TryGetValue((q, col), out var f))
                    {
                        if (!LooksLikeStatus(col, tc.vals)) continue;
                        f = new StatusFlow { Table = q, Column = col };
                        flows[(q, col)] = f;
                    }
                    foreach (var (from, to) in tc.trans)
                    {
                        var tr = f.Transitions.FirstOrDefault(x => x.From == from && x.To == to);
                        if (tr is null) f.Transitions.Add(tr = new Transition { From = from, To = to });
                        tr.Evidence.Add(fx.Source);
                        f.Evidence.Inferred = false;
                        f.Evidence.Add(fx.Source);
                    }
                    foreach (var v in tc.init) f.Initial[v] = f.Initial.GetValueOrDefault(v) + 1;
                }
            }

        // initial states from added rows for columns that are status-like from the snapshot
        foreach (var fx in effects)
            foreach (var td in fx.Diff.Tables)
                foreach (var row in td.Added.Values)
                    foreach (var (col, v) in row)
                        if (!string.IsNullOrEmpty(v) && flows.TryGetValue((td.QualifiedName, col), out var f) && v.Length <= 24)
                            f.Initial[v] = f.Initial.GetValueOrDefault(v) + 1;

        foreach (var f in flows.Values) Finish(f);
        return flows.Values.Where(f => f.States.Count >= 2).OrderBy(f => f.Table).ThenBy(f => f.Column).ToList();
    }

    /// <summary>Skips columns that are obviously not a status: free decimals, dates, ids.</summary>
    public static bool LooksLikeStatus(string col, IEnumerable<string> values)
    {
        var c = col.ToLowerInvariant();
        if (RelationFinder.LooksLikeId(col) && !c.Contains("status") && !c.Contains("state")) return false;
        if (c.Contains("date") || c.Contains("time") || c.Contains("amount") || c.Contains("price") || c.Contains("total") || c.Contains("qty") || c.Contains("quantity")) return false;
        foreach (var v in values)
        {
            if (DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) && v.Length >= 8) return false;
            if (v.Contains('.') && Values.TryDecimal(v, out _)) return false;
        }
        return true;
    }

    private static void Finish(StatusFlow f)
    {
        var states = new HashSet<string>(f.Distribution.Keys);
        foreach (var t in f.Transitions) { states.Add(t.From); states.Add(t.To); }
        foreach (var i in f.Initial.Keys) states.Add(i);
        var ordered = new List<string>();
        var start = f.Initial.OrderByDescending(i => i.Value).Select(i => i.Key).FirstOrDefault()
                    ?? states.FirstOrDefault(s => !f.Transitions.Any(t => t.To == s))
                    ?? f.Distribution.OrderByDescending(d => d.Value).Select(d => d.Key).FirstOrDefault()
                    ?? states.First();
        // depth-first along the most frequent transitions, so the main path reads left to right
        void Visit(string s)
        {
            if (ordered.Contains(s)) return;
            ordered.Add(s);
            foreach (var next in f.Transitions.Where(t => t.From == s).OrderByDescending(t => t.Evidence.Seen).Select(t => t.To).ToList())
                Visit(next);
        }
        Visit(start);
        foreach (var s in states.OrderByDescending(s => f.Distribution.GetValueOrDefault(s)).ThenBy(s => s))
            if (!ordered.Contains(s)) ordered.Add(s);
        f.States = ordered;
        if (f.Transitions.Count > 0)
        {
            foreach (var a in ordered)
                foreach (var b in ordered)
                {
                    if (a == b || f.Transitions.Any(t => t.From == a && t.To == b)) continue;
                    if (f.NeverSeen.Count >= 20) break;
                    f.NeverSeen.Add((a, b));
                }
        }
        f.Transitions = f.Transitions.OrderByDescending(t => t.Evidence.Seen).ToList();
    }
}
