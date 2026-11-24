using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

public sealed class Visit
{
    public string Win = "";
    public DateTime Start, End;
    public List<string> Controls = new();
    public string Source = "";
    /// <summary>A close event of a tab document ("Window › Tab") happened during this visit: the document is gone.</summary>
    public bool ClosedDocument;
    /// <summary>The window appeared with no click, menu pick or selection in the two seconds before it: the program showed it on its own.</summary>
    public bool ShownByProgram;
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
    /// <summary>A window that opens later than this after the user's last click, menu pick or selection was shown by the program (a notes popup when a record loads).</summary>
    public static readonly TimeSpan ProgramShown = TimeSpan.FromSeconds(2);
    /// <summary>How long after a document's close a press credited to it still belongs to that visit (the press that closed it, written late).</summary>
    public const double ClosedPressSeconds = 1.5;
    /// <summary>How far before a document's late open the press or tab select that opened it is looked for.</summary>
    public static readonly TimeSpan LateOpen = TimeSpan.FromSeconds(30);
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

    /// <summary>
    /// Sessions of one PC each: events of the same source machine (the tag in "events/2026-09-01.&lt;machine&gt;.jsonl" after a merge;
    /// empty for the PC's own files) are cut at gaps of SessionGap on their own, so users working at the same time on different PCs
    /// stay separate sessions instead of melting into one working day. Ordered by start time.
    /// </summary>
    public static List<List<UiEvent>> Sessions(List<UiEvent> events)
    {
        var sessions = new List<(string machine, List<UiEvent> list)>();
        var cur = new Dictionary<string, (List<UiEvent> list, DateTime last)>();
        foreach (var e in events.OrderBy(e => e.T))
        {
            var m = MachineOf(e.Source);
            if (!cur.TryGetValue(m, out var c) || e.T - c.last > SessionGap)
            {
                c = (new List<UiEvent>(), e.T);
                sessions.Add((m, c.list));
            }
            c.list.Add(e);
            cur[m] = (c.list, e.T);
        }
        return sessions.OrderBy(s => s.list[0].T).ThenBy(s => s.machine, StringComparer.Ordinal).Select(s => s.list).ToList();
    }

