using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

internal sealed class Visit
{
    public string Win = "";
    public DateTime Start, End;
    public List<string> Controls = new();
    public string Source = "";
}

/// <summary>Groups events into sessions and flows by window sequence. Accumulates per session into LearnState.</summary>
public static class FlowLearner
{
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);
    private const int MaxDurations = 2000;

    /// <summary>Whole-history convenience wrapper (used by tests and the XES writer).</summary>
    public static (List<FlowStat> flows, List<NavEdge> nav, string? main, int sessions) Learn(List<UiEvent> events)
    {
        var state = new LearnState();
        foreach (var s in Sessions(events)) { state.Sessions++; Accumulate(state, s); }
        var (flows, nav, main) = Finalize(state);
        return (flows, nav, main, state.Sessions);
    }

    public static List<List<UiEvent>> Sessions(List<UiEvent> events)
    {
        var sessions = new List<List<UiEvent>>();
        List<UiEvent>? cur = null;
        DateTime last = DateTime.MinValue;
        foreach (var e in events.OrderBy(e => e.T))
        {
            if (cur is null || e.T - last > SessionGap) { cur = new List<UiEvent>(); sessions.Add(cur); }
            cur.Add(e);
            last = e.T;
        }
        return sessions;
    }

    public static void Accumulate(LearnState state, List<UiEvent> session)
    {
        var visits = Visits(session);
        if (visits.Count == 0) return;
        foreach (var v in visits)
        {
            if (!state.Windows.TryGetValue(v.Win, out var w)) state.Windows[v.Win] = w = new WindowAcc();
            w.Seconds += (v.End - v.Start).TotalSeconds;
            w.Visits++;
        }
        for (var i = 1; i < visits.Count; i++)
        {
            var k = LearnState.Key(visits[i - 1].Win, visits[i].Win);
            if (!state.Nav.TryGetValue(k, out var n)) state.Nav[k] = n = new NavAcc();
            n.Count++;
            if (n.Durations.Count < MaxDurations) n.Durations.Add((visits[i - 1].End - visits[i - 1].Start).TotalMilliseconds);
        }
        // flows are cut at the main window, which is only known at the end: store the raw visit runs by
        // the window that opened them, and let Finalize re-cut by the main window. To keep this additive, we
        // record every maximal run between visits of each candidate main window; the common case (one main
        // window) makes that exact. We approximate: cut at the window with the most time inside THIS session.
        var main = visits.GroupBy(v => v.Win).OrderByDescending(g => g.Sum(v => (v.End - v.Start).TotalSeconds)).ThenByDescending(g => g.Count()).First().Key;
        var i2 = 0;
        while (i2 < visits.Count)
        {
            if (visits[i2].Win == main) { i2++; continue; }
            var j = i2;
            while (j < visits.Count && visits[j].Win != main) j++;
            Record(state, visits.GetRange(i2, j - i2));
            i2 = j;
        }
    }

    public static (List<FlowStat> flows, List<NavEdge> nav, string? main) Finalize(LearnState state)
    {
        var main = state.Windows.OrderByDescending(w => w.Value.Seconds).ThenByDescending(w => w.Value.Visits).Select(w => w.Key).FirstOrDefault();
        var flows = new List<FlowStat>();
        foreach (var (key, f) in state.Flows)
        {
            if (key == main) continue;
            var s = new FlowStat { Key = key, Count = f.Count, MedianMs = Median(f.Durations), Evidence = f.Evidence };
            var best = f.Paths.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
            s.CommonPath = best.Key.Split('\u0001').ToList();
            s.CommonPathCount = best.Value;
            if (f.Backs.Count > 0)
            {
                var b = f.Backs.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
                s.BackFrom = b.Key; s.BackFromCount = b.Value;
            }
            foreach (var (win, ctr) in f.Controls)
                s.StepControls[win] = ctr.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(6).Select(c => (c.Key, c.Value)).ToList();
            flows.Add(s);
        }
        var nav = state.Nav.Select(n =>
        {
            var parts = n.Key.Split('\u0001');
            return new NavEdge { From = parts[0], To = parts[1], Count = n.Value.Count, MedianMs = Median(n.Value.Durations) };
        }).OrderByDescending(n => n.Count).ThenBy(n => n.From).ThenBy(n => n.To).ToList();
        return (flows.OrderByDescending(f => f.Count).ThenBy(f => f.Key).ToList(), nav, main);
    }

    internal static List<Visit> Visits(List<UiEvent> session)
    {
        var visits = new List<Visit>();
        Visit? cur = null;
        foreach (var e in session)
        {
            var key = Titles.Key(e.Win);
            if (key.Length == 0) continue;
            if (cur is null || cur.Win != key)
            {
                cur = new Visit { Win = key, Start = e.T, End = e.T, Source = e.Source };
                visits.Add(cur);
            }
            cur.End = e.T;
            if (e.Act is Acts.Invoke or Acts.Value or Acts.Select && !string.IsNullOrEmpty(e.Name)) cur.Controls.Add(e.Name);
        }
        return visits;
    }

    private static void Record(LearnState state, List<Visit> steps)
    {
        if (steps.Count == 0) return;
        var key = steps[0].Win;
        if (!state.Flows.TryGetValue(key, out var f)) state.Flows[key] = f = new FlowAcc();
        f.Count++;
        f.Evidence.Add(steps[0].Source);
        if (f.Durations.Count < MaxDurations) f.Durations.Add((steps[^1].End - steps[0].Start).TotalMilliseconds);
        var path = string.Join('\u0001', steps.Select(s => s.Win));
        f.Paths[path] = f.Paths.GetValueOrDefault(path) + 1;
        for (var i = 1; i < steps.Count - 1; i++)
            if (steps[i + 1].Win == steps[i - 1].Win) f.Backs[steps[i].Win] = f.Backs.GetValueOrDefault(steps[i].Win) + 1;
        foreach (var s in steps)
        {
            if (!f.Controls.TryGetValue(s.Win, out var ctr)) f.Controls[s.Win] = ctr = new Dictionary<string, int>();
            foreach (var c in s.Controls) ctr[c] = ctr.GetValueOrDefault(c) + 1;
        }
    }

    public static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        var m = s.Count / 2;
        return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2.0;
    }
}
