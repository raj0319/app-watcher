using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

internal sealed class Visit
{
    public string Win = "";
    public DateTime Start, End;
    public List<string> Controls = new();
    public string Source = "";
    /// <summary>A close event of a tab document ("Window › Tab") happened during this visit: the document is gone.</summary>
    public bool ClosedDocument;
}

/// <summary>
/// Groups events into sessions and flows by window sequence. Accumulates per session into LearnState.
/// A flow is what a person does between two returns to the main window. It also ends when a document closes:
/// a prompt ("Close?") answered and a different window after it, a tab document's close event, or an Exit or
/// Cancel press followed by the main window. A run that is still longer than 20 minutes and 40 steps is split at
/// every prompt inside it, so one working day never becomes one flow.
/// </summary>
public static class FlowLearner
{
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan LongFlow = TimeSpan.FromMinutes(20);
    public const int LongFlowSteps = 40;
    private const int MaxDurations = 2000;
    private static readonly HashSet<string> LeaveButtons = new(StringComparer.OrdinalIgnoreCase) { "exit", "cancel", "close", "quit", "abandon", "discard" };

    /// <summary>Whole-history convenience wrapper (used by tests and the XES writer).</summary>
    public static (List<FlowStat> flows, List<NavEdge> nav, string? main, int sessions) Learn(List<UiEvent> events, HashSet<string>? sessionWindows = null, HashSet<string>? dialogWindows = null)
    {
        var state = new LearnState();
        foreach (var s in Sessions(events)) { state.Sessions++; Accumulate(state, s, sessionWindows, dialogWindows); }
        var (flows, nav, main) = Finalize(state, sessionWindows);
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

    /// <summary>
    /// The main window among window keys: the parent of the tab documents ("Window › Tab" share the prefix "Window") when
    /// there are any, else the window with the most time. Tabbed workspaces credit most time to their documents, so the
    /// prefix rule comes first.
    /// </summary>
    public static string? MainWindow(IEnumerable<(string win, double seconds, int visits)> windows)
    {
        var list = windows.ToList();
        if (list.Count == 0) return null;
        var prefixes = list.Where(w => w.win.Contains(" › ", StringComparison.Ordinal)).Select(w => w.win[..w.win.IndexOf(" › ", StringComparison.Ordinal)])
            .GroupBy(p => p).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        if (prefixes.Count > 0) return prefixes[0];
        return list.OrderByDescending(w => w.seconds).ThenByDescending(w => w.visits).ThenBy(w => w.win, StringComparer.Ordinal).First().win;
    }

    public static void Accumulate(LearnState state, List<UiEvent> session, HashSet<string>? sessionWindows = null, HashSet<string>? dialogWindows = null)
    {
        var all = Visits(session);
        if (all.Count == 0) return;
        foreach (var v in all)
        {
            if (!state.Windows.TryGetValue(v.Win, out var w)) state.Windows[v.Win] = w = new WindowAcc();
            w.Seconds += (v.End - v.Start).TotalSeconds;
            w.Visits++;
        }
        for (var i = 1; i < all.Count; i++)
        {
            var k = LearnState.Key(all[i - 1].Win, all[i].Win);
            if (!state.Nav.TryGetValue(k, out var n)) state.Nav[k] = n = new NavAcc();
            n.Count++;
            if (n.Durations.Count < MaxDurations) n.Durations.Add((all[i - 1].End - all[i - 1].Start).TotalMilliseconds);
        }
        // flows start at the first working screen: splash, login and station prompts are left out
        var visits = sessionWindows is null ? all : all.Where(v => !sessionWindows.Contains(v.Win)).ToList();
        if (visits.Count == 0) return;
        bool IsDialog(Visit v) => dialogWindows is not null && dialogWindows.Contains(v.Win);
        var main = MainWindow(visits.Where(v => !IsDialog(v)).GroupBy(v => v.Win).Select(g => (g.Key, g.Sum(v => (v.End - v.Start).TotalSeconds), g.Count())));
        foreach (var run in Runs(visits, main, IsDialog)) Record(state, run.Where(v => !IsDialog(v)).ToList());
    }

    /// <summary>Cuts the visits into runs at the points where a flow ends; a long run is split at its prompts.</summary>
    internal static List<List<Visit>> Runs(List<Visit> visits, string? main, Func<Visit, bool> isDialog)
    {
        var runs = new List<List<Visit>>();
        var cur = new List<Visit>();
        void Cut() { if (cur.Count > 0) { runs.Add(cur); cur = new List<Visit>(); } }
        for (var i = 0; i < visits.Count; i++)
        {
            var v = visits[i];
            if (v.Win == main) { Cut(); continue; }
            cur.Add(v);
            if (v.ClosedDocument) { Cut(); continue; }
            // a prompt answered, and the window after it is not the one before it: the document closed
            if (isDialog(v) && i + 1 < visits.Count)
            {
                var before = cur.Count >= 2 ? cur[^2].Win : "";
                if (before.Length > 0 && visits[i + 1].Win != before) Cut();
            }
            // Exit or Cancel, then the main window (seen as the next visit) ends the flow
            if (i + 1 < visits.Count && visits[i + 1].Win == main && v.Controls.Any(c => c.StartsWith("press ", StringComparison.Ordinal) && LeaveButtons.Contains(c[6..]))) Cut();
        }
        Cut();
        var result = new List<List<Visit>>();
        foreach (var run in runs)
        {
            var steps = run.Where(v => !isDialog(v)).Sum(v => 1 + v.Controls.Count); // screens visited plus what was done on them
            if (steps > LongFlowSteps && run[^1].End - run[0].Start > LongFlow)
            {
                var part = new List<Visit>();
                foreach (var v in run)
                {
                    part.Add(v);
                    if (isDialog(v)) { result.Add(part); part = new List<Visit>(); }
                }
                if (part.Count > 0) result.Add(part);
            }
            else result.Add(run);
        }
        return result.Where(r => r.Any(v => !isDialog(v))).ToList();
    }

    public static (List<FlowStat> flows, List<NavEdge> nav, string? main) Finalize(LearnState state, HashSet<string>? sessionWindows = null)
    {
        var main = MainWindow(state.Windows.Where(w => sessionWindows is null || !sessionWindows.Contains(w.Key)).Select(w => (w.Key, w.Value.Seconds, w.Value.Visits)));
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
                s.StepControls[win] = ctr.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(8).Select(c => (c.Key, c.Value)).ToList();
            flows.Add(s);
        }
        var nav = state.Nav.Select(n =>
        {
            var parts = n.Key.Split('\u0001');
            return new NavEdge { From = parts[0], To = parts[1], Count = n.Value.Count, MedianMs = Median(n.Value.Durations) };
        }).OrderByDescending(n => n.Count).ThenBy(n => n.From).ThenBy(n => n.To).ToList();
        return (flows.OrderByDescending(f => f.Count).ThenBy(f => f.Key).ToList(), nav, main);
    }

    /// <summary>"press Save", "type Customer", "pick Status": the step actions a flow is made of.</summary>
    public static string Verb(UiEvent e) => e.Act switch
    {
        Acts.Invoke => (e.Ct == "MenuItem" ? "menu " : "press ") + e.FieldKey,
        Acts.Value => "type " + e.FieldKey,
        Acts.Select => (e.Ct == "TabItem" ? "tab " : "pick ") + e.FieldKey,
        _ => e.FieldKey,
    };

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
            if (e.Act is Acts.Invoke or Acts.Value or Acts.Select && e.FieldKey.Length > 0) cur.Controls.Add(Verb(e));
            if (e.Act == Acts.Close && e.Ct == "Window" && Titles.Key(e.Name) == key && key.Contains(" › ", StringComparison.Ordinal)) cur.ClosedDocument = true;
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
