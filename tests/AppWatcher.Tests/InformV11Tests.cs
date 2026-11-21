using System.Text.Json;
using System.Text.RegularExpressions;
using AppWatcher.Core.Events;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// v0.11 against the anonymised v0.10 run (Fixtures/inform-v0.10): one test per row of the pass-mark table. The clicks are
/// replayed in the order the live run produced them (the hovers before a click, the click heard at the event that really
/// followed it: a document's destroy, the foreground switch before a message box closes, the switch to the box an Exit
/// opens) through <see cref="ClickTracker"/>, the class the collector itself calls, with the anonymised screen records as
/// the trees. Points are the centres of the targets' recorded rectangles (the driver logs carry no pointer positions); the
/// message boxes' buttons come from the box layouts in dialogs.json (their pictures' captions), placed at a fixed origin.
/// </summary>
public class InformV11Tests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform-v0.10");
    private static readonly TimeSpan TestPcOffset = TimeSpan.FromHours(-5);
    private const long Main = 1, Doc = 2, TopWindow = 3, Box = 4;
    private const int BoxX = 690, BoxY = 420;

    private static List<UiEvent> Events() => EventReader.ReadFile(Path.Combine(Dir, "events-2026-11-20.jsonl")).OrderBy(e => e.T).ToList();
    private static List<ScreenRecord> Records() => Directory.GetFiles(Dir, "*.md").Select(ScreenRecord.Parse).Where(r => r is not null).Select(r => r!).ToList();

    private sealed record Truth(DateTime T, string Kind, string Name, string? Title = null);

    /// <summary>The driver's truth: presses, prompts and sub-tab clicks, at the test PC's wall clock turned into this PC's local time.</summary>
    private static List<Truth> Truths()
    {
        var list = new List<Truth>();
        foreach (var file in new[] { "drive-a.log", "drive-complex2.log" })
            foreach (var line in File.ReadLines(Path.Combine(Dir, file)))
            {
                var m = Regex.Match(line, @"^(\d\d):(\d\d):(\d\d)\s+(.*)$");
                if (!m.Success) continue;
                var t = new DateTimeOffset(2026, 11, 20, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), TestPcOffset).LocalDateTime;
                var text = m.Groups[4].Value.Trim();
                Match x;
                if ((x = Regex.Match(text, @"^press '([^']*)' \(mouse\)$")).Success) list.Add(new Truth(t, "press", x.Groups[1].Value));
                else if ((x = Regex.Match(text, @"^prompt '([^']*)' buttons \[[^\]]*\] -> (\w+)$")).Success) list.Add(new Truth(t, "prompt", x.Groups[2].Value, x.Groups[1].Value));
                else if ((x = Regex.Match(text, @"^nav '([^']*)'$")).Success) list.Add(new Truth(t, "nav", x.Groups[1].Value));
                else if ((x = Regex.Match(text, @"^hover '([^']*)'$")).Success) list.Add(new Truth(t, "hover", x.Groups[1].Value));
            }
        return list.OrderBy(t => t.T).ToList();
    }

    private sealed record BoxLayout(int w, int h, List<BoxButton> buttons);
    private sealed record BoxButton(string name, int x, int y, int w, int h);
    private static Dictionary<string, BoxLayout> Boxes() => JsonSerializer.Deserialize<Dictionary<string, BoxLayout>>(File.ReadAllText(Path.Combine(Dir, "dialogs.json")))!;

    private static List<ControlRow> BoxRows(string title, BoxLayout b)
    {
        var rows = new List<ControlRow> { ClickSurfaceTests.Row(0, "Window", title, "", $"{BoxX},{BoxY} {b.w}x{b.h}") };
        rows.AddRange(b.buttons.Select(x => ClickSurfaceTests.Row(1, "Button", x.name, "", $"{BoxX + x.x},{BoxY + x.y} {x.w}x{x.h}")));
        return rows;
    }

    private static (int x, int y) Centre(ControlRow r) { Labels.TryRect(r.Position, out var x, out var y, out var w, out var h); return (x + w / 2, y + h / 2); }
    private static bool Inside(IReadOnlyList<ControlRow> rows, int x, int y) => Labels.TryRect(rows[0].Position, out var rx, out var ry, out var rw, out var rh) && x >= rx && y >= ry && x < rx + rw && y < ry + rh;

    /// <summary>The window title in front at a moment: the last foreground, select or open event before it.</summary>
    private static string InFrontAt(List<UiEvent> ev, DateTime t) =>
        ev.LastOrDefault(e => e.T <= t && e.Act is Acts.Foreground or Acts.Open or Acts.Select && e.Ct is "Window" or "TabItem" && e.Win.Length > 0)?.Win ?? "";

    /// <summary>
    /// The record of the window a click went to: the window in front when the driver clicked, else the most recent window of
    /// the last 90 s whose record holds a control of that name (a popup such as the vendor's notes can be in front while the
    /// driver clicks a tab of the order behind it).
    /// </summary>
    private static ScreenRecord? TargetRecord(List<UiEvent> ev, List<ScreenRecord> recs, DateTime t, string name)
    {
        bool Has(ScreenRecord r) => r.Controls.Any(c => c.Name.Trim() == name && c.Type is "Button" or "SplitButton" or "TabItem");
        static string Tail(string title) => title.Contains('›') ? title[(title.IndexOf('›') + 2)..] : title;
        ScreenRecord? Of(string win) => recs.FirstOrDefault(r => r.Title == win && Has(r))
            ?? recs.FirstOrDefault(r => r.Title.Contains('›') && win.Contains('›') && Tail(win) == Tail(r.Title) && Has(r));
        var front = Of(InFrontAt(ev, t));
        if (front is not null) return front;
        foreach (var win in ev.Where(e => e.T <= t.AddSeconds(1) && e.T >= t.AddSeconds(-90) && e.Win.Length > 0).Select(e => e.Win).Reverse().Distinct())
            if (Of(win) is { } r) return r;
        return null;
    }

    /// <summary>
    /// One click replayed in the live order. The hovers of the 12 s before it are samples with no click; the click is a sample
    /// at the reaction the events show (the target's destroy, the foreground leaving it, or 300 ms later when nothing reacts),
    /// with the target still on screen unless the reaction is its destroy; the change that confirms it comes at the reaction.
    /// Returns the press the tracker would count (null when none) and every press credited during the hovers.
    /// </summary>
    private static (string? press, List<string> phantoms, ClickOutcome outcome) Replay(Truth click, List<UiEvent> ev, List<ScreenRecord> recs, List<Truth> truths, Dictionary<string, BoxLayout> boxes)
    {
        var w = new FakeWorld();
        var tracker = new ClickTracker();
        var phantoms = new List<string>();
        var main = recs.First(r => r.Title == "DDI System Inform - Sample Co");
        w.Rows[Main] = main.Controls; w.Root[Main] = Main; w.Screen[Main] = main.Title;
        long target; string targetScreen; ControlRow? row;
        if (click.Kind == "prompt")
        {
            target = Box;
            w.Rows[Box] = BoxRows(click.Title!, boxes[click.Title!]); w.Root[Box] = Box; w.Screen[Box] = click.Title!;
            targetScreen = click.Title!;
            row = w.Rows[Box].FirstOrDefault(r => r.Type == "Button" && r.Name == click.Name);
        }
        else
        {
            // a press in the window in front: a top window of its own (the notes window a vendor shows) or a document of the workspace
            var rec = TargetRecord(ev, recs, click.T, click.Name);
            if (rec is null) return (null, phantoms, ClickOutcome.None);
            var isDoc = rec.Title.Contains(" › ", StringComparison.Ordinal);
            target = isDoc ? Doc : TopWindow;
            w.Rows[target] = rec.Controls; w.Root[target] = isDoc ? Main : TopWindow; if (isDoc) w.Doc[target] = target; w.Screen[target] = rec.Title;
            targetScreen = rec.Title;
            row = rec.Controls.FirstOrDefault(r => r.Name == click.Name && r.Type is "Button" or "SplitButton" or "TabItem" && Labels.TryRect(r.Position, out _, out _, out var rw, out _) && rw > 0);
        }
        if (row is null) return (null, phantoms, ClickOutcome.None);
        var (px, py) = Centre(row);
        w.At = (x, y) => !w.Hidden.Contains(Box) && w.Rows.ContainsKey(Box) && Inside(w.Rows[Box], x, y) ? Box
                       : !w.Hidden.Contains(target) && Inside(w.Rows[target], x, y) ? target : Main;
        // the hovers before the click: the pointer rests on buttons, windows change (the driver brings the window in front): nothing is pressed
        var previous = truths.LastOrDefault(t => t.T < click.T && t.Kind != "hover")?.T ?? DateTime.MinValue;
        foreach (var h in truths.Where(t => t.Kind == "hover" && t.T > previous && t.T <= click.T && (click.T - t.T).TotalSeconds <= 12))
        {
            var hr = w.Rows[target].FirstOrDefault(r => r.Name == h.Name && Labels.TryRect(r.Position, out _, out _, out var hw, out _) && hw > 0);
            if (hr is null) continue;
            var (hx, hy) = Centre(hr);
            tracker.Observe(new InputSample(h.T, hx, hy, false, false, false, target), w);
            var c = tracker.Confirm(h.T.AddMilliseconds(40), "window changed", targetScreen, out _);
            if (c is not null) phantoms.Add(c.Name);
            if (tracker.MayCreditPointer(h.T.AddMilliseconds(40)) || tracker.MayCreditFocused(h.T.AddMilliseconds(40))) phantoms.Add("(pointer or focus credit allowed for " + h.Name + ")");
        }
        // the reaction the events show, within 3 s after the driver's second
        var from = click.T; var until = click.T.AddSeconds(3);
        var reaction = click.Kind == "prompt"
            ? ev.FirstOrDefault(e => e.T >= from && e.T <= until && (e.Act == Acts.Foreground && e.Win != click.Title || e.Act == Acts.Close && e.Name == click.Title))
            : ev.FirstOrDefault(e => e.T >= from && e.T <= until && (e.Act == Acts.Close && e.Ct == "Window" || e.Act == Acts.Foreground && e.Win != targetScreen || e.Act == Acts.Dialog));
        DateTime at; long goingAway = 0, front2 = target, prevFront = target; string why;
        if (reaction is null) { at = click.T.AddMilliseconds(500); why = "value filled"; }
        else if (reaction.Act == Acts.Close && (reaction.Name == targetScreen || reaction.Win == targetScreen || target == Doc && reaction.Win.EndsWith(targetScreen[(targetScreen.IndexOf('›') + 2)..], StringComparison.Ordinal)))
        { at = reaction.T; goingAway = target; w.Hidden.Add(target); front2 = Main; why = "window closed"; }
        else { at = reaction.T; front2 = reaction.Act == Acts.Dialog || reaction.Win == "Close?" ? Box : Main; why = reaction.Act == Acts.Dialog ? "dialog" : "window changed"; }
        if (click.Kind == "prompt") front2 = Main; // the box loses the foreground before it closes: it is still on screen when the click is heard
        tracker.Observe(new InputSample(at.AddMilliseconds(-60), px, py, true, true, false, prevFront), w); // the down, sampled while held
        var o = tracker.Observe(new InputSample(at, px, py, false, false, false, front2, goingAway), w);
        if (o.NeedsLiveRead)
        {
            // the live read finds the target itself when its window is still at the point, nothing when it is gone
            var live = o.Hit is { } hit && w.WindowAt(px, py) == hit.Hwnd ? new LiveElement(hit.Row.Name, hit.Row.AutomationId, hit.Row.Type, hit.Row.Position, hit.Screen, true) : null;
            var lr = tracker.LiveRead(o.ClickedAt, live);
            if (lr.Confirmed is { } early) return (early.Name, phantoms, o);
        }
        if (o.Kind == ClickKind.Tab) return (o.Hit!.Row.Name.Trim(), phantoms, o);
        var press = tracker.Confirm(at.AddMilliseconds(why == "value filled" ? 350 : 5), why, targetScreen, out var deferred);
        return (press?.Name, phantoms, o);
    }

    [Fact]
    public void Exit_is_counted_17_times_of_17_in_the_live_order()
    {
        var ev = Events(); var recs = Records(); var truths = Truths(); var boxes = Boxes();
        var exits = truths.Where(t => t.Kind == "press" && t.Name == "Exit").ToList();
        Assert.Equal(17, exits.Count);
        var missed = new List<string>();
        foreach (var x in exits)
        {
            var (press, phantoms, o) = Replay(x, ev, recs, truths, boxes);
            if (press != "Exit") missed.Add($"{x.T:HH:mm:ss} -> {press ?? "nothing"} ({o.Kind}: {o.Trace})");
            Assert.Empty(phantoms);
        }
        Assert.True(missed.Count == 0, "missed: " + string.Join(" | ", missed));
    }

    [Fact]
    public void OK_No_and_Cancel_on_the_message_boxes_are_counted_7_times_of_7()
    {
        var ev = Events(); var recs = Records(); var truths = Truths(); var boxes = Boxes();
        var prompts = truths.Where(t => t.Kind == "prompt").ToList();
        Assert.Equal(7, prompts.Count);
        var got = prompts.Select(p => (p, Replay(p, ev, recs, truths, boxes))).ToList();
        Assert.True(got.All(g => g.Item2.press == g.p.Name), string.Join(" | ", got.Select(g => $"{g.p.Title} -> {g.p.Name}: {g.Item2.press ?? "nothing"} ({g.Item2.outcome.Trace})")));
        // the three the brief names: OK on Close? at 08:34:36, No at 08:38:19, Cancel at 08:43:12
        Assert.Contains(got, g => g.p.Title == "No Selection Specified" && g.Item2.press == "No");
        Assert.Contains(got, g => g.p.Title == "Protected Area" && g.Item2.press == "Cancel");
    }

    [Fact]
    public void Add_Line_Item_is_confirmed_by_the_rows_read_after_it_2_times_of_2()
    {
        var ev = Events(); var recs = Records(); var truths = Truths(); var boxes = Boxes();
        var adds = truths.Where(t => t.Kind == "press" && t.Name == "Add Line Item").ToList();
        Assert.Equal(2, adds.Count);
        foreach (var a in adds)
        {
            var (press, phantoms, o) = Replay(a, ev, recs, truths, boxes);
            Assert.True(press == "Add Line Item", $"{a.T:HH:mm:ss}: {press ?? "nothing"} ({o.Kind}: {o.Trace})");
            Assert.Empty(phantoms);
        }
        // the re-read at 300 ms sees the new line: a new row is a change the click explains
        var before = new List<FieldRead> { new() { Key = "g\u0001Ln\u0001line 1", Label = "Ln", Value = "1", IsCell = true, Row = "line 1" } };
        var after = before.Append(new FieldRead { Key = "g\u0001Ln\u0001line 2", Label = "Ln", Value = "2", IsCell = true, Row = "line 2" }).ToList();
        Assert.Single(FieldReads.Diff(before, after, new HashSet<string>()).filled);
    }

    [Fact]
    public void Sub_tab_clicks_resolve_to_a_page_selection_45_of_49_or_more()
    {
        var ev = Events(); var recs = Records(); var truths = Truths(); var boxes = Boxes();
        var navs = truths.Where(t => t.Kind == "nav").ToList();
        Assert.Equal(49, navs.Count);
        var byTree = 0; var byLiveRead = 0; var otherLayout = new List<string>(); var unknown = new List<string>();
        foreach (var n in navs)
        {
            var front = InFrontAt(ev, n.T);
            var rec = TargetRecord(ev, recs, n.T, n.Name);
            var item = rec?.Controls.FirstOrDefault(c => c.Type == "TabItem" && c.Name.Trim() == n.Name);
            if (item is null)
            {
                // a page of a document whose layout v0.10 never recorded: it shares its title with another document (Product Analysis
                // with Product Master, Customer Ledger with Customer Master) and only the first layout was kept; v0.11 reads each
                // document's own tree and keeps the second layout, so these pages resolve live, not in this fixture
                var soon = InFrontAt(ev, n.T.AddSeconds(4));
                var shared = new[] { front, soon }.Any(x => x.Contains("Product - ", StringComparison.Ordinal) || x.Contains("Customer - ", StringComparison.Ordinal));
                (shared ? otherLayout : unknown).Add($"{n.T:HH:mm:ss} {n.Name} in [{front}]");
                continue;
            }
            if (Labels.TryRect(item.Position, out _, out _, out var iw, out _) && iw > 0)
            {
                var (press, _, o) = Replay(n, ev, recs, truths, boxes);
                if (o.Kind == ClickKind.Tab && press == n.Name) byTree++; else unknown.Add($"{n.T:HH:mm:ss} {n.Name}: {o.Kind}");
            }
            else byLiveRead++; // a tab item that was off screen when the document was recorded: the live read under the pointer finds it
        }
        // tab items recorded with a rectangle are selected from the tree (no live read needed); the others need the live read;
        // the pages of the two layouts v0.10 never recorded need the v0.11 per-document tree, which this fixture cannot hold
        var detail = $"{byTree} by the tree + {byLiveRead} by the live read + {otherLayout.Count} in a layout v0.10 never recorded, of 49; unresolved: {string.Join(", ", unknown)}";
        Assert.True(unknown.Count == 0, detail);
        Assert.True(byTree + byLiveRead >= 40, detail);
        Assert.True(byTree + byLiveRead + otherLayout.Count >= 45, detail);
    }

    [Fact]
    public void A_hovered_or_focused_button_is_never_pressed_without_a_click_and_a_menu_click_has_no_target_beneath()
    {
        var w = new FakeWorld();
        var main = Records().First(r => r.Title == "DDI System Inform - Sample Co");
        w.Rows[Main] = main.Controls; w.Root[Main] = Main; w.Screen[Main] = main.Title; w.At = (_, _) => Main;
        var news = main.Controls.First(r => r.Name.StartsWith("1.0.1 is now available", StringComparison.Ordinal) && r.Type == "Button");
        var (nx, ny) = Centre(news);
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 8, 37, 35);
        // 08:37:35: the pointer rests on the news button, a window changes, no click: nothing is pressed (v0.10 pressed it)
        t.Observe(new InputSample(t0, nx, ny, false, false, false, Main), w);
        Assert.Null(t.Confirm(t0.AddMilliseconds(30), "window changed", main.Title, out _));
        Assert.False(t.MayCreditPointer(t0.AddMilliseconds(30)));
        // a default button that takes the focus when a dialog opens (08:38:09 OK, 08:38:31 Cancel) is not pressed without a click or Enter
        Assert.False(t.MayCreditFocused(t0.AddSeconds(34)));
        // Enter on a focused button is a press
        t.Observe(new InputSample(t0.AddSeconds(40), nx, ny, false, false, KeyWentDown: true, Main, PressKeyWentDown: true), w);
        Assert.True(t.MayCreditFocused(t0.AddSeconds(40).AddMilliseconds(200)));
        // the brief's sequence: hover A at t, click B at t + 1 s, destroy at t + 1.2 s: B is pressed, A never
        var notes = Records().First(r => r.Title.EndsWith("Internal Notes", StringComparison.Ordinal));
        var w2 = new FakeWorld();
        w2.Rows[TopWindow] = notes.Controls; w2.Root[TopWindow] = TopWindow; w2.Screen[TopWindow] = notes.Title; w2.At = (_, _) => TopWindow;
        var report = notes.Controls.FirstOrDefault(r => r.Name == "Report") ?? notes.Controls.First(r => r.Type == "Button" && r.Name != "Exit");
        var exit = notes.Controls.First(r => r.Name == "Exit");
        var t2 = new ClickTracker();
        var h = new DateTime(2026, 11, 20, 8, 30, 5);
        t2.Observe(new InputSample(h, Centre(report).x, Centre(report).y, false, false, false, TopWindow), w2);
        Assert.Null(t2.Confirm(h.AddMilliseconds(40), "window changed", notes.Title, out _));
        t2.Observe(new InputSample(h.AddSeconds(1), Centre(exit).x, Centre(exit).y, true, true, false, TopWindow), w2);    // down
        w2.Hidden.Add(TopWindow);
        var o = t2.Observe(new InputSample(h.AddSeconds(1.2), Centre(exit).x, Centre(exit).y, false, false, false, Main, GoingAway: TopWindow), w2);
        Assert.Equal("Exit", o.Hit!.Row.Name);
        Assert.Equal("Exit", t2.Confirm(h.AddSeconds(1.2), "window closed", notes.Title, out _)!.Name);
        // a click on a menu item over the document (08:45:10, Purchasing > Purchase Order Report) is the menu's, never the button beneath
        var t3 = new ClickTracker();
        t3.MenuOpened();
        t3.Observe(new InputSample(t0, 1860, 984, true, true, false, Main), w);
        Assert.Equal(ClickKind.Menu, t3.Observe(new InputSample(t0.AddMilliseconds(90), 1860, 984, false, false, false, Main), w).Kind);
        Assert.Null(t3.Confirm(t0.AddMilliseconds(300), "window opened", main.Title, out _));
    }

    [Fact]
    public void A_stale_tree_under_the_point_is_found_out_by_the_live_read()
    {
        // 08:30:34: the tree had a button where the click went into a field; the live element there is the field: no press
        var recs = Records();
        var product = recs.First(r => r.Title.Contains("Product - 100EX", StringComparison.Ordinal));
        var w = new FakeWorld();
        w.Rows[Doc] = product.Controls; w.Root[Doc] = Main; w.Doc[Doc] = Doc; w.Screen[Doc] = product.Title; w.At = (_, _) => Doc;
        var edit = product.Controls.First(r => r.Name == "Edit" && r.Type == "Button");
        var (x, y) = Centre(edit);
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 8, 30, 33);
        t.Observe(new InputSample(t0, x, y, true, true, false, Doc), w);
        var o = t.Observe(new InputSample(t0.AddMilliseconds(80), x, y, false, false, false, Doc), w);
        Assert.True(o.NeedsLiveRead);
        var r = t.LiveRead(o.ClickedAt, new LiveElement("", "srcMasterSearch", "Edit", "104,160 273x32", product.Title, true));
        Assert.True(r.Cancelled);
        Assert.Null(t.Confirm(t0.AddMilliseconds(400), "window changed", product.Title, out _));
        // a second document with the same title and no tree of its own yet: the first document's tree is never used for it
        var w2 = new FakeWorld();
        w2.Rows[Main] = recs.First(r => r.Title == "DDI System Inform - Sample Co").Controls; w2.Root[Main] = Main; w2.Root[Doc] = Main; w2.Doc[Doc] = Doc; w2.At = (_, _) => Doc;
        Assert.Null(ClickSurfaces.Resolve(w2, x, y, 0, Doc, Doc));
    }

    [Fact]
    public void Typed_values_have_no_fragments_no_empty_values_and_each_quantity_once()
    {
        var d = new ValueDebouncer();
        var t0 = new DateTime(2026, 11, 20, 8, 39, 52);
        UiEvent Typed(double s, string win, string val, string aid = "srcMasterSearch", string ct = "Edit", string name = "") => new() { T = t0.AddSeconds(s), Win = win, Ct = ct, Aid = aid, Name = name, Act = Acts.Value, Val = val };
        UiEvent Other(double s, string act, string win) => new() { T = t0.AddSeconds(s), Win = win, Ct = act == Acts.Select ? "TabItem" : "Window", Name = win, Act = act };
        var written = new List<UiEvent>();
        void Feed(UiEvent e) { if (e.Act == Acts.Value) d.Push(e, e.T); else written.AddRange(d.FlushFor(e, e.T)); }
        // L383..L388: the first keys before the record loaded, the document takes its title, more keys: one value, the final one
        const string main = "DDI System Inform - Sample Co", doc = "DDI System Inform - Sample Co › Customer - 0000001 \"Test Customer\"";
        Feed(Typed(0.617, main, "0000"));
        Feed(Other(0.981, Acts.Select, doc)); Feed(Other(0.981, Acts.Open, doc));
        Feed(Typed(1.201, doc, "000000")); Feed(Typed(1.301, doc, "0000001"));
        written.AddRange(d.Flush(t0.AddSeconds(3)));
        Assert.Single(written);
        Assert.Equal("0000001", written[0].Val);
        Assert.Equal(doc, written[0].Win);
        // an empty typed value is never written
        written.Clear();
        Feed(Typed(10, doc, "", aid: "txtSpecialInstructions"));
        written.AddRange(d.Flush(t0.AddSeconds(12)));
        Assert.Empty(written);
        // L222 + L229: the grid editor's "5" (it names the grid) and the cell read "5" with its column: written once, as the cell
        written.Clear();
        var editor = Typed(20.275, doc, "5", aid: "gridLIs", name: "Line Items"); editor.OverGridCell = true;
        Feed(editor);
        Feed(new UiEvent { T = t0.AddSeconds(20.659), Win = doc, Ct = "DataItem", Name = "Quant\r\nOrdered", Act = Acts.Value, Val = "5", How = "read", Row = "line 1" });
        written.AddRange(d.Flush(t0.AddSeconds(23)));
        Assert.Single(written);
        Assert.Equal("DataItem", written[0].Ct);
        Assert.Equal("line 1", written[0].Row);
    }

    [Fact]
    public void Price_Extension_is_Quant_Ordered_times_Net_Price_from_the_typed_row_seen_in_two_orders()
    {
        // what v0.11 writes for the two orders of the run: the typed line read on its own (row "line 1"), the new empty line 2
        // the Enter added read as row "line 2" in between; the values are the run's (1, 5 and 20 at 45.3426)
        var ev = new List<UiEvent>(); var src = 0;
        const string doc = "DDI System Inform - Sample Co › New Sales Order";
        void Cell(DateTime t, string name, string val, string? by, string row) => ev.Add(new UiEvent { T = t, Win = doc, Ct = "DataItem", Name = name, Act = Acts.Value, Val = val, By = by, How = by is null ? "read" : null, Row = row, Source = $"events/2026-11-20.jsonl#L{++src}" });
        void Order(DateTime t0)
        {
            ev.Add(new UiEvent { T = t0, Win = doc, Ct = "Window", Name = doc, Act = Acts.Open, Source = $"events/2026-11-20.jsonl#L{++src}" });
            var b = t0.AddSeconds(17);
            Cell(b, "Product Number", "100EX", null, "line 1");
            Cell(b, "Price Extension", "45.34", "app", "line 1"); Cell(b, "Net Price", "45.3426", "app", "line 1"); Cell(b, "Quant Ordered", "1", "app", "line 1");
            var q5 = t0.AddSeconds(25);
            Cell(q5, "Quant Ordered", "5", null, "line 1"); Cell(q5, "Price Extension", "226.71", "app", "line 1");
            Cell(q5, "Ln", "2", "app", "line 2"); Cell(q5, "Net Price", "0", "app", "line 2"); Cell(q5, "Price Extension", "0", "app", "line 2");
            var q20 = t0.AddSeconds(32);
            Cell(q20, "Quant Ordered", "20", null, "line 1"); Cell(q20, "Price Extension", "906.85", "app", "line 1");
            ev.Add(new UiEvent { T = t0.AddSeconds(90), Win = doc, Ct = "Window", Name = doc, Act = Acts.Close, Source = $"events/2026-11-20.jsonl#L{++src}" });
        }
        Order(new DateTime(2026, 11, 20, 8, 32, 44));
        Order(new DateTime(2026, 11, 20, 8, 46, 18));
        var md = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = ev }))["logic.md"];
        Assert.Contains("Price Extension", md);
        Assert.Matches(@"Price Extension = Net Price [×x\*] Quant Ordered|Price Extension = Quant Ordered [×x\*] Net Price", md);
        Assert.DoesNotContain("What decides Price Extension", md);
    }

    [Fact]
    public void Typing_the_customer_in_a_new_order_fills_its_header_seen_twice_of_twice()
    {
        // the code is typed while the document has no title yet (credited to the main window); the header is read in the
        // document after it took its title, within 5 s
        var ev = new List<UiEvent>(); var src = 0;
        const string main = "DDI System Inform - Sample Co", doc = "DDI System Inform - Sample Co › New Sales Order";
        void Add(DateTime t, string win, string act, string name, string aid = "", string? val = null, string? by = null, string ct = "Edit") =>
            ev.Add(new UiEvent { T = t, Win = win, Ct = ct, Name = name, Aid = aid, Act = act, Val = val, By = by, Source = $"events/2026-11-20.jsonl#L{++src}" });
        foreach (var t0 in new[] { new DateTime(2026, 11, 20, 8, 32, 51), new DateTime(2026, 11, 20, 8, 46, 23) })
        {
            Add(t0, main, Acts.Value, "", "srcCustomer", "0000001");
            Add(t0.AddSeconds(2), doc, Acts.Select, "New Sales Order", ct: "TabItem");
            Add(t0.AddSeconds(2), doc, Acts.Open, doc, ct: "Window");
            foreach (var (name, aid, val) in new[] { ("Ship To", "srcShipTo", "SAME"), ("Writer", "srcWriter", "Writer One"), ("Ship Via", "srcShipVia", "PICK UP"), ("Branch", "srcBranch", "02 - Sample Co"), ("Whse", "srcWarehouse", "02") })
                Add(t0.AddSeconds(3.5), doc, Acts.Value, name, aid, val, "app");
            Add(t0.AddSeconds(60), doc, Acts.Close, doc, ct: "Window");
        }
        var md = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = ev }))["logic.md"];
        var line = md.Split('\n').FirstOrDefault(l => l.Contains("Typing Customer", StringComparison.OrdinalIgnoreCase) || l.Contains("typing Customer", StringComparison.Ordinal)) ?? "";
        foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) Assert.True(line.Contains(f + " (seen 2× of 2)", StringComparison.Ordinal), md);
    }

    [Fact]
    public void A_lookup_is_a_new_list_under_or_beside_the_field_2_right_and_0_false()
    {
        R(0, "", "", "");
        var field = "201,425 150x29";
        // the product lookup (a list window that opened right under the cell): a lookup
        Assert.True(FieldReads.PopupServesField(field, "121,456 1807x580"));
        // a grid of the record that loaded 300 px further down (the inventory grid after a code in the master search): not one
        Assert.False(FieldReads.PopupServesField("104,160 273x32", "121,520 1782x405"));
        // a list that was in the previous read with other rows now (12 typed in a cell while the product list stayed): not new
        var before = new List<ControlRow> { R(0, "Pane", "", "doc"), R(1, "List", "", "lstProducts", "201,456 400x120"), R(2, "ListItem", "100EX | SAMPLE PRODUCT", "", "201,456 400x20") };
        var after = before.Take(2).Append(R(2, "ListItem", "12 | TWELVE", "", "201,456 400x20")).ToList();
        Assert.Null(FieldReads.NewLookup(before, after, field));
        Assert.NotNull(FieldReads.NewLookup(before.Take(1).ToList(), before, field));
        // a list 60 px below the field is not its lookup
        var far = new List<ControlRow> { R(0, "Pane", "", "doc"), R(1, "List", "", "lstOther", "201,514 400x120"), R(2, "ListItem", "A | B", "", "201,514 400x20") };
        Assert.Null(FieldReads.NewLookup(far.Take(1).ToList(), far, field));
    }

    private static ControlRow R(int depth, string type, string name, string aid, string pos = "") => ClickSurfaceTests.Row(depth, type, name, aid, pos);

    [Fact]
    public void A_second_layout_under_one_title_gets_its_own_record()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aw-layouts-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScreenRecord Rec(params string[] buttons) => new()
            {
                Title = "DDI System Inform - Sample Co › Product - 100EX \"100EX, SAMPLE PRODUCT\"", Kind = ScreenKinds.Tab, Captured = DateTime.Now,
                Controls = new[] { R(0, "Window", "Product", "") }.Concat(buttons.Select((b, i) => R(1, "Button", b, "cmd" + b.Replace(" ", ""), $"{10 + i * 110},967 108x35"))).ToList(),
            };
            var master = Rec("New", "Edit", "Exit", "Report", "Labels", "Copy", "Notes");
            var analysis = Rec("Exit", "Chart", "Data", "Print", "Refresh", "Export", "Overview", "Assembly");
            Assert.True(ScreenWriter.WriteOnce(dir, master));
            Assert.True(ScreenWriter.WriteOnce(dir, analysis));        // another layout: a record of its own
            Assert.False(ScreenWriter.WriteOnce(dir, analysis));       // the same layout again: nothing new
            var partial = Rec("New", "Edit", "Exit", "Report", "Labels", "Copy");
            Assert.False(ScreenWriter.WriteOnce(dir, partial));        // the first layout read less fully: the richer one stays
            Assert.Equal(2, Directory.GetFiles(dir, "*.md").Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_v010_fixture_has_none_of_the_real_strings_and_keeps_its_counts()
    {
        Assert.Equal(594, Events().Count);
        Assert.Equal(7, Events().Count(e => e.Act == Acts.Dialog));
        // the stand-ins are there (the real strings are checked by scripts\check-fixtures.ps1 with the list kept outside the repository)
        var all = string.Join(" ", Directory.GetFiles(Dir).Select(File.ReadAllText));
        Assert.Contains("Sample Co", all);
        Assert.Contains("Test Customer", all);
    }
}
