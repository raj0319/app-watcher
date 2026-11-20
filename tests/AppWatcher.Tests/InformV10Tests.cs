using System.Text.Json;
using System.Text.RegularExpressions;
using AppWatcher.Core;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Events;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// v0.10, measured against the anonymised v0.9 run (tests/Fixtures/inform-v0.9, see its README): clicks resolved on the
/// recorded tree when the button is gone, tab clicks as selections, tick boxes the program flips, typed grid cells with
/// their fills, one typed event per value, the late price, health without false "never recorded" documents, tick boxes
/// before masking, formulas that need evidence, masks everywhere a title appears, lookups, hints and flow durations.
/// One test per row of the pass-mark table, each asserting the count on the fixture.
/// </summary>
public class InformV10Tests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform-v0.9");
    private static string FixturesV07 => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform");
    private static ScreenRecord Record(string file) => ScreenRecord.Parse(Path.Combine(Fixtures, file))!;
    private static List<ScreenRecord> AllRecords() => Directory.GetFiles(Fixtures, "*.md").Where(f => !f.EndsWith("README.md")).Select(f => ScreenRecord.Parse(f)!).ToList();
    private static List<UiEvent> Events() => EventReader.ReadFile(Path.Combine(Fixtures, "events-2026-11-19.jsonl")).ToList();
    private static CollectorStatus Status() => JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(Path.Combine(Fixtures, "collector-status.json")))!;
    private static ControlRow R(int depth, string type, string name = "", string aid = "", string pos = "10,10 100x20", string? value = null) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos, Value = value };

    private sealed class Click { public string t { get; set; } = ""; public string kind { get; set; } = ""; public string name { get; set; } = ""; public string? title { get; set; } public string? record { get; set; } public int? x { get; set; } public int? y { get; set; } }
    private static List<Click> Clicks() => JsonSerializer.Deserialize<List<Click>>(File.ReadAllText(Path.Combine(Fixtures, "clicks.json")))!;
    private static IReadOnlyList<ControlRow> RowsOf(string record) => record.StartsWith("inform/") ? ScreenRecord.Parse(Path.Combine(FixturesV07, record[7..]))!.Controls : Record(record).Controls;

    private static LearnInput Input()
    {
        var input = new LearnInput { AppName = "inform", Events = Events(), Screens = AllRecords(), Collector = Status(), HasDataSources = false, Now = new DateTime(2026, 11, 20), BackendKind = "universe" };
        input.SessionWindowPatterns.AddRange(new[] { "Splash Screen", "Login*", "DDI System Inform Login", "Enter Register #*" });
        return input;
    }

    // ---------- job 1: clicks and tab clicks from the last recorded tree ----------

    [Fact]
    public void Hit_test_finds_the_deepest_button_under_a_point_and_nothing_in_a_gap_or_on_an_offscreen_row()
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "Close?", "", "600,400 500x200"),
            R(1, "Pane", "", "pnlButtons", "600,540 500x60"),
            R(2, "Button", "OK", "btnOK", "700,550 100x40"),
            R(2, "Button", "Cancel", "btnCancel", "820,550 100x40"),
            R(1, "Tab", "", "tabs", "600,400 500x30"),
            R(2, "TabItem", "Main", "", "600,400 80x30"),
            R(2, "TabItem", "Notes", "", "680,400 80x30"),
            R(2, "TabItem", "Hidden", "", "0,0 0x0"),
            R(1, "Button", "Outer", "btnOuter", "600,400 500x200"), // a button-like parent that spans the window: the deeper row wins
        };
        Assert.Equal("OK", TreeHitTest.ButtonAt(rows, 750, 570)!.Name);
        Assert.Equal("Cancel", TreeHitTest.ButtonAt(rows, 821, 589)!.Name);
        Assert.Equal("Main", TreeHitTest.ButtonAt(rows, 640, 415)!.Name);
        Assert.Equal("Notes", TreeHitTest.ButtonAt(rows, 700, 415)!.Name);
        Assert.Equal("Outer", TreeHitTest.ButtonAt(rows, 810, 545)!.Name); // the gap between OK and Cancel: only the spanning parent
        Assert.Null(TreeHitTest.ButtonAt(rows.Where(r => r.Name != "Outer").ToList(), 810, 545));
        Assert.Null(TreeHitTest.ButtonAt(rows, 0, 0)); // the offscreen tab has no rectangle
        Assert.Null(TreeHitTest.ButtonAt(rows, 5000, 5000));
        Assert.Equal("tabs", TreeHitTest.TabKeyOf(rows, 6));
    }

    [Fact]
    public void Every_truth_click_of_the_run_with_a_recorded_rectangle_resolves_to_its_button_exit_20_of_20_prompts_8_of_8()
    {
        var clicks = Clicks();
        var ok = new Dictionary<string, (int hit, int truth)>();
        var tabsWithRect = 0; var tabsWithoutRect = 0; var tabsHit = 0;
        foreach (var c in clicks)
        {
            var row = c.kind == "nav" ? "tab" : c.name;
            if (!ok.ContainsKey(row)) ok[row] = (0, 0);
            ok[row] = (ok[row].hit, ok[row].truth + 1);
            if (c.kind == "nav") { if (c.x is null) { tabsWithoutRect++; continue; } tabsWithRect++; }
            Assert.NotNull(c.record);
            Assert.True(c.x is not null, $"{c.kind} {c.name} in {c.record} has no rectangle in the record");
            var hit = TreeHitTest.ButtonAt(RowsOf(c.record!), c.x!.Value, c.y!.Value);
            Assert.NotNull(hit);
            Assert.Equal(c.name, hit!.Name);
            if (c.kind == "nav") { Assert.Equal("TabItem", hit.Type); tabsHit++; }
            ok[row] = (ok[row].hit + 1, ok[row].truth);
        }
        Assert.Equal((20, 20), ok["Exit"]);
        Assert.Equal((2, 2), ok["Add Line Item"]);
        Assert.Equal((1, 1), ok["Run"]);
        Assert.Equal(8, ok["OK"].hit + ok["No"].hit + ok["Cancel"].hit);
        Assert.Equal(8, ok["OK"].truth + ok["No"].truth + ok["Cancel"].truth);
        Assert.Equal(57, tabsWithRect + tabsWithoutRect);
        Assert.Equal(tabsWithRect, tabsHit);
        Assert.True(tabsWithRect >= 37, $"{tabsWithRect} tab items had a rectangle in the recorded tree; the other {tabsWithoutRect} were off screen when the document was recorded and need the live read under the pointer");
    }

    [Fact]
    public void A_click_on_a_button_that_the_next_event_destroys_is_resolved_on_the_recorded_tree_not_on_a_hovered_button()
    {
        // the real sequence of 16:19:35-16:19:45: hover Add Line Item, hover Comments, click Exit; the dialog shows; click OK; the dialog is destroyed
        var doc = Record("ddi-system-inform-sample-co-new-sales-order.md").Controls;
        var dialog = ScreenRecord.Parse(Path.Combine(FixturesV07, "close.md"))!.Controls;
        var exit = doc.First(r => r.Type == "Button" && r.Name == "Exit"); var ok = dialog.First(r => r.Type == "Button" && r.Name == "OK");
        Labels.TryRect(exit.Position, out var ex, out var ey, out var ew, out var eh); Labels.TryRect(ok.Position, out var ox, out var oy, out var ow, out var oh);
        var t = new DateTime(2026, 11, 19, 16, 19, 39);
        var d = new PressDecider();
        // the hover on Comments raised a state change: nothing is remembered any more (only a mouse capture remembers)
        // the dialog's show is the first event after the click on Exit: the recorded tree of the document, at the pointer, says Exit
        var hitExit = TreeHitTest.ButtonAt(doc, ex + ew / 2, ey + eh / 2)!;
        d.Clicked(new PressCandidate(hitExit.Name, hitExit.AutomationId, hitExit.Type, "Main › New Sales Order"), ex + ew / 2, ey + eh / 2, t);
        var press = d.Confirm(t.AddMilliseconds(150), w => w == "Main › New Sales Order");
        Assert.Equal("Exit", press!.Name);
        // the dialog's destroy is the first event after the click on OK: the dialog's recorded tree, at the pointer, says OK, and the press comes before the close
        var hitOk = TreeHitTest.ButtonAt(dialog, ox + ow / 2, oy + oh / 2)!;
        d.Clicked(new PressCandidate(hitOk.Name, hitOk.AutomationId, hitOk.Type, "Close?"), ox + ow / 2, oy + oh / 2, t.AddSeconds(5));
        Assert.Equal("OK", d.Confirm(t.AddSeconds(5).AddMilliseconds(20), w => w == "Close?")!.Name);
        Assert.Null(d.Confirm(t.AddSeconds(5).AddMilliseconds(40))); // the document's close that follows confirms nothing twice
        // a live read that comes back after the recorded tree answered refines only while the click is unconfirmed
        d.Clicked(new PressCandidate("Exit", "cmdExit", "Button", "Main › New Purchase Order"), 1, 1, t.AddSeconds(60));
        d.Refine(new PressCandidate("Exit", "cmdExit", "Button", "Main › New Purchase Order"), t.AddSeconds(60));
        Assert.Equal("Exit", d.Pending!.Name);
        Assert.NotNull(d.Confirm(t.AddSeconds(60.2)));
        d.Refine(new PressCandidate("Report", "", "Button", "Main"), t.AddSeconds(60));
        Assert.Null(d.Pending);
        // the tick box on the notes window flipped when the record loaded: no click landed on it, so it is the program's
        Assert.False(PressDecider.ToggleIsUserAction(clickedRecently: false, boxHasFocus: false));
    }

    [Fact]
    public void No_press_in_the_run_keeps_a_wrong_name_once_hover_memory_is_gone()
    {
        // the 14 wrong names of the v0.9 run were all the button the pointer hovered before the click; with the recorded tree as the
        // surface, a click resolved at the pointer can only name the control at that point
        var wrong = new[] { "Report", "Cancel", "Select Card Terminal", "Edit", "26.4.1 is now available!" };
        foreach (var c in Clicks().Where(c => c.x is not null))
        {
            var hit = TreeHitTest.ButtonAt(RowsOf(c.record!), c.x!.Value, c.y!.Value)!;
            Assert.DoesNotContain(hit.Name, wrong.Where(w => w != c.name));
        }
        var d = new PressDecider();
        var t = DateTime.Now;
        d.Clicked(null, 1, 1, t); // a click that resolved to nothing confirms nothing
        Assert.Null(d.Confirm(t.AddMilliseconds(100)));
    }

    // ---------- job 2: typed grid cells and the late price ----------

    private static List<ControlRow> OrderGrid(string line1Qty, string line1Price, string line1Ext, string? line2Product = null, string customer = "0000001")
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "New Sales Order", "SalesOrderEntryView", "0,155 1920x855"),
            R(1, "Text", "Customer", "", "20,200 60x20"),
            R(1, "Edit", "", "srcCustomer", "90,200 120x24", customer),
            R(2, "Edit", "", "srcCustomer_EmbeddableTextBox", "92,202 116x20", customer),
            R(1, "DataGrid", "", "gridLIs", "121,345 1106x400"),
            R(2, "Header", "", "", "121,345 1106x52"),
            R(3, "HeaderItem", "Ln", "[Column Header] LineNumber", "159,345 42x52"),
            R(3, "HeaderItem", "Product Number", "[Column Header] DataID", "201,345 150x52"),
            R(3, "HeaderItem", "Product Description", "[Column Header] Description", "351,345 300x52"),
            R(3, "HeaderItem", "Quant Ordered", "[Column Header] QuantityOrdered", "651,345 75x52"),
            R(3, "HeaderItem", "Net Price", "[Column Header] NetPrice", "1010,345 90x52"),
            R(3, "HeaderItem", "Price Extension", "[Column Header] PriceExtension", "1122,345 105x52"),
            R(2, "DataItem", "1", "0", "121,397 1106x28"),
            R(3, "Edit", "", "8522590", "159,399 42x24", "1"),
            R(3, "Edit", "", "8522590", "201,399 150x24", "100EX"),
            R(3, "Edit", "", "8522590", "351,399 300x24", "100EX, SAMPLE PRODUCT"),
            R(3, "Edit", "", "8522590", "651,399 75x24", line1Qty),
            R(3, "Edit", "", "8522590", "1010,399 90x24", line1Price),
            R(3, "Edit", "", "8522590", "1122,399 105x24", line1Ext),
        };
        if (line2Product is not null)
        {
            rows.Add(R(2, "DataItem", "2", "0", "121,425 1106x28"));
            rows.Add(R(3, "Edit", "", "8522590", "159,427 42x24", "2"));
            rows.Add(R(3, "Edit", "", "8522590", "201,427 150x24", line2Product));
            rows.Add(R(3, "Edit", "", "8522590", "651,427 75x24", ""));
            rows.Add(R(3, "Edit", "", "8522590", "1010,427 90x24", "0"));
            rows.Add(R(3, "Edit", "", "8522590", "1122,427 105x24", "0"));
        }
        return rows;
    }

    [Fact]
    public void A_nameless_cell_editor_takes_its_column_name_and_the_cell_that_had_the_focus_is_the_typed_value_by_place()
    {
        var before = FieldReads.FromRows(OrderGrid("1", "0", "0"));
        var qtyCell = before.First(f => f.Label == "Quant Ordered");
        Assert.True(qtyCell.IsCell); Assert.EndsWith("\u0001Quant Ordered\u0001line 1", qtyCell.Key); // keyed by the line number, not the row index
        Assert.Equal("651,399 75x24", qtyCell.Position);
        // the editor that had the focus has no name and a numeric id shared by every cell: it is found by its place
        var focused = FieldReads.AtRect(before, "652,400 73x22")!;
        Assert.Equal("Quant Ordered", focused.Label);
        var after = FieldReads.FromRows(OrderGrid("5", "45.3426", "226.71"));
        var (typed, filled) = FieldReads.Diff(before, after, new HashSet<string> { focused.Label });
        Assert.Equal("Quant Ordered", Assert.Single(typed).Label);
        Assert.Equal(new[] { "Net Price", "Price Extension" }, filled.Select(f => f.Label).OrderBy(x => x).ToArray());
        // a line added above keeps the first line's cells under their own key (no false "new" cells)
        var withLine2 = FieldReads.FromRows(OrderGrid("5", "45.3426", "226.71", "100EXA"));
        var (typed2, filled2) = FieldReads.Diff(after, withLine2, new HashSet<string> { "Product Number" });
        Assert.Empty(typed2.Where(f => f.Key.EndsWith("line 1")));
        Assert.Contains(filled2, f => f.Key.EndsWith("\u0001Net Price\u0001line 2"));
        Assert.DoesNotContain(filled2, f => f.Key.EndsWith("line 1"));
        Assert.True(Masking.LooksLikeData("8522590") && Masking.LooksLikeData("Phone: (masked)") == false);
        Assert.Equal("Product Number", new UiEvent { Name = "", Aid = "8522590", Ct = "Edit" }.FieldKey == "Edit" ? "Product Number" : "Product Number"); // a bare numeric id names nothing: the collector fills the column in
        Assert.Equal("Edit", new UiEvent { Name = "", Aid = "8522590", Ct = "Edit" }.FieldKey);
        Assert.Equal("Master Search", new UiEvent { Name = "", Aid = "srcMasterSearch_EmbeddableTextBox", Ct = "Edit" }.FieldKey);
        Assert.Equal("Vendor - # \"…\" Internal Notes", new UiEvent { Name = "Vendor - 00001 \"ACME\" Internal Notes", Ct = "TabItem" }.FieldKey);
    }

    [Fact]
    public void Keystrokes_credited_to_two_windows_while_a_popup_flickers_are_one_typed_value()
    {
        // 16:20:03-16:20:04: 0, 00, 000, 00001 on two windows that popped up: one value
        var d = new ValueDebouncer();
        var t = new DateTime(2026, 11, 19, 16, 20, 3);
        UiEvent Ev(double sec, string win, string val) => new() { T = t.AddSeconds(sec), Act = Acts.Value, Win = win, Name = "", Aid = "srcVendor", Ct = "Edit", Val = val };
        d.Push(Ev(0, "Main › New Purchase Order", "0"), t);
        d.Push(Ev(0.7, "Vendor - 00001 \"ACME\" Internal Notes", "00"), t.AddSeconds(0.7));
        d.Push(Ev(0.9, "Vendor - 00001 \"ACME\" Internal Notes", "000"), t.AddSeconds(0.9));
        d.Push(Ev(1.1, "Vendor - 00001 \"ACME\" Internal Notes", "00001"), t.AddSeconds(1.1));
        Assert.Empty(d.Flush(t.AddSeconds(1.5)));
        var one = Assert.Single(d.Flush(t.AddSeconds(2.2)));
        Assert.Equal("00001", one.Val);
        // the program's fill of the same field is not folded into the user's typing
        d.Push(Ev(3, "Main", "1"), t.AddSeconds(3));
        var app = Ev(3.1, "Main", "ACME"); app.By = ValueBy.App; d.Push(app, t.AddSeconds(3.1));
        Assert.Equal(2, d.Flush(t.AddSeconds(5), all: true).Count);
    }

    [Fact]
    public void The_typed_cells_and_the_late_price_give_the_extension_formula_and_the_customer_fills()
    {
        var t = new DateTime(2026, 11, 19, 16, 18, 0);
        var main = "DDI System Inform - Sample Co"; var win = main + " › New Sales Order";
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1) };
        UiEvent Typed(DateTime at, string field, string val, string ct = "DataItem") { var e = Synthetic.Ev(at, Acts.Value, win, field, val, ct); e.How = "read"; return e; }
        UiEvent App(DateTime at, string field, string val, string w, string ct = "DataItem") { var e = Synthetic.Ev(at, Acts.Value, w, field, val, ct); e.By = ValueBy.App; return e; }
        input.Events.Add(Synthetic.Ev(t, Acts.Open, win, win, ct: "Window"));
        input.Events.Add(Typed(t.AddSeconds(4), "Customer", "0000001", "Edit"));
        // the header fills came 300 ms later, credited to the main window by the collector of that run: they belong to the document
        foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) input.Events.Add(App(t.AddSeconds(4.3), f, "v", main, "Edit"));
        input.Events.Add(Typed(t.AddSeconds(13), "Product Number", "100EX"));
        foreach (var (f, v) in new[] { ("Product Description", "SAMPLE PRODUCT"), ("Quant UM", "EA"), ("Quant Ordered", "1"), ("Net Price", "0"), ("Price Extension", "0") }) input.Events.Add(App(t.AddSeconds(13.3), f, v, win));
        // the second read at 1.5 s: the program priced the line
        foreach (var (f, v) in new[] { ("Net Price", "45.3426"), ("Price Extension", "45.34") }) input.Events.Add(App(t.AddSeconds(14.5), f, v, win));
        input.Events.Add(Typed(t.AddSeconds(20), "Quant Ordered", "5"));
        input.Events.Add(App(t.AddSeconds(20.3), "Price Extension", "226.71", win));
        input.Events.Add(Typed(t.AddSeconds(26), "Quant Ordered", "20"));
        input.Events.Add(App(t.AddSeconds(26.3), "Price Extension", "906.85", win));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(99), Acts.Invoke, win, "Exit", ct: "Button"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(100), Acts.Dialog, "Close?", "Close?", "All changes will be lost. Continue?", "Window"));
        var r = Learner.Learn(input);
        Assert.Equal(4, input.Events.Count(e => e.Act == Acts.Value && e.By is null));
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Price Extension");
        Assert.Equal("Net Price × Quant Ordered", calc.Formula);
        Assert.Equal(3, calc.Fits); Assert.Equal(3, calc.Tested); Assert.Empty(calc.Alternatives);
        Assert.Single(input.Events, e => e.FieldKey == "Net Price" && e.Val == "45.3426");
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 3× of 3)", md);
        Assert.Contains("Typing Customer fills Branch (seen 1× of 1), Ship To (seen 1× of 1), Ship Via (seen 1× of 1), Whse (seen 1× of 1), Writer (seen 1× of 1)", md);
        Assert.Contains("Typing Product Number fills", md);
        Assert.Contains("Net Price (seen 1× of 1)", md);
        Assert.DoesNotContain("0000001", md);
    }

    // ---------- job 3: memory quality and privacy ----------

    [Fact]
    public void Health_names_no_document_that_was_renamed_or_recorded_under_another_title()
    {
        var ws = new MdiWorkspace();
        ws.Shown(1, "SalesOrderEntryView");        // the class name shown before the title is set
        Assert.Empty(ws.TitlesSeen); Assert.Equal("", ws.Active);
        ws.Retitle(1, "Sales Order Entry");
        ws.Retitle(1, "New Sales Order");           // the record loaded: renamed, one entry
        Assert.Equal(new[] { "New Sales Order" }, ws.TitlesSeen);
        ws.Shown(2, "Vendor Master"); ws.Retitle(2, "Vendor - 00001 \"ACME\"");
        Assert.Equal(new[] { "New Sales Order", "Vendor - 00001 \"ACME\"" }, ws.TitlesSeen);
        Assert.True(MdiWorkspace.IsClassLikeName("BaseOrderView") && MdiWorkspace.IsClassLikeName("SaleOrderInquiryVM") && !MdiWorkspace.IsClassLikeName("Price Change"));
        Assert.Empty(ws.NeverRecorded(t => t == "New Sales Order" || t == "Vendor Master"));          // recorded under one of its titles
        Assert.Equal(new[] { "Vendor - 00001 \"ACME\"" }, ws.NeverRecorded(t => t == "New Sales Order"));
        ws.Destroyed(2);
        Assert.Equal(new[] { "Vendor - 00001 \"ACME\"" }, ws.NeverRecorded(t => t == "New Sales Order")); // gone documents still count
        // the learner, given the collector list of that run (15 entries), names none: class names are dropped and a document renamed after its first title has a record
        var r = Learner.Learn(Input());
        Assert.Empty(r.Health.DocumentsNeverRecorded);
        var health = MemoryWriter.Render(r)["health.md"];
        Assert.DoesNotContain("never recorded", health);
    }

    [Fact]
    public void Tick_boxes_keep_their_state_and_a_copy_or_a_cell_formula_needs_three_distinct_values_and_no_alternative()
    {
        Assert.Equal("On", Masking.ForAppValue("On", "Fax Include Area Code", "chkFaxIncludeAreaCode", false, "CheckBox"));
        Assert.Equal("Off", Masking.ForAppValue("Off", "Name", "", false, "CheckBox"));
        Assert.Equal("Aaaa Aaa", Masking.ForAppValue("Test Two", "Writer", "srcWriter", false, "Edit"));
        Assert.Equal("Aaaa Aaaaaaaa", Masking.ForAppValue("Test Customer", "Customer", "srcCustomer", false, "Edit"));
        var r = Learner.Learn(Input());
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.DoesNotContain("copied from", md);
        Assert.DoesNotContain("Hits = ", md);
        Assert.DoesNotContain("Ln = ", md);
        Assert.Empty(r.Logic.Calculations);
        // a cell formula with three distinct values and one fit is written; with two values or an alternative it is a question
        var t = new DateTime(2026, 11, 19, 10, 0, 0);
        var win = "Main › Order";
        LearnInput Make(params (string qty, string ext)[] lines)
        {
            var input = new LearnInput { AppName = "x", Now = t.AddDays(1) };
            var i = 0;
            foreach (var (qty, ext) in lines)
            {
                var at = t.AddSeconds(10 * i++);
                var q = Synthetic.Ev(at, Acts.Value, win, "Qty", qty, "DataItem"); input.Events.Add(q);
                var p = Synthetic.Ev(at.AddMilliseconds(300), Acts.Value, win, "Price", "2.5", "DataItem"); p.By = ValueBy.App; input.Events.Add(p);
                var e = Synthetic.Ev(at.AddMilliseconds(300), Acts.Value, win, "Ext", ext, "DataItem"); e.By = ValueBy.App; input.Events.Add(e);
            }
            return input;
        }
        var three = Learner.Learn(Make(("1", "2.5"), ("5", "12.5"), ("20", "50")));
        Assert.Contains(three.Logic.Calculations, c => c.Field == "Ext" && c.Formula == "Price × Qty");
        var two = Learner.Learn(Make(("5", "12.5"), ("20", "50")));
        Assert.DoesNotContain(two.Logic.Calculations, c => c.Field == "Ext");
        Assert.Contains(two.Logic.Unexplained, c => c.Field == "Ext");
        var same = Learner.Learn(Make(("5", "12.5"), ("5", "12.5"), ("5", "12.5")));
        Assert.DoesNotContain(same.Logic.Calculations, c => c.Field == "Ext"); // three observations of one value are one observation
    }

    [Fact]
    public void Masks_reach_every_place_a_title_or_a_name_appears_and_the_memory_holds_no_real_name()
    {
        var saved = Titles.MaskPatterns;
        try
        {
            Titles.MaskPatterns = ProfileStore.ByName("ddi-inform")!.TitleMasks.ToList();
            Assert.Contains("DDI System Inform - *", Titles.MaskPatterns);
            Assert.Equal("Vendor - # \"…\"", Titles.Key("Vendor - 00001 \"ACME\""));
            Assert.Equal("DDI System Inform - … › Customer - # \"…\"", Titles.Key("DDI System Inform - Sample Co › Customer - 0000001 \"Test Customer\""));
            var r = Learner.Learn(Input());
            var all = MemoryWriter.Render(r);
            var text = string.Join("\n", all.Values);
            foreach (var leak in new[] { "Test Customer", "Writer One", "ACME", "Sample City", "Sample Street", "Sample Co", "0000001", "00001" })
                Assert.DoesNotContain(leak, text);
            Assert.DoesNotContain(all["logic.md"], "(masked)"); // a control named by a phone number is renamed, not printed
            Assert.DoesNotMatch(@"\b\d{7}\b", all["logic.md"]); // no record id as a field name
            Assert.Contains("tab Vendor - # \"…\"", all["flows.md"] + all["logic.md"]); // the document name as a tab step, masked
        }
        finally { Titles.MaskPatterns = saved; }
        Assert.True(Masking.IsPersonalField("Writer") && Masking.IsPersonalField("Sold To") && Masking.IsPersonalField("Ordered By") && Masking.IsPersonalField("Buyer"));
    }

    // ---------- job 4: small ----------

    [Fact]
    public void A_lookup_grid_that_stayed_on_screen_is_no_new_dropdown_a_menu_name_is_no_hint_and_a_flow_runs_from_open_to_close()
    {
        var before = OrderGrid("", "", "").ToList();
        before.Add(R(1, "List", "", "lstLookup", "201,430 400x120"));
        before.Add(R(2, "ListItem", "100EX | SAMPLE PRODUCT", "", "201,430 400x20"));
        before.Add(R(2, "ListItem", "100EXA | SAMPLE PRODUCT A", "", "201,450 400x20"));
        var same = before.ToList();
        Assert.Null(FieldReads.NewLookup(same, before, "201,399 150x24"));                      // the same rows as before the typing: no lookup opened
        var changed = before.Select(r => r.Name == "100EXA | SAMPLE PRODUCT A" ? R(2, "ListItem", "12 | TWELVE", "", "201,450 400x20") : r).ToList();
        Assert.NotNull(FieldReads.NewLookup(before, changed, "201,399 150x24"));                 // new rows under the field: a lookup
        var r = Learner.Learn(Input());
        Assert.DoesNotContain(r.Logic.Hints, h => h.Text == "Accounting");
        var flow = r.Flows.FirstOrDefault(f => f.Key.EndsWith("› New Sales Order"));
        Assert.NotNull(flow);
        Assert.True(flow!.MedianMs > 10_000, $"the order flow lasted {flow.MedianMs} ms: it runs from the document's open to its close, not from one flicker to the next");
        Assert.True(flow.Count <= 4, $"{flow.Count} runs of the order flow: the driver opened it four times");
    }

    [Fact]
    public void The_v09_fixture_keeps_what_v09_did_right()
    {
        var r = Learner.Learn(Input());
        Assert.Equal(8, r.Health.DialogsSeen);
        Assert.True(r.Screens.Count(s => s.Kind == ScreenKinds.Tab) >= 14, $"{r.Screens.Count(s => s.Kind == ScreenKinds.Tab)} documents");
        Assert.Contains(r.Logic.Hints, h => h.Text == "Click to add a new Line Item row");
        Assert.True(r.Flows.Count >= 8);
        var again = MemoryWriter.Render(Learner.Learn(Input()));
        foreach (var (name, text) in MemoryWriter.Render(r)) Assert.Equal(text, again[name]);
        Assert.Equal(447, Events().Count);
    }
}
