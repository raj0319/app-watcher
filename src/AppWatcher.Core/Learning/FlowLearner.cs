using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

internal sealed class Visit
{
    public string Win = "";
    public DateTime Start, End;
    public List<string> Controls = new();
    public string Source = "";
}

/// <summary>Groups events into sessions and flows by window sequence.</summary>
public static class FlowLearner
{
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    public static (List<FlowStat> flows, List<NavEdge> nav, string? main, int sessions) Learn(List<UiEvent> events)
    {
        var sessions = Sessions(events);
        var visitsPerSession = sessions.Select(Visits).Where(v => v.Count > 0).ToList();
        var main = MainWindow(visitsPerSession.SelectMany(v => v));

        var nav = new Dictionary<(string, string), int>();
        foreach (var visits in visitsPerSession)
            for (var i = 1; i < visits.Count; i++)
            {
                var k = (visits[i - 1].Win, visits[i].Win);
                nav[k] = nav.GetValueOrDefault(k) + 1;
            }

        var flows = new Dictionary<string, (FlowStat stat, List<double> durations, Dictionary<string, int> paths, Dictionary<string, int> backs, Dictionary<string, Dictionary<string, int>> controls)>();
        foreach (var visits in visitsPerSession)
        {
            var i = 0;
            while (i < visits.Count)
            {
                if (visits[i].Win == main) { i++; continue; }
                var j = i;
                while (j < visits.Count && visits[j].Win != main) j++;
                var steps = visits.GetRange(i, j - i);
                Record(flows, steps);
                i = j;
            }
        }

        var list = new List<FlowStat>();
        foreach (var (key, f) in flows)
        {
            var s = f.stat;
            s.Count = f.durations.Count;
            s.MedianMs = Median(f.durations);
            var best = f.paths.OrderByDescending(p => p.Value).First();
            s.CommonPath = best.Key.Split('\u0001').ToList();
            s.CommonPathCount = best.Value;
            if (f.backs.Count > 0)
            {
                var b = f.backs.OrderByDescending(p => p.Value).First();
                s.BackFrom = b.Key; s.BackFromCount = b.Value;
            }
            foreach (var (win, ctr) in f.controls)
                s.StepControls[win] = ctr.OrderByDescending(c => c.Value).Take(6).Select(c => (c.Key, c.Value)).ToList();
            list.Add(s);
        }
        var navList = nav.Select(n => new NavEdge { From = n.Key.Item1, To = n.Key.Item2, Count = n.Value }).OrderByDescending(n => n.Count).ToList();
        return (list.OrderByDescending(f => f.Count).ToList(), navList, main, sessions.Count);
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

    private static string? MainWindow(IEnumerable<Visit> visits)
    {
        return visits.GroupBy(v => v.Win)
            .Select(g => (win: g.Key, time: g.Sum(v => (v.End - v.Start).TotalSeconds), n: g.Count()))
            .OrderByDescending(x => x.time).ThenByDescending(x => x.n)
            .Select(x => x.win).FirstOrDefault();
    }

    private static void Record(Dictionary<string, (FlowStat, List<double>, Dictionary<string, int>, Dictionary<string, int>, Dictionary<string, Dictionary<string, int>>)> flows, List<Visit> steps)
    {
        if (steps.Count == 0) return;
        var key = steps[0].Win;
        if (!flows.TryGetValue(key, out var f))
        {
            f = (new FlowStat { Key = key }, new List<double>(), new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, Dictionary<string, int>>());
            flows[key] = f;
        }
        f.Item1.Evidence.Add(steps[0].Source);
        f.Item2.Add((steps[^1].End - steps[0].Start).TotalMilliseconds);
        var path = string.Join('\u0001', steps.Select(s => s.Win));
        f.Item3[path] = f.Item3.GetValueOrDefault(path) + 1;
        for (var i = 1; i < steps.Count - 1; i++)
            if (steps[i + 1].Win == steps[i - 1].Win) f.Item4[steps[i].Win] = f.Item4.GetValueOrDefault(steps[i].Win) + 1;
        foreach (var s in steps)
        {
            if (!f.Item5.TryGetValue(s.Win, out var ctr)) f.Item5[s.Win] = ctr = new Dictionary<string, int>();
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
