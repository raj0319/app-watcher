using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Columns with a small set of values (at most 12) become state machines ordered by the transitions seen over time.</summary>
public static class StatusFlowLearner
{
    public const int MaxStates = 12;

    public static List<StatusFlow> Learn(DataSnapshot? latest, List<EffectRecord> effects)
    {
        var state = new LearnState();
        foreach (var fx in effects.OrderBy(f => f.T)) AccumulateEffect(state, fx);
        return Finalize(state, latest);
    }

    public static void AccumulateEffect(LearnState state, EffectRecord fx)
    {
        foreach (var td in fx.Diff.Tables)
        {
            var q = td.QualifiedName;
            var touched = new HashSet<string>();
            foreach (var fields in td.Changed.Values)
                foreach (var (col, cv) in fields)
                {
                    if (string.IsNullOrEmpty(cv.Old) || string.IsNullOrEmpty(cv.New) || cv.Old.Length > 24 || cv.New.Length > 24) continue;
                    if (!LooksLikeStatus(col, new[] { cv.Old, cv.New })) continue;
                    var key = LearnState.Key(q, col);
                    if (!state.Status.TryGetValue(key, out var acc)) state.Status[key] = acc = new StatusAcc();
                    var tk = LearnState.Key(cv.Old, cv.New);
                    if (!acc.Transitions.TryGetValue(tk, out var ev)) acc.Transitions[tk] = ev = new Evidence();
                    ev.Add(fx.Source);
                    acc.Evidence.Add(fx.Source);
                    touched.Add(col);
                }
            foreach (var row in td.Added.Values)
                foreach (var (col, v) in row)
                {
                    if (string.IsNullOrEmpty(v) || v.Length > 24) continue;
                    var key = LearnState.Key(q, col);
                    // initial values count for columns already known as status-like (from transitions) or that look like one by name
                    var c = col.ToLowerInvariant();
                    if (!state.Status.ContainsKey(key) && !(c.Contains("status") || c.Contains("state") || c.Contains("stage") || c.Contains("phase"))) continue;
                    if (!LooksLikeStatus(col, new[] { v })) continue;
                    if (!state.Status.TryGetValue(key, out var acc)) state.Status[key] = acc = new StatusAcc();
                    acc.Initial[v] = acc.Initial.GetValueOrDefault(v) + 1;
                }
        }
    }

    public static List<StatusFlow> Finalize(LearnState state, DataSnapshot? latest)
    {
        var flows = new Dictionary<string, StatusFlow>();
        foreach (var (key, acc) in state.Status)
        {
            var parts = key.Split('\u0001');
            var f = new StatusFlow { Table = parts[0], Column = parts[1], Initial = new Dictionary<string, int>(acc.Initial) };
            f.Evidence = new Evidence { Seen = acc.Evidence.Seen, Files = acc.Evidence.Files.ToList(), Inferred = acc.Transitions.Count == 0 };
            foreach (var (tk, ev) in acc.Transitions)
            {
                var tp = tk.Split('\u0001');
                f.Transitions.Add(new Transition { From = tp[0], To = tp[1], Evidence = new Evidence { Seen = ev.Seen, Files = ev.Files.ToList() } });
            }
            flows[key] = f;
        }

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
                        var key = LearnState.Key(q, col);
                        if (flows.TryGetValue(key, out var existing)) { if (!tooMany) existing.Distribution = counts; continue; }
                        if (tooMany || counts.Count < 2) continue;
                        if (counts.Values.Sum() < Math.Max(5, counts.Count * 2)) continue;
                        if (!LooksLikeStatus(col, counts.Keys)) continue;
                        var f = new StatusFlow { Table = q, Column = col, Distribution = counts };
                        f.Evidence.Inferred = true;
                        f.Evidence.Files.Add("snapshots/before.json");
                        flows[key] = f;
                    }
                }

        foreach (var f in flows.Values) Finish(f);
        return flows.Values.Where(f => f.States.Count >= 2 && f.States.Count <= MaxStates).OrderBy(f => f.Table).ThenBy(f => f.Column).ToList();
    }

    /// <summary>Skips columns that are obviously not a status: free decimals, dates, ids.</summary>
    public static bool LooksLikeStatus(string col, IEnumerable<string> values)
    {
        var c = col.ToLowerInvariant();
        if (RelationFinder.LooksLikeId(col) && !c.Contains("status") && !c.Contains("state")) return false;
        if (c.Contains("date") || c.Contains("time") || c.Contains("amount") || c.Contains("price") || c.Contains("total") || c.Contains("qty") || c.Contains("quantity") || c.Contains("name") || c.Contains("note") || c.Contains("desc")) return false;
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
        var start = f.Initial.OrderByDescending(i => i.Value).ThenBy(i => i.Key).Select(i => i.Key).FirstOrDefault()
                    ?? states.OrderBy(s => s).FirstOrDefault(s => !f.Transitions.Any(t => t.To == s))
                    ?? f.Distribution.OrderByDescending(d => d.Value).ThenBy(d => d.Key).Select(d => d.Key).FirstOrDefault()
                    ?? states.OrderBy(s => s).First();
        // depth-first along the most frequent transitions, so the main path reads left to right
        void Visit(string s)
        {
            if (ordered.Contains(s)) return;
            ordered.Add(s);
            foreach (var next in f.Transitions.Where(t => t.From == s).OrderByDescending(t => t.Evidence.Seen).ThenBy(t => t.To).Select(t => t.To).ToList())
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
                    if (f.NeverSeen.Count >= 30) break;
                    f.NeverSeen.Add((a, b));
                }
        }
        f.Transitions = f.Transitions.OrderByDescending(t => t.Evidence.Seen).ThenBy(t => t.From).ThenBy(t => t.To).ToList();
    }
}
