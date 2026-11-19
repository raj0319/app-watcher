using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AppWatcher.Core;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Events;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Setup;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The fixes after the measured 0.8 run against the real tabbed-MDI ERP (docs/test-v0.8-inform.md), checked against the
/// anonymised copy of that run (tests/Fixtures/inform-v0.8) and synthetic evidence: documents are MDI children, inner tabs
/// are selections, clicks come from the key state and the control under the pointer, typed grid cells come from re-reads,
/// OCR formulas only between real fields, privacy of what the program fills in, flows without program-shown windows,
/// the primary backend, honest health lines, lookups and hints.
/// </summary>
public class InformV09Tests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform-v0.8");
    private static string FixturesV07 => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform");
    private static ScreenRecord Record(string file) => ScreenRecord.Parse(Path.Combine(Fixtures, file))!;
    private static List<ScreenRecord> AllRecords() => Directory.GetFiles(Fixtures, "*.md").Where(f => !f.EndsWith("README.md")).Select(f => ScreenRecord.Parse(f)!).ToList();
    private static List<UiEvent> Events() => EventReader.ReadFile(Path.Combine(Fixtures, "events-2026-11-19.jsonl")).ToList();
    private static CollectorStatus Status() => System.Text.Json.JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(Path.Combine(Fixtures, "collector-status.json")))!;
    private static ControlRow R(int depth, string type, string name = "", string aid = "", string pos = "10,10 100x20", string? value = null) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos, Value = value };

    private static LearnInput Input()
    {
        var input = new LearnInput { AppName = "inform", Events = Events(), Screens = AllRecords(), Collector = Status(), HasDataSources = false, Now = new DateTime(2026, 11, 20), BackendKind = "universe" };
        input.SessionWindowPatterns.AddRange(new[] { "Splash Screen", "Login*", "DDI System Inform Login", "Enter Register #*" });
        input.Backends.Add(new BackendInfo { Kind = "rest", Host = "shop.example", Where = "App Log.json" });
        input.Backends.Add(new BackendInfo { Kind = "rest", Host = "support.example", Where = "App Log.json" });
        input.Backends.Add(new BackendInfo { Kind = "universe", Host = "192.0.2.10", Database = "DDI", Where = "installedapplication.config; tcp connection to that host (port 60010)" });
        return input;
    }

    // ---------- 1. documents are MDI children ----------

    /// <summary>The main window with the MDICLIENT pane and two documents, the second in front, as the real program shows it (probe of 2026-11-19).</summary>
    private static List<ControlRow> MainWithMdiChildren()
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "DDI System Inform - Sample Co", "MainView", "-8,-8 1936x1048"),
            R(1, "ToolBar", "Fast Click Menu", "Toolbar : F", "0,60 1920x60"),
            R(2, "Button", "Customer Master", "[Toolbar : F Tools] Tool : M/S/P/h - Index : 2", "74,86 32x33"),
            R(2, "Button", "Sales Order", "[Toolbar : F Tools] Tool : M/S/P/h - Index : 7", "274,86 32x33"),
            R(1, "Pane", "", "197300", "0,155 1920x855"),
            R(2, "Window", "Price Change", "1", "0,155 1920x855"),
            R(3, "Pane", "", "pnlWork", "0,155 1920x855"),
            R(4, "Tab", "", "2819600", "0,200 800x300"),
            R(5, "TabItem", "Setup", "", "0,200 60x20", "selected"),
            R(5, "TabItem", "Ledger", "", "60,200 60x20"),
            R(4, "Button", "Select Products", "cmdSelect", "20,520 120x30"),
            R(2, "Window", "Sales Order Entry", "1", "0,155 1920x855"),
            R(3, "Pane", "", "pnlWork", "0,155 1920x855"),
            R(4, "Tab", "", "1574634", "0,200 800x300"),
            R(5, "TabItem", "Main", "", "0,200 60x20", "selected"),
            R(5, "TabItem", "General", "", "60,200 60x20"),
            R(5, "TabItem", "Credit", "", "120,200 60x20"),
            R(4, "Tab", "", "11863606", "0,520 800x300"),
            R(5, "TabItem", "Header", "", "0,520 60x20", "selected"),
            R(5, "TabItem", "Detail", "", "60,520 60x20"),
            R(4, "Edit", "", "srcCustomer", "90,240 120x24"),
            R(4, "Button", "Add Line Item", "cmdAddLine", "20,300 120x30"),
            R(4, "Button", "Exit", "cmdExit", "160,300 120x30"),
            R(1, "StatusBar", "", "statusBar", "0,1010 1920x30"),
        };
        return rows;
    }

    [Fact]
    public void The_document_in_front_is_the_screen_and_a_destroyed_document_hands_over_to_the_next()
    {
        Assert.True(MdiWorkspace.IsMdiClientClass("WindowsForms10.MDICLIENT.app.0.e76fc7_r6_ad1"));
        Assert.False(MdiWorkspace.IsMdiClientClass("WindowsForms10.Window.8.app.0.e76fc7_r6_ad1"));
        var ws = new MdiWorkspace();
        Assert.Null(ws.Active);
        Assert.Equal("Price Change", ws.Shown(1001, "Price Change"));
        Assert.Equal("Sales Order Entry", ws.Shown(1002, "Sales Order Entry")); // the second one opened is in front
        Assert.Equal("DDI System Inform - Sample Co › Sales Order Entry", TabDocuments.Title("DDI System Inform - Sample Co", ws.Active!));
        Assert.Equal("Price Change", ws.Destroyed(1002));
        Assert.Null(ws.Destroyed(1001));
        Assert.Equal(new[] { "Price Change", "Sales Order Entry" }, ws.TitlesSeen);
        // the z-order read from the windows wins over the order of events (a click brought Price Change to the front)
        ws.Shown(1001, "Price Change"); ws.Shown(1002, "Sales Order Entry");
        Assert.Equal("Price Change", ws.Reorder(new[] { (1001L, "Price Change"), (1002L, "Sales Order Entry") }));
        Assert.Equal("Sales Order Entry", ws.Activated(1002));
        ws.Retitle(1002, "Sales Order Entry - S00001");
        Assert.Equal("Sales Order Entry - S00001", ws.Active);
        Assert.Contains("Sales Order Entry - S00001", ws.TitlesSeen);
        Assert.Equal(2, ws.Count);
    }

    [Fact]
    public void A_tab_control_inside_a_document_is_never_the_workspace_strip()
    {
        var rows = MainWithMdiChildren();
        var tabs = Enumerable.Range(0, rows.Count).Where(i => rows[i].Type == "Tab").ToList();
        Assert.Equal(3, tabs.Count);
        Assert.All(tabs, i => Assert.True(TabDocuments.IsInnerTab(rows, i), $"the Tab with id {rows[i].AutomationId} lies inside a document"));
        Assert.Empty(TabDocuments.TabNames(rows)); // no document strip is exposed: the documents are the MDI children
        var doc = TabDocuments.Document(rows, "Sales Order Entry");
        Assert.Equal("Sales Order Entry", doc[0].Name);
        Assert.Contains(doc, r => r.Name == "Add Line Item");
        Assert.DoesNotContain(doc, r => r.Name is "Customer Master" or "Select Products");
        // a strip at the window level is still the strip (tabbed documents that are exposed)
        var strip = new List<ControlRow> { R(0, "Window", "Main"), R(1, "Tab", "", "854088"), R(2, "TabItem", "Orders"), R(1, "Pane", "", "197300"), R(2, "Window", "Orders", "1"), R(3, "Tab", "", "inner"), R(4, "TabItem", "Main") };
        Assert.False(TabDocuments.IsInnerTab(strip, 1));
        Assert.True(TabDocuments.IsInnerTab(strip, 5));
        Assert.Equal(new[] { "Orders" }, TabDocuments.TabNames(strip));
    }

    // ---------- 2. inner tabs are selections ----------

    [Fact]
    public void Fifty_sub_tab_clicks_give_fifty_selections_and_an_unchanged_page_gives_none()
    {
        var rows = MainWithMdiChildren();
        var doc = TabDocuments.Document(rows, "Sales Order Entry");
        var first = TabDocuments.InnerTabSelections(doc);
        Assert.Equal(2, first.Count);
        Assert.Equal("Main", first["1574634"]); Assert.Equal("Header", first["11863606"]);
        Assert.Empty(TabDocuments.InnerTabChanges(first, first));
        var pages = new[] { "Main", "General", "Credit", "Notes", "Freight" };
        var selects = 0; var prev = first;
        for (var click = 0; click < 50; click++)
        {
            var page = pages[(click + 1) % pages.Length];
            var now = new Dictionary<string, string>(prev) { ["1574634"] = page };
            var changes = TabDocuments.InnerTabChanges(prev, now);
            selects += changes.Count;
            Assert.Equal(new[] { page }, changes);
            prev = now;
        }
        Assert.Equal(50, selects);
        Assert.Equal(new[] { "Main", "Header" }, TabDocuments.InnerTabChanges(null, first)); // the first read of a document reports its open pages
    }

    // ---------- 3. clicks without a hook ----------

    [Fact]
    public void Hover_A_then_click_B_is_a_press_on_B_and_exit_then_close_prompt_is_a_press_on_exit()
    {
        var t = new DateTime(2026, 11, 19, 13, 9, 29);
        var d = new PressDecider();
        // a hover raises a state change without PRESSED: nothing is remembered (the collector only calls RememberPressed for PRESSED)
        d.Clicked(new PressCandidate("Exit", "", "Button", "Main"), 300, 100, t); // the left button went down: Exit is under the pointer now
        var c = d.Confirm(t.AddMilliseconds(400));
        Assert.NotNull(c); Assert.Equal("Exit", c!.Name);
        Assert.Null(d.Confirm(t.AddMilliseconds(500))); // the dialog that follows confirms nothing twice
        // a pressed state remembered 30 px away from where the click is read: the pointer left the button it pressed, the remembered one wins
        d.RememberPressed(new PressCandidate("Report", "", "Button", "Main"), 100, 100, t.AddSeconds(5));
        d.Clicked(new PressCandidate("Labels", "", "Button", "Main"), 140, 100, t.AddSeconds(5.2));
        Assert.Equal("Report", d.Confirm(t.AddSeconds(5.5))!.Name);
        // the same remembered control with the pointer still on it: the fresh read wins
        d.RememberPressed(new PressCandidate("Report", "", "Button", "Main"), 100, 100, t.AddSeconds(10));
        d.Clicked(new PressCandidate("Labels", "", "Button", "Main"), 105, 102, t.AddSeconds(10.2));
        Assert.Equal("Labels", d.Confirm(t.AddSeconds(10.5))!.Name);
        // a click older than 1.5 s confirms nothing; a click on a non-button (a grid cell) is no press
        d.Clicked(new PressCandidate("Exit", "", "Button", "Main"), 1, 1, t.AddSeconds(20));
        Assert.Null(d.Confirm(t.AddSeconds(22)));
        d.Clicked(new PressCandidate("Quant Ordered", "", "DataItem", "Main"), 1, 1, t.AddSeconds(30));
        Assert.Null(d.Confirm(t.AddSeconds(30.2)));
        // the window check: a click in the prompt does not count for the document behind it
        d.Clicked(new PressCandidate("OK", "", "Button", "Close?"), 1, 1, t.AddSeconds(40));
        Assert.Null(d.Confirm(t.AddSeconds(40.2), win => win == "Main"));
        Assert.NotNull(d.Confirm(t.AddSeconds(40.3), win => win == "Close?"));
    }

    [Fact]
    public void A_tick_box_flipped_by_a_re_read_is_not_a_toggle_and_a_repeated_name_with_nothing_between_is_one_press()
    {
        Assert.False(PressDecider.ToggleIsUserAction(clickedRecently: false, boxHasFocus: false)); // "Additional Information" flipped when the record loaded: by: app
        Assert.True(PressDecider.ToggleIsUserAction(clickedRecently: true, boxHasFocus: false));
        Assert.True(PressDecider.ToggleIsUserAction(clickedRecently: false, boxHasFocus: true));
        var t = new DateTime(2026, 11, 19, 13, 0, 0);
        var d = new PressDecider();
        d.Pressed("Exit", t);
        Assert.True(d.IsRepeat("Exit", t.AddSeconds(4)));   // nothing recorded in between: the same click seen late
        d.EventEmitted();
        Assert.False(d.IsRepeat("Exit", t.AddSeconds(4)));  // a window closed in between: a second Exit is a second press
        d.Pressed("Exit", t.AddSeconds(4));
        Assert.True(d.IsRepeat("Exit", t.AddSeconds(5)));   // within 1.5 s it is always the same click
        Assert.False(d.IsRepeat("Exit", t.AddSeconds(20)));
        Assert.False(d.IsRepeat("Save", t.AddSeconds(4.1)));
        Assert.True(PressDecider.IsActionable("Button")); Assert.False(PressDecider.IsActionable("TabItem")); Assert.False(PressDecider.IsActionable("DataItem"));
    }

    // ---------- 4. re-read on cell moves and keys ----------

    private static List<ControlRow> OrderLine(string qty, string price, string ext, string customer = "0000001")
    {
        return new List<ControlRow>
        {
            R(0, "Window", "Sales Order Entry", "1", "0,155 1920x855"),
            R(1, "Text", "Customer", "", "20,200 60x20"),
            R(1, "Edit", "", "srcCustomer", "90,200 120x24", customer),
            R(1, "Edit", "", "srcCustomer_EmbeddableTextBox", "92,202 116x20", customer),
            R(1, "DataGrid", "", "gridLines", "20,220 900x300"),
            R(2, "Header", "", "", "20,220 900x20"),
            R(3, "HeaderItem", "Product", "", "20,220 150x20"),
            R(3, "HeaderItem", "Quant Ordered", "", "170,220 100x20"),
            R(3, "HeaderItem", "Net Price", "", "270,220 100x20"),
            R(3, "HeaderItem", "Price Extension", "", "370,220 120x20"),
            R(2, "DataItem", "row 1", "", "20,240 900x24"),
            R(3, "Edit", "Product", "_EmbeddableTextBox", "20,244 150x20", "100EX"),
            R(3, "Edit", "Quant Ordered", "_EmbeddableTextBox", "170,244 100x20", qty),
            R(3, "Edit", "Net Price", "_EmbeddableTextBox", "270,244 100x20", price),
            R(3, "Edit", "Price Extension", "_EmbeddableTextBox", "370,244 120x20", ext),
        };
    }

    [Fact]
    public void The_cell_that_had_the_focus_is_the_typed_value_and_the_other_changed_cells_are_filled_by_the_program()
    {
        var before = FieldReads.FromRows(OrderLine("1", "45.3426", "45.34"));
        var after = FieldReads.FromRows(OrderLine("5", "45.3426", "226.71"));
        var (typed, filled) = FieldReads.Diff(before, after, new HashSet<string> { "Quant Ordered" });
        var t = Assert.Single(typed); Assert.Equal("Quant Ordered", t.Label); Assert.Equal("5", t.Value); Assert.True(t.IsCell);
        var f = Assert.Single(filled); Assert.Equal("Price Extension", f.Label); Assert.Equal("226.71", f.Value);
        // the focus moved on to Net Price before the read: the cell the user just left is still the typed one
        var (typed2, filled2) = FieldReads.Diff(after, FieldReads.FromRows(OrderLine("20", "45.3426", "906.85")), new HashSet<string> { "Net Price", "Quant Ordered" });
        Assert.Equal("Quant Ordered", Assert.Single(typed2).Label);
        Assert.Equal("Price Extension", Assert.Single(filled2).Label);
        // a wrapper id names nothing; an empty first read is only the baseline
        Assert.Empty(FieldReads.Diff(new List<FieldRead>(), after, new HashSet<string> { "Quant Ordered" }).filled);
        var (typed3, filled3) = FieldReads.Diff(before, after, new HashSet<string> { "_EmbeddableTextBox" });
        Assert.Empty(typed3); Assert.Equal(2, filled3.Count);
    }

    [Fact]
    public void Re_reads_of_qty_1_5_20_give_the_extension_formula_three_of_three_and_the_header_links_two_of_two()
    {
        var t = new DateTime(2026, 11, 19, 13, 11, 45);
        var win = "DDI System Inform - Sample Co › Sales Order Entry";
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1) };
        UiEvent Typed(DateTime at, string field, string val, string ct = "DataItem") { var e = Synthetic.Ev(at, Acts.Value, win, field, val, ct); e.How = "read"; return e; }
        UiEvent App(DateTime at, string field, string val, string ct = "DataItem") { var e = Synthetic.Ev(at, Acts.Value, win, field, val, ct); e.By = ValueBy.App; return e; }
        for (var order = 0; order < 2; order++)
        {
            var at = t.AddMinutes(order * 3);
            input.Events.Add(Synthetic.Ev(at, Acts.Open, win, win, ct: "Window"));
            input.Events.Add(Typed(at.AddSeconds(5), "Customer", "0000001", "Edit"));
            foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) input.Events.Add(App(at.AddSeconds(5.4), f, "v" + order, "Edit"));
            input.Events.Add(Typed(at.AddSeconds(12), "Product", "100EX"));
            input.Events.Add(App(at.AddSeconds(12.4), "Product Description", "SAMPLE PRODUCT"));
            input.Events.Add(App(at.AddSeconds(12.4), "Net Price", "45.3426"));
            input.Events.Add(App(at.AddSeconds(12.4), "Quant Ordered", "1"));
            input.Events.Add(App(at.AddSeconds(12.4), "Price Extension", "45.34"));
            input.Events.Add(Typed(at.AddSeconds(20), "Quant Ordered", "5"));
            input.Events.Add(App(at.AddSeconds(20.4), "Price Extension", "226.71"));
            if (order == 0)
            {
                input.Events.Add(Typed(at.AddSeconds(26), "Quant Ordered", "20"));
                input.Events.Add(App(at.AddSeconds(26.4), "Price Extension", "906.85"));
            }
            input.Events.Add(Synthetic.Ev(at.AddSeconds(40), Acts.Invoke, win, "Exit", ct: "Button"));
            input.Events.Add(Synthetic.Ev(at.AddSeconds(41), Acts.Dialog, "Close?", "Close?", "All changes will be lost. Continue?", "Window"));
        }
        var r = Learner.Learn(input);
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Price Extension");
        Assert.Equal("Net Price × Quant Ordered", calc.Formula);
        Assert.True(calc.Fits >= 3 && calc.Fits == calc.Tested, $"{calc.Fits} of {calc.Tested}");
        var links = r.Logic.Links.Where(l => l.When == "typed:Customer").ToList();
        Assert.Equal(new[] { "Branch", "Ship To", "Ship Via", "Whse", "Writer" }, links.Select(l => l.Field).OrderBy(x => x).ToArray());
        Assert.All(links, l => { Assert.Equal(2, l.Seen); Assert.Equal(2, l.CauseCount); });
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.Contains("Typing Customer fills Branch (seen 2× of 2)", md);
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen", md);
        Assert.DoesNotContain("0000001", md);
        // typed values found by re-reading count as what people type, not as steps the program took
        var flow = Assert.Single(r.Flows);
        Assert.Contains("type Quant Ordered", flow.StepControls[win].Select(c => c.name));
        Assert.DoesNotContain("type Price Extension", flow.StepControls[win].Select(c => c.name));
    }

    // ---------- 5. OCR formulas only between real fields ----------

    private static OcrLine L(string text, int x, int y, int w = 80, int h = 14) => new() { Text = text, X = x, Y = y, W = w, H = h };

    [Fact]
    public void The_three_wrong_findings_of_the_v08_run_produce_nothing()
    {
        var main = "DDI System Inform - Sample Co";
        var t = new DateTime(2026, 11, 19, 13, 12, 0);
        var known = OcrLogic.KnownFields(AllRecords());
        Assert.True(known.ContainsKey(main));
        Assert.True(known[main].Count >= 3);
        Assert.DoesNotContain("Sample City, NY", known[main]);
        Assert.DoesNotContain("Order Balance", known[main]);
        // (1) a city line and a balance on three pictures of the main window: "<city>, NY = Order Balance × -365" must not come back
        var s = new LearnState();
        var states = new List<OcrState>();
        var pairs = new[] { (-1234.56m, 3.38m), (-2104.00m, 5.76m), (-3650.67m, 10.00m), (-99.00m, 1.00m) };
        for (var i = 0; i < pairs.Length; i++)
        {
            states.Add(new OcrState
            {
                Screen = main, File = $"screens/shots/ddi-system-inform-sample-co/{10 + i}-after-type.png.ocr.json", T = t.AddSeconds(i * 20 + 1),
                Lines = new List<OcrLine> { L("Sample City, NY", 100, 300, 120), L(pairs[i].Item1.ToString("0.00"), 230, 300), L("Order Balance", 100, 340, 110), L(pairs[i].Item2.ToString("0.00"), 230, 340), L("Master Search", 100, 100), L("12", 230, 100) },
            });
        }
        var session = Enumerable.Range(0, pairs.Length).Select(i => Synthetic.Ev(t.AddSeconds(i * 20), Acts.Value, main, "Master Search", "12")).ToList();
        OcrLogic.Accumulate(s, session, states, known);
        Assert.Empty(s.CalcFits.Keys.Where(k => k.Contains("Sample City")));
        Assert.Empty(s.CalcTests.Keys.Where(k => k.Contains("Sample City")));
        Assert.False(OcrLogic.LooksLikeFieldName("Sample City, NY"));
        Assert.False(OcrLogic.LooksLikeFieldName("1 Sample Street"));
        Assert.True(OcrLogic.LooksLikeFieldName("Order Balance"));
        // (2) and (3): tick boxes flipped together are not "copied from" each other, through the tree path
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1) };
        UiEvent App(DateTime at, string field, string val, string ct) { var e = Synthetic.Ev(at, Acts.Value, main, field, val, ct); e.By = ValueBy.App; return e; }
        for (var i = 0; i < 2; i++)
        {
            var at = t.AddMinutes(i);
            input.Events.Add(Synthetic.Ev(at, Acts.Value, main, "Customer", "0000001"));
            input.Events.Add(App(at.AddMilliseconds(500), "Include Area Code", "On", "CheckBox"));
            input.Events.Add(App(at.AddMilliseconds(500), "Fax", "On", "CheckBox"));
            input.Events.Add(App(at.AddMilliseconds(500), "Auto Prompt SDS", "Off", "CheckBox"));
            input.Events.Add(App(at.AddMilliseconds(500), "Ship Complete", "Off", "CheckBox"));
            input.Events.Add(App(at.AddMilliseconds(500), "Customer Part Num Required", "Off", "CheckBox"));
        }
        var r = Learner.Learn(input);
        Assert.Empty(r.Logic.Calculations);
        Assert.Empty(r.Logic.Unexplained);
        Assert.Contains(r.Logic.Links, l => l.Field == "Fax" && l.When == "typed:Customer"); // the tick box is still filled after typing the customer
        Assert.DoesNotContain("copied from", MemoryWriter.Render(r)["logic.md"]);
    }

    [Fact]
    public void Ocr_numbers_are_money_or_quantity_shaped_and_constant_factors_stay_between_a_thousandth_and_a_thousand()
    {
        Assert.True(OcrLogic.TryNumber("45.3426", out var v) && v == 45.3426m);
        Assert.True(OcrLogic.TryNumber("$1,234.50", out _));
        Assert.False(OcrLogic.TryNumber("1.234567", out _));   // more than four decimals: not money nor a quantity
        Assert.False(OcrLogic.TryNumber("0000776", out _));
        Assert.False(OcrLogic.TryNumber("11/19/2026", out _));
        var s = new LearnState();
        // 1234 = x × 0.0001 and y × 5000 are outside the range; 100 = z × 0.25 is inside
        LogicLearner.LearnCalc(s, "W", "Total", 1234m, new List<(string, decimal)> { ("Big", 12340000m), ("Tiny", 0.2468m) }, "f");
        Assert.Empty(s.CalcFits);
        LogicLearner.LearnCalc(s, "W", "Total", 100m, new List<(string, decimal)> { ("Base", 400m) }, "f");
        Assert.Contains(s.CalcFits.Keys, k => k.EndsWith("Base × 0.25"));
        // a negative factor needs a visible minus: none appears
        LogicLearner.LearnCalc(s, "W", "Due", -500m, new List<(string, decimal)> { ("Paid", 250m) }, "f");
        Assert.DoesNotContain(s.CalcFits.Keys, k => k.Contains("× -"));
        Assert.True(LogicLearner.IsToggleText("On") && LogicLearner.IsToggleText("Indeterminate") && !LogicLearner.IsToggleText("One"));
    }

    [Fact]
    public void A_screen_without_a_record_still_learns_from_pictures_when_the_labels_look_like_fields()
    {
        var win = "Custom Drawn Quote";
        var t = new DateTime(2026, 11, 19, 10, 0, 0);
        var s = new LearnState();
        var states = new List<OcrState>();
        var lines = new[] { ("1", "45.34"), ("5", "226.71"), ("20", "906.85") };
        for (var i = 0; i < 3; i++)
            states.Add(new OcrState { Screen = win, File = $"screens/shots/custom/{i}.png.ocr.json", T = t.AddSeconds(i * 30 + 1), Lines = new List<OcrLine> { L("Qty", 100, 100, 40), L(lines[i].Item1, 160, 100, 30), L("Unit Price", 300, 100, 70), L("45.3426", 380, 100), L("Line Total", 500, 100, 70), L(lines[i].Item2, 580, 100) } });
        var session = Enumerable.Range(0, 3).Select(i => Synthetic.Ev(t.AddSeconds(i * 30), Acts.Value, win, "Qty", lines[i].Item1)).ToList();
        OcrLogic.Accumulate(s, session, states, OcrLogic.KnownFields(new List<ScreenRecord>()));
        Assert.Contains(s.CalcFits.Keys, k => k.Contains("Line Total") && (k.Contains("Qty × Unit Price") || k.Contains("Unit Price × Qty")));
    }

    // ---------- 6. privacy ----------

    [Fact]
    public void Personal_fields_the_program_fills_keep_their_shape_and_the_company_name_leaves_the_title()
    {
        Assert.Equal("###-###-####", Masking.ForAppValue("212-555-0100", "Phone", "txtPhone", false));
        Assert.Equal("Aaaa Aaaa", Masking.ForAppValue("Test Name", "Name", "", false));
        Assert.Equal("# Aaaaaa Aaaaaa", Masking.ForAppValue("1 Sample Street", "Address", "", false));
        Assert.StartsWith("aaaaaa#@", Masking.ForAppValue("writer1@example.com", "Email", "", false));
        Assert.Equal("Aaaa", Masking.ForAppValue("Test", "Attention", "", false));
        Assert.Equal("226.71", Masking.ForAppValue("226.71", "Price Extension", "", false)); // a number stays a number
        Assert.Equal("PICK UP", Masking.ForAppValue("PICK UP", "Ship Via", "", false));
        Assert.StartsWith("<a aaaa=", Masking.ForAppValue("<a href=\"x@example.com\">x@example.com</a>", "Website", "llblEmail", false));
        Assert.True(Masking.IsPersonalField("Fax") && Masking.IsPersonalField("Address Line1") && Masking.IsPersonalField("Line 2") && !Masking.IsPersonalField("Ship Via"));
        Assert.Equal("***", Masking.ForAppValue("x", "Password", "", false));
        var saved = Titles.MaskPatterns;
        try
        {
            Titles.MaskPatterns = new List<string> { "DDI System Inform - *" };
            Assert.Equal("DDI System Inform - …", Titles.Key("DDI System Inform - Sample Co"));
            Assert.Equal("DDI System Inform - … › Price Change", Titles.Key("DDI System Inform - Sample Co › Price Change"));
            Assert.Equal("Vendor - # \"…\" Internal Notes", Titles.Key("Vendor - 00001 \"ACME\" Internal Notes"));
            Assert.Equal("Close?", Titles.Key("Close?"));
            var input = Input();
            var r = Learner.Learn(input);
            var all = string.Join("\n", MemoryWriter.Render(r).Values);
            Assert.DoesNotContain("Sample Co", all); // the company name (the stand-in for the real one) is gone from every memory file
            Assert.Contains("DDI System Inform - … › Price Change", all);
        }
        finally { Titles.MaskPatterns = saved; }
        var p = ProfileStore.ByName("ddi-inform")!;
        Assert.Contains("DDI System Inform - *", p.TitleMasks);
        Assert.True(p.SelectionReads);
    }

    // ---------- 7. flows ----------

    [Fact]
    public void No_flow_starts_at_the_notes_window_the_program_opens_and_program_fills_are_not_steps()
    {
        var r = Learner.Learn(Input());
        Assert.DoesNotContain(r.Flows, f => f.Key.Contains("Internal Notes"));
        Assert.True(r.Flows.Sum(f => f.Count) >= 6, $"{r.Flows.Sum(f => f.Count)} flow runs");
        Assert.DoesNotContain(r.Flows.SelectMany(f => f.StepControls.Values).SelectMany(c => c).Select(c => c.name), n => n is "type 12 Mon Dmnd" or "type 12 Mon Hits" or "type Amount");
        Assert.Contains(r.Flows, f => f.Key.EndsWith("› Price Change"));
        // the notes window is still a screen that was seen, just not a step people take on purpose
        Assert.Contains(r.Screens, s => s.Title.Contains("Internal Notes"));
        var md = MemoryWriter.Render(r)["flows.md"];
        Assert.DoesNotContain("| Vendor - # \"…\" Internal Notes | 7 |", md);
    }

    [Fact]
    public void A_window_that_opens_two_seconds_after_the_last_click_is_shown_by_the_program()
    {
        var t = new DateTime(2026, 11, 19, 9, 0, 0);
        var events = new List<UiEvent>();
        void Ev(double sec, string act, string win, string name, string ct = "Edit", string? by = null) { var e = Synthetic.Ev(t.AddSeconds(sec), act, win, name, ct: ct); e.By = by; events.Add(e); }
        Ev(0, Acts.Foreground, "Main", "Main", "Window");
        Ev(1, Acts.Invoke, "Main", "Vendor Master", "Button");
        Ev(2, Acts.Select, "Main › Vendor Master", "Vendor Master", "TabItem");
        Ev(8, Acts.Value, "Main › Vendor Master", "Vendor");        // typing the vendor code loads it...
        Ev(9, Acts.Foreground, "Vendor Notes", "Vendor Notes", "Window"); // ...and the program pops its notes: no click in the 2 s before
        Ev(10, Acts.Value, "Main › Vendor Master", "Name", "Edit", ValueBy.App);
        Ev(30, Acts.Invoke, "Main › Vendor Master", "Exit", "Button");
        Ev(31, Acts.Foreground, "Main", "Main", "Window");
        Ev(32, Acts.Invoke, "Main", "Price Change", "Button");
        Ev(33, Acts.Foreground, "Main › Price Change", "Main › Price Change", "Window"); // opened by a click: a visit
        Ev(40, Acts.Foreground, "Main", "Main", "Window");
        var visits = FlowLearner.Visits(events);
        Assert.True(visits.First(v => v.Win == "Vendor Notes").ShownByProgram);
        Assert.False(visits.First(v => v.Win == "Main › Price Change").ShownByProgram);
        Assert.False(visits[0].ShownByProgram);
        Assert.DoesNotContain("type Name", visits.First(v => v.Win == "Main › Vendor Master").Controls);
        var (flows, _, main, _) = FlowLearner.Learn(events);
        Assert.Equal("Main", main);
        Assert.DoesNotContain(flows, f => f.Key == "Vendor Notes");
        Assert.Contains(flows, f => f.Key == "Main › Vendor Master");
        Assert.Contains(flows, f => f.Key == "Main › Price Change");
    }

    // ---------- 8. backends and health ----------

    [Fact]
    public void The_profiles_backend_is_the_data_server_and_log_file_hosts_are_services()
    {
        var input = Input();
        Assert.Equal("universe", input.PrimaryBackend()!.Kind);
        var r = Learner.Learn(input);
        var health = MemoryWriter.Render(r)["health.md"];
        Assert.Contains("universe server (192.0.2.10, account DDI)", health);
        Assert.DoesNotContain("rest server", health);
        Assert.Contains("universe backend at 192.0.2.10", health);
        var arch = MemoryWriter.Render(r)["architecture.md"];
        Assert.Contains("Service the program talks to: rest, shop.example", arch);
        Assert.Contains("Backend: universe, 192.0.2.10", arch);
        // without a profile kind, a database server still beats a web service; services alone are not a data server
        var noKind = new LearnInput { Backends = input.Backends.ToList() };
        Assert.Equal("universe", noKind.PrimaryBackend()!.Kind);
        var servicesOnly = new LearnInput { Backends = input.Backends.Where(b => b.IsService).ToList(), HasDataSources = false };
        Assert.Null(servicesOnly.PrimaryBackend());
        Assert.Contains(Learner.Expectations(servicesOnly), e => e.Contains("web services at shop.example, support.example") && e.Contains("not its data server"));
    }

    [Fact]
    public void Health_lists_the_documents_never_recorded_and_the_screens_typed_on_without_a_typed_value()
    {
        var t = new DateTime(2026, 11, 19, 13, 11, 40);
        var main = "DDI System Inform - Sample Co"; var soe = main + " › Sales Order Entry";
        var input = Input();
        input.Collector!.DocumentsNeverRecorded = new List<string> { "Sales Order Entry", "Purchase Order Entry", "Price Change" };
        input.Collector.MdiDocuments = 12;
        for (var i = 0; i < 6; i++) input.Events.Add(Synthetic.Ev(t.AddSeconds(i * 3), Acts.Focus, soe, "Quant Ordered", ct: "DataItem"));
        input.Events.Add(Synthetic.Ev(t, Acts.Select, soe, "Sales Order Entry", ct: "TabItem"));
        var r = Learner.Learn(input);
        Assert.Equal(new[] { main + " › Purchase Order Entry", main + " › Sales Order Entry" }, r.Health.DocumentsNeverRecorded.OrderBy(x => x).ToArray()); // Price Change has a record
        Assert.Equal(new[] { soe }, r.Health.TypedButUnrecorded);
        var health = MemoryWriter.Render(r)["health.md"];
        Assert.Contains("2 documents were opened but never recorded as screens: Purchase Order Entry, Sales Order Entry.", health);
        Assert.Contains("On 1 screen people typed but no typed value was recorded: Sales Order Entry.", health);
        Assert.Contains("the document DDI System Inform - Sample Co › Sales Order Entry was opened but never recorded", health);
        Assert.Contains(input.Collector.Problems(), p => p.StartsWith("3 document(s) were opened but never recorded"));
        var facts = PlainReport.FromLearn(r);
        Assert.Equal(2, facts.DocumentsNeverRecorded.Count);
        Assert.Contains(PlainReport.Lines(facts, false), l => l.Contains("never recorded as screens"));
        Assert.DoesNotContain("Nothing is missing", string.Join("\n", PlainReport.Lines(facts, false)));
    }

    // ---------- 9. small ----------

    [Fact]
    public void A_grid_that_holds_the_cell_being_typed_in_is_not_a_lookup_and_a_field_value_tip_is_dropped()
    {
        var before = OrderLine("", "", "");
        var after = OrderLine("", "", "").ToList();
        after[4] = R(1, "DataGrid", "", "gridLines", "20,220 900x330"); // the order grid grew a row: still the same grid, and it holds the cell
        Assert.Null(FieldReads.NewLookup(before, after, "170,244 100x20"));
        after.Add(R(1, "List", "", "lstLookup", "170,270 400x120"));
        after.Add(R(2, "ListItem", "100EX | SAMPLE PRODUCT", "", "170,270 400x20"));
        after.Add(R(2, "ListItem", "100EXA | SAMPLE PRODUCT A", "", "170,290 400x20"));
        Assert.NotNull(FieldReads.NewLookup(before, after, "170,244 100x20")); // a list that was not there before, under the cell, is one
        Assert.True(Hints.IsFieldValueTip("Edit", "Remit-to:\r\nSame"));
        Assert.True(Hints.IsFieldValueTip("Edit", "Phone: 000-000-0000"));
        Assert.False(Hints.IsFieldValueTip("Button", "Click to add a new Line Item row"));
        Assert.False(Hints.IsFieldValueTip("Button", "Change Branch"));
        Assert.False(Hints.IsFieldValueTip("Edit", "Enter the customer code or press F3 to search"));
        Assert.False(Hints.IsFieldValueTip("TabItem", "Procurement"));
    }

    [Fact]
    public void The_v08_session_learns_its_dialogs_menus_and_documents_the_same_way_with_the_new_rules()
    {
        var input = Input();
        var r = Learner.Learn(input);
        Assert.Equal("DDI System Inform - Sample Co", r.MainWindow);
        Assert.Equal(5, r.Health.DialogsSeen);
        Assert.Equal(7, r.Health.TabSelections);
        Assert.True(r.Screens.Count(s => s.Kind == ScreenKinds.Tab) >= 7);
        Assert.DoesNotContain(r.Screens, s => s.Title is "Close?" or "Protected Area" or "Sales" or "Pricing");
        Assert.Empty(r.Logic.Calculations); // no formula is right in this run: the tree never showed two reads of the extension
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.DoesNotContain("copied from", md);
        Assert.DoesNotContain("× -", md);
        Assert.Contains("Exit opens Close? (a message)", md);
        // a learn run twice and an incremental run give the same memory
        var again = MemoryWriter.Render(Learner.Learn(Input()));
        foreach (var (name, text) in MemoryWriter.Render(r)) Assert.Equal(text, again[name]);
    }

    // ---------- the fixtures hold none of the real strings ----------

    private static readonly string[] ForbiddenHashes =
    {
        "03b009bc8f59b399d36222d389b036dba3bdf682fb683d9e19e84c0abab12348", "61fff898362d7177526d7b3cb137a53ea619bc98efc28f6327d9693a110446bd",
        "894d0732ac2d85de699ffbeaed0e919268c59d6d3ed3f4280ace267681d6d0ad", "864b24fdae91ef938ba12b344c1652425531133b3194c9114475269e17257c47",
        "64c47e55f48c8db98cb0d82154a31ceeed78c56fe898e6c1d7141a8897b80f0d", "2d820fcfdd4b6ecb9f208c323e889a2c39e49f6bf040105edc253014a1642f59",
        "9b45101315171aeba9aee6186e015f23408d24851037e3f8c6fa8ede22bb3da7", "6f08f9cfc3770b74b474456543aba50fb6f439db5c2b99f772b646f741ea17c9",
        "5a2a558c78d3717db731600c4f354fa1d9c84b556f108091a891f444f1bdec40", "dffda0f1b7e2841e6309915625eb9646810a5408258dd17cff33abb126430b02",
        "18b7c80b7335a0bf5f2da3676ce45acef892d67637e13ff2065a727d85d127d7", "844c63b9bc1643a92f574bdaef9f9be3926f9ecd98e63801e991de33f8df1fb2",
        "f7365d26a878accd989dd51bd0ec16cd4f5784a9123a866cfcacefd81c07e0a6", "b5f0651d428ce2723831d18544b20740b8d56424ac4280a58ebf93cc3dff8c58",
        "acea6bdb7a0c4c279606d22803bedefabe1ba4f33f7b2a432b82c05b089fe9c9", "0b4f0d3d03d47875821d675f3ee2cb1cfbd1d469ff08b341e809f6da7e5b46db",
        "1865e4f9be9639fc76d7d4da8ee25eaa7421f6837accb67516937e69469ea7e4", "f58a468c32cb2e8370d77981eec43a4ca7ca84e1f15b1c7ba11e797e812854db",
        "3578b13880d5e0ce2530069c46e0a265f188dbd3b4d54264324f80ae38ebbe5c", "37bf713760c5cbad0600a2741cb115f3becc4b2469d6dd7b3346ded41395f43d",
        "cf49591a32a6133a189d0b21103c06155665fcde45005dc7241f84725364f2b3", "c3d6fbc6d9d3e3d2887c0f9a6f779b671672b155d9834db0a8b54636c18f826e",
        "c4ea3ef3f20baa29f3d18f55f23b21547d77b436454198b906561b4a84120c57", "79a6a933dfc9b1975e444d4e8481c64c771d8ab40b7ac72f8bc1a1bca1718bef",
        "a6761ccff1191f3ee53acada4f7965241538511ef6eb52d37974507ab5a9023e", "36f2fa50fd29547b9dcf220852c96b4f322004d97b740f4a5b4987b5d2ee15d1",
        "7bd572d16535c987d6e9c572e7afafe844d4d189c26f793a8a7b1bae4a84b5a5", "5d79591518c652094b5c64e4e2da241e7043fa4f51d4bc96ee17998a87a1ea7c",
        "3b6175d7078279b8dcc362a0adca711a3cde6529c83ec8aa302dbad9653ed788", "5eb957cdc050703f29e23504e01166d6dd551d821660b819568fd0fa25063138",
        "4862c3540fb29bebfbaa8ae271a39600e00beb1eca84a340294fc6ab289ff976",
    };

    [Fact]
    public void Both_fixture_folders_hold_none_of_the_real_strings()
    {
        var forbidden = new HashSet<string>(ForbiddenHashes, StringComparer.OrdinalIgnoreCase);
        static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).ToLowerInvariant();
        var files = Directory.GetFiles(Fixtures).Concat(Directory.GetFiles(FixturesV07)).ToList();
        Assert.True(files.Count >= 22);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var token in Regex.Split(text, @"[^A-Za-z0-9.&@-]+").Where(t => t.Length >= 3).Select(t => t.Trim('.', '-')).Distinct())
                Assert.False(forbidden.Contains(Hash(token)), $"a real string is in {Path.GetFileName(f)}");
            foreach (Match ip in Regex.Matches(text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"))
                Assert.StartsWith("192.0.2.", ip.Value);
            Assert.DoesNotMatch(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", text);
            Assert.DoesNotMatch(@"\(\d{3}\) ?\d{3}-\d{4}|\b\d{3}-\d{3}-\d{4}\b", text);
        }
        Assert.Equal(291, Events().Count);
        Assert.Contains(Events(), e => e.Name == "Writer One" || e.Val == "Writer One");
    }
}