    /// <summary>The PC tag of an event's source ("events/2026-09-01.a1b2c3d4.jsonl#L7" gives "a1b2c3d4"); empty for a day file of this PC.</summary>
    public static string MachineOf(string? source)
    {
        if (string.IsNullOrEmpty(source) || !source.StartsWith("events/", StringComparison.Ordinal)) return "";
        var end = source.IndexOf('#');
        var name = end < 0 ? source[7..] : source[7..end];
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return name.Length > 11 && name[10] == '.' ? name[11..] : "";
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
        // a window the program showed on its own (no click before it) is neither a visit nor the start of a flow: the visits around it
        // join. The main window, prompts and the session windows are never that (returning to the main window needs no click).
        bool IsDialogWin(string win) => dialogWindows is not null && dialogWindows.Contains(win);
        var mainOfAll = MainWindow(all.Where(v => !IsDialogWin(v.Win) && (sessionWindows is null || !sessionWindows.Contains(v.Win))).GroupBy(v => v.Win).Select(g => (g.Key, g.Sum(v => (v.End - v.Start).TotalSeconds), g.Count())));
        all = WithoutProgramShown(all, win => IsDialogWin(win) || win == mainOfAll || (sessionWindows is not null && sessionWindows.Contains(win)), mainOfAll);
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
    public static List<List<Visit>> Runs(List<Visit> visits, string? main, Func<Visit, bool> isDialog)
    {
        var runs = new List<List<Visit>>();
        var cur = new List<Visit>();
        void Cut() { if (cur.Count > 0) { runs.Add(cur); cur = new List<Visit>(); } }
        for (var i = 0; i < visits.Count; i++)
        {
            var v = visits[i];
            if (v.Win == main) { Cut(); continue; }
            cur.Add(v);
            if (v.ClosedDocument)
            {
                // the close of a document seen on its own (the Exit press was credited to the main window in between) belongs to the
                // run that was working in that document: the flow runs from the document's open to its close
                if (cur.Count(x => !isDialog(x)) == 1 && v.Controls.Count == 0 && runs.Count > 0 && runs[^1].LastOrDefault(x => !isDialog(x))?.Win == v.Win) { runs[^1].AddRange(cur); cur.Clear(); continue; }
                Cut(); continue;
            }
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
        Acts.Invoke => (e.Ct == "MenuItem" ? "menu " : "press ") + Titles.Name(e.FieldKey), // masked like a title, as in screens.md
        Acts.Value => "type " + e.FieldKey,
        Acts.Select => (e.Ct == "TabItem" ? "tab " : "pick ") + e.FieldKey,
        _ => e.FieldKey,
    };

    public static List<Visit> Visits(List<UiEvent> session)
    {
        var visits = new List<Visit>();
        Visit? cur = null;
        var lastClick = DateTime.MinValue;
        var closed = new Dictionary<string, Visit>(StringComparer.Ordinal); // documents whose close was seen, by key
        var (moved, startAt) = LateDocumentStarts(session);
        for (var i = 0; i < session.Count; i++)
        {
            var e = session[i];
            var key = Titles.Key(e.Win);
            var click = e.Act is Acts.Invoke or Acts.Select or Acts.Menu or Acts.Dropdown;
            if (key.Length == 0) { if (click) lastClick = e.T; continue; }
            // an event on the main window between the press that opened a document and the document's late open is the document's
            var movedHere = moved.TryGetValue(i, out var movedKey);
            if (movedHere) key = movedKey!;
            // the document's own tab selected (the strip read when the document comes in front) is the program's, not a person's click:
            // a popup the program opens right after it (the vendor's notes when a vendor loads) is still shown by the program
            var ownTab = e.Act == Acts.Select && e.Ct == "TabItem" && (movedHere ? NamesDocument(e, key) : IsOwnTab(e, key, cur));
            // a value credited to the main window within two seconds of the document in front of it (the program's fills, a cell read late) stays with the document
            if (cur is not null && e.Act == Acts.Value && cur.Win.StartsWith(key + " › ", StringComparison.Ordinal) && (e.T - cur.End).TotalSeconds <= 2) key = cur.Win;
            // the main window's strip selecting the tab of the document in front (the strip is read after the document opened) is that document
            // (the tab's name keyed as the document's title would be, so both are masked alike)
            if (cur is not null && e.Act == Acts.Select && !string.IsNullOrEmpty(e.Name) && cur.Win.StartsWith(key + " › ", StringComparison.Ordinal)
                && cur.Win == Titles.Key(e.Win + " › " + e.Name)) key = cur.Win;
            // a press on a document whose close was already seen (Exit is known only when the window is gone, and is written after
            // the close): it ends that visit, it is not a new one
            if (e.Act == Acts.Invoke && (cur is null || cur.Win != key) && closed.TryGetValue(key, out var gone) && (e.T - gone.End).TotalSeconds is >= -0.5 and <= ClosedPressSeconds)
            {
                if (e.FieldKey.Length > 0) gone.Controls.Add(Verb(e));
                lastClick = e.T;
                continue;
            }
            if (cur is null || cur.Win != key)
            {
                cur = new Visit { Win = key, Start = startAt.TryGetValue(i, out var opened) ? opened : e.T, End = e.T, Source = e.Source };
                // a window that appears (open, foreground) with no click, menu pick or selection in the two seconds before it was shown by the program
                cur.ShownByProgram = visits.Count > 0 && !click && e.T - lastClick > ProgramShown && !key.Contains(" › ", StringComparison.Ordinal);
                visits.Add(cur);
            }
            if (click && !ownTab) lastClick = e.T;
            cur.End = e.T;
            // what the program filled in is not a step a person took
            // (nor is the main window's strip showing the new document's tab before the document had its title)
            if (e.Act is Acts.Invoke or Acts.Value or Acts.Select && e.FieldKey.Length > 0 && !(e.Act == Acts.Value && e.By == ValueBy.App) && !(movedHere && ownTab)) cur.Controls.Add(Verb(e));
            if (e.Act == Acts.Close && e.Ct == "Window" && Titles.Key(e.Name) == key && key.Contains(" › ", StringComparison.Ordinal)) { cur.ClosedDocument = true; closed[key] = cur; }
        }
        // a window the program showed once on its own is its popup every time it appears in the session (a flicker of focus
        // between it and the window behind it starts no visit either)
        var programWindows = new HashSet<string>(visits.Where(v => v.ShownByProgram).Select(v => v.Win), StringComparer.Ordinal);
        foreach (var v in visits.Skip(1)) if (programWindows.Contains(v.Win)) v.ShownByProgram = true;
        return visits;
    }

    /// <summary>
    /// A document of a tabbed workspace gets its title late, so its open is written at the first input inside it (or at its close
    /// when the user only looked). What is seen on time is the main window's press or menu item that opened it and, within a
    /// second, the main window's strip selecting the document's tab. For each document visit that begins right after main-window
    /// events, this looks back up to <see cref="LateOpen"/> for that tab select or that press (named as the document), with only reads, fills, typing, selections and tips on the main window in between (no other
    /// press, no menu, no other window). When found, the visit starts at that event, and the main-window events after it are the
    /// document's. The anchor itself stays on the main window, so the return to the main window that ends the flow before is kept.
    /// Returns the moved events (index to document key) and the start time of the visit, keyed by the index of its first event.
    /// </summary>
    private static (Dictionary<int, string> moved, Dictionary<int, DateTime> startAt) LateDocumentStarts(List<UiEvent> session)
    {
        var moved = new Dictionary<int, string>();
        var startAt = new Dictionary<int, DateTime>();
        var keys = session.Select(e => Titles.Key(e.Win)).ToList();
        var prevKey = "";
        for (var i = 0; i < session.Count; i++)
        {
            var key = keys[i];
            if (key.Length == 0) continue;
            var before = prevKey;
            prevKey = key;
            var cut = key.IndexOf(" › ", StringComparison.Ordinal);
            if (cut <= 0 || before != key[..cut]) continue; // only a document that comes right after its main window
            var main = before;
            var anchor = -1;
            for (var b = i - 1; b >= 0; b--)
            {
                var e = session[b];
                if (session[i].T - e.T > LateOpen) break;
                if (keys[b].Length == 0) { if (e.Act is Acts.Invoke or Acts.Menu) break; continue; }
                if (keys[b] != main) break;
                if (e.Act == Acts.Invoke)
                {
                    if (NamesDocument(e, key)) anchor = b;
                    break; // a press of anything else: the user did something else before the document came
                }
                if (e.Act == Acts.Select)
                {
                    // the strip showing the document's tab; a press of it just before is looked for further back
                    if (e.Ct == "TabItem" && NamesDocument(e, key)) anchor = b;
                    continue;
                }
                if (e.Act is Acts.Value or Acts.Focus or Acts.Foreground or Acts.Open or Acts.Hint or Acts.Result or Acts.Dropdown) continue;
                break; // a menu opened, a close, a prompt: not the way to this document
            }
            if (anchor < 0) continue;
            var first = i;
            for (var m = i - 1; m > anchor; m--)
                if (keys[m] == main) { moved[m] = key; first = m; }
            startAt[first] = session[anchor].T;
        }
        return (moved, startAt);
    }

    /// <summary>A control of the main window named as the document: its title keyed on the main window's title is the document's key.</summary>
    private static bool NamesDocument(UiEvent e, string docKey) => !string.IsNullOrWhiteSpace(e.Name) && Titles.Key(e.Win + " › " + e.Name) == docKey;

    /// <summary>A tab item named as the document it belongs to: on the document ("Main › Order" selecting "Order") or on the main window's strip while that document is in front.</summary>
    private static bool IsOwnTab(UiEvent e, string key, Visit? cur)
    {
        if (string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Win)) return false;
        var cut = e.Win.LastIndexOf(" › ", StringComparison.Ordinal);
        if (cut > 0 && key.Contains(" › ", StringComparison.Ordinal) && Titles.Key(e.Win[..cut] + " › " + e.Name) == key) return true;
        return cur is not null && cur.Win.StartsWith(key + " › ", StringComparison.Ordinal) && cur.Win == Titles.Key(e.Win + " › " + e.Name);
    }

    /// <summary>
    /// Drops the visits of windows the program showed on its own (except prompts, which the flow rules handle) and joins the
    /// visits around them when they are the same window, so "Main → Notes → Main" is one stay on Main.
    /// </summary>
    public static List<Visit> WithoutProgramShown(List<Visit> visits, Func<string, bool> isDialog, string? main = null)
    {
        var result = new List<Visit>();
        bool Dropped(Visit x) => x.ShownByProgram && !isDialog(x.Win);
        for (var i = 0; i < visits.Count; i++)
        {
            var v = visits[i];
            if (Dropped(v)) continue;
            if (main is not null && v.Win == main && result.Count > 0 && result[^1].Win != main && !result[^1].ClosedDocument)
            {
                // the next visit that stays (a popup the program showed in between is not one)
                var next = visits.Skip(i + 1).FirstOrDefault(x => !Dropped(x));
                if (next is not null && next.Win == result[^1].Win)
                {
                    // the main window flickering in front for under a second with nothing done on it, between two visits of the same
                    // document (a prompt closing, a popup going): the document stayed open, its visit goes on
                    if (v.Controls.Count == 0 && (v.End - v.Start).TotalSeconds < 1) continue;
                    // only tab selections credited to the main window between two visits of the same open document: the document's
                    // own sub-tabs, read on the main window's tree; they are steps of that document's visit, which goes on
                    if (v.Controls.Count > 0 && v.Controls.All(c => c.StartsWith("tab ", StringComparison.Ordinal)) && result[^1].Win.StartsWith(main + " › ", StringComparison.Ordinal))
                    {
                        result[^1].Controls.AddRange(v.Controls);
                        continue;
                    }
                }
            }
            if (result.Count > 0 && result[^1].Win == v.Win && !isDialog(v.Win))
            {
                var prev = result[^1];
                prev.End = v.End;
                prev.Controls.AddRange(v.Controls);
                prev.ClosedDocument |= v.ClosedDocument;
                continue;
            }
            result.Add(v);
        }
        return result;
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
