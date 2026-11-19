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
/// The fixes after the measured 0.7 run against a real tabbed-MDI ERP, checked against anonymised copies of what was
/// recorded (tests/Fixtures/inform, see its README): message boxes by shape, tab documents, values the program fills,
/// flows that end when a document closes, formulas from the pictures' text, honest health lines, backend discovery.
/// </summary>
public class InformFixturesTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform");
    private static ScreenRecord Record(string file) => ScreenRecord.Parse(Path.Combine(Fixtures, file))!;
    private static List<ScreenRecord> AllRecords() => Directory.GetFiles(Fixtures, "*.md").Where(f => !f.EndsWith("README.md")).Select(f => ScreenRecord.Parse(f)!).ToList();
    private static List<UiEvent> Events() => EventReader.ReadFile(Path.Combine(Fixtures, "events-2026-11-19.jsonl")).ToList();
    private static CollectorStatus Status() => System.Text.Json.JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(Path.Combine(Fixtures, "collector-status.json")))!;

    private static LearnInput Input()
    {
        var input = new LearnInput { AppName = "inform", Events = Events(), Screens = AllRecords(), Collector = Status(), HasDataSources = true, Now = new DateTime(2026, 11, 20) };
        input.SessionWindowPatterns.AddRange(new[] { "Splash Screen", "Login*", "Enter Register #*" });
        return input;
    }

    // ---------- 4. dialogs by structure ----------

    [Theory]
    [InlineData("close.md", "Close?", "OK", "Cancel")]
    [InlineData("no-selection-specified.md", "No Selection Specified", "Yes", "No")]
    [InlineData("advanced-demand-forecasting-and-purchasing.md", "Advanced Demand Forecasting and Purchasing", "OK", null)]
    public void The_three_real_message_boxes_are_dialogs_by_their_shape(string file, string title, string button1, string? button2)
    {
        var rec = Record(file);
        Assert.Equal(18, rec.Controls.Count); // the body is an Edit "lblBody" with an inner "[Editor] Edit Area", the buttons sit in panes
        Assert.Equal(title, rec.Title);
        var (isDialog, body) = DialogDetector.Detect("WindowsForms10.Window.8.app.0", rec.Controls);
        Assert.True(isDialog, $"{title} must be a dialog");
        Assert.Contains(title, body ?? "");
        Assert.True(DialogDetector.CouldBeDialog(rec.Controls));
        Assert.True(DialogDetector.CouldBeNamedDialog(rec.Controls));
        var buttons = DialogDetector.Logical(rec.Controls).Where(r => r.Type == "Button").Select(r => r.Name).ToList();
        Assert.Contains(button1, buttons);
        if (button2 is not null) Assert.Contains(button2, buttons);
        Assert.Single(DialogDetector.Logical(rec.Controls).Where(r => r.Type == "Edit")); // the wrapper editor folds into lblBody
        Assert.True(DialogDetector.LooksLikePromptTitle(title) || title.StartsWith("Advanced"));
    }

    [Fact]
    public void A_real_screen_with_a_text_box_and_two_buttons_is_not_a_dialog_and_a_popup_menu_is_a_menu()
    {
        var notes = Record("vendor-internal-notes.md");
        Assert.False(DialogDetector.Detect("x", notes.Controls).isDialog); // Edit and Exit are not message-box buttons; txtNotes is a field
        var sales = Record("sales.md");
        Assert.True(DialogDetector.IsMenuWindow(sales.Controls));
        Assert.Equal("Sales", DialogDetector.MenuName("&Sales - 0"));
        Assert.Equal("Pricing", DialogDetector.MenuName("&Pricing - 1"));
        Assert.False(DialogDetector.IsMenuWindow(Record("ddi-system-inform-sample-co.md").Controls));
    }

    [Fact]
    public void The_profile_names_the_real_prompts_and_no_real_screen()
    {
        var p = ProfileStore.ByName("ddi-inform")!;
        Assert.Contains("No Selection Specified", p.DialogWindows);
        Assert.Contains("*Specified", p.DialogWindows);
        Assert.Contains("Close?", p.DialogWindows);
        Assert.DoesNotContain("Advanced Demand Forecasting", p.IgnoreWindows);
        Assert.Contains("DDI System Inform Login", p.SessionWindows);
        Assert.Equal("universe", p.BackendKind);
        Assert.Contains("inference", p.Note!);
        Assert.Contains("re-read", p.Note!);
    }

    // ---------- 2. tab documents ----------

    private static ControlRow R(int depth, string type, string name = "", string aid = "", string pos = "10,10 100x20") => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos };

    /// <summary>The main window as it was with two documents open: the real toolbars plus a tab strip and two MDI children.</summary>
    private static List<ControlRow> MainWithDocuments()
    {
        var main = Record("ddi-system-inform-sample-co.md").Controls.ToList();
        var rows = new List<ControlRow> { main[0] };
        rows.Add(R(1, "Tab", "", "854088", "0,121 1920x34"));
        rows.Add(R(2, "TabItem", "New Sales Order", "", "2,123 178x31"));
        rows.Add(R(3, "Button", "TabCloseButton", "", "155,131 17x17"));
        rows.Add(R(2, "TabItem", "Purchase Order Entry", "", "183,123 210x32"));
        rows.Add(R(3, "Button", "TabCloseButton", "", "368,132 17x17"));
        rows.Add(R(1, "Pane", "", "197300", "0,121 1920x889"));
        rows.Add(R(2, "Window", "Purchase Order Entry", "PurchaseOrderEntryView", "0,155 1920x855"));
        rows.Add(R(3, "Pane", "", "pnlWork", "0,155 1920x855"));
        rows.Add(R(4, "Text", "Vendor", "", "20,200 60x20"));
        rows.Add(R(4, "Edit", "", "srcVendor", "90,200 120x24"));
        rows.Add(R(5, "Edit", "", "srcVendor_EmbeddableTextBox", "92,202 116x20"));
        rows.Add(R(4, "Text", "Buyer", "", "20,240 60x20"));
        rows.Add(R(4, "Edit", "", "txtBuyer", "90,240 120x24"));
        rows.Add(R(4, "Button", "Add Line Item", "cmdAddLine", "20,300 120x30"));
        rows.Add(R(4, "Button", "Exit", "cmdExit", "160,300 120x30"));
        rows.Add(R(2, "Window", "New Sales Order", "SalesOrderEntryView", "0,0 0x0"));
        rows.Add(R(3, "Pane", "", "pnlWork", "0,0 0x0"));
        rows.Add(R(4, "Button", "Add Line Item", "cmdAddLine", "0,0 0x0"));
        rows.AddRange(main.Skip(1)); // the toolbars, menu bar and status bar of the window
        return rows;
    }

    [Fact]
    public void A_tab_screen_holds_only_the_selected_document_never_the_toolbar_or_other_documents()
    {
        var rows = MainWithDocuments();
        var doc = TabDocuments.Document(rows, "Purchase Order Entry");
        Assert.True(doc.Count >= 8);
        Assert.Equal(0, doc[0].Depth);
        Assert.Equal("Purchase Order Entry", doc[0].Name);
        Assert.Contains(doc, r => r.Name == "Add Line Item");
        Assert.DoesNotContain(doc, r => r.Name is "Customer Master" or "Bank Ledger" or "Change User"); // toolbar buttons of the window
        Assert.DoesNotContain(doc, r => r.Type is "ToolBar" or "StatusBar" or "MenuBar" or "Tab");
        Assert.DoesNotContain(doc, r => r.Name == "New Sales Order"); // the other document
        Assert.Equal(new[] { "New Sales Order", "Purchase Order Entry" }, TabDocuments.TabNames(rows));

        // a record made the old way (the whole window on screen) loses the parent window's controls for the "not used" lists
        var cleaned = TabDocuments.WithoutParentControls(rows, Record("ddi-system-inform-sample-co.md").Controls);
        Assert.DoesNotContain(cleaned, r => r.Name is "Customer Master" or "Bank Ledger");
        Assert.Contains(cleaned, r => r.Name == "Add Line Item");
    }

    [Fact]
    public void The_active_tab_follows_what_the_tab_strip_says_and_clears_when_the_tab_is_gone()
    {
        Assert.Equal("Purchase Order Entry", TabDocuments.Active(new[] { new TabDocuments.TabItem("New Sales Order", false), new TabDocuments.TabItem("Purchase Order Entry", true) }));
        Assert.Null(TabDocuments.Active(new[] { new TabDocuments.TabItem("New Sales Order", false) })); // the selected tab was closed, nothing selected
        // a strip that answers "not selected" for every item (WinForms does) keeps the current tab while its item is there
        Assert.Equal("Quote", TabDocuments.Active(new[] { new TabDocuments.TabItem("Orders", false), new TabDocuments.TabItem("Quote", false) }, "Quote"));
        Assert.Null(TabDocuments.Active(new[] { new TabDocuments.TabItem("Orders", false) }, "Quote"));
        Assert.Equal("Orders", TabDocuments.Active(new[] { new TabDocuments.TabItem("Orders", true), new TabDocuments.TabItem("Quote", false) }, "Quote"));
        Assert.Null(TabDocuments.Active(Array.Empty<TabDocuments.TabItem>()));
        Assert.Equal("Main › Orders", TabDocuments.Title("Main", "Orders"));
    }

    [Fact]
    public void Logic_report_lists_only_the_documents_own_controls_as_not_used_and_asks_about_no_toolbar_button()
    {
        var t = new DateTime(2026, 11, 19, 10, 0, 0);
        var mainTitle = "DDI System Inform - Sample Co";
        var tabTitle = mainTitle + " › Purchase Order Entry";
        var tab = new ScreenRecord { Title = tabTitle, Kind = ScreenKinds.Tab, Controls = MainWithDocuments() }; // recorded the old way: whole window
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1), Screens = { Record("ddi-system-inform-sample-co.md"), tab } };
        input.Events.Add(Synthetic.Ev(t, Acts.Foreground, mainTitle, mainTitle, ct: "Window"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(5), Acts.Select, tabTitle, "Purchase Order Entry", ct: "TabItem"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(6), Acts.Open, tabTitle, tabTitle, ct: "Window"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(10), Acts.Value, tabTitle, "Vendor", "00001"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(20), Acts.Invoke, tabTitle, "Exit", ct: "Button"));
        var r = Learner.Learn(input);
        var notUsed = r.Logic.NotUsed.Where(n => n.Screen == tabTitle).ToList();
        Assert.Contains(notUsed, n => n.Kind == "button" && n.Name == "Add Line Item");
        Assert.DoesNotContain(notUsed, n => n.Name is "Bank Ledger" or "Customer Master" or "Change User");
        Assert.DoesNotContain(r.Logic.Questions, q => q.Text.Contains("Bank Ledger"));
        Assert.Contains(r.Logic.Questions, q => q.Text.StartsWith("What does Add Line Item on"));
        Assert.True(r.Logic.Questions.Count(q => q.Kind == "button" && q.Screen == tabTitle) <= 10);
    }

    // ---------- 3. values the program fills in grids and bound editors ----------

    private static List<ControlRow> OrderLine(string qty, string price, string ext, string desc)
    {
        ControlRow V(int depth, string type, string name, string aid, string pos, string? value) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos, Value = value };
        return new List<ControlRow>
        {
            V(0, "Window", "Sales Order Entry", "SalesOrderEntryView", "0,0 1920x1000", null),
            V(1, "Text", "Customer", "", "20,50 70x20", null),
            V(1, "Edit", "", "srcCustomer", "100,50 120x24", "0000001"),
            V(2, "Edit", "", "srcCustomer_EmbeddableTextBox", "102,52 116x20", "0000001"),
            V(1, "Text", "Ship To", "", "20,90 70x20", null),
            V(1, "Edit", "", "txtShipTo", "100,90 200x24", desc.Length > 0 ? "Sample Co, Main St" : ""),
            V(1, "DataGrid", "", "gridLines", "20,200 900x300", null),
            V(2, "Header", "", "Band Headers", "20,200 900x24", null),
            V(3, "HeaderItem", "Product", "", "20,200 150x24", null),
            V(3, "HeaderItem", "Product Description", "", "170,200 300x24", null),
            V(3, "HeaderItem", "Quant Ordered", "", "470,200 100x24", null),
            V(3, "HeaderItem", "Net Price", "", "570,200 100x24", null),
            V(3, "HeaderItem", "Price Extension", "", "670,200 120x24", null),
            V(2, "DataItem", "Row 0", "", "20,224 900x24", null),
            V(3, "Edit", "Product", "_EmbeddableTextBox", "20,224 150x24", "100EX"),
            V(3, "Edit", "Product Description", "_EmbeddableTextBox", "170,224 300x24", desc),
            V(3, "Edit", "Quant Ordered", "_EmbeddableTextBox", "470,224 100x24", qty),
            V(3, "Edit", "Net Price", "_EmbeddableTextBox", "570,224 100x24", price),
            V(3, "Edit", "Price Extension", "_EmbeddableTextBox", "670,224 120x24", ext),
        };
    }

    [Fact]
    public void Typing_in_one_grid_cell_shows_as_the_program_filling_the_other_cells()
    {
        var before = FieldReads.FromRows(OrderLine("", "", "", ""));
        var after = FieldReads.FromRows(OrderLine("1", "45.3426", "45.34", "SAMPLE PRODUCT"));
        Assert.Contains(before, f => f.Label == "Customer" && f.AutomationId == "srcCustomer" && f.Value == "0000001"); // the wrapper editor folds into srcCustomer
        Assert.DoesNotContain(before, f => f.AutomationId == "srcCustomer_EmbeddableTextBox");
        var filled = FieldReads.Filled(before, after, new HashSet<string> { "Product", "_EmbeddableTextBox" });
        var labels = filled.Select(f => f.Label).ToList();
        Assert.Equal(new[] { "Ship To", "Product Description", "Quant Ordered", "Net Price", "Price Extension" }, labels);
        Assert.All(filled.Where(f => f.Label != "Ship To"), f => Assert.True(f.IsCell));
        // the user then types the quantity: only the extension counts as filled
        var later = FieldReads.FromRows(OrderLine("5", "45.3426", "226.71", "SAMPLE PRODUCT"));
        var filled2 = FieldReads.Filled(after, later, new HashSet<string> { "Quant Ordered" });
        var one = Assert.Single(filled2);
        Assert.Equal("Price Extension", one.Label); Assert.Equal("226.71", one.Value);
        // a baseline with nothing before is never reported as fills
        Assert.Empty(FieldReads.Filled(new List<FieldRead>(), after, new HashSet<string>()));
    }

    [Fact]
    public void The_learner_turns_re_read_fills_into_the_extension_formula_and_the_header_links()
    {
        var t = new DateTime(2026, 11, 19, 10, 12, 0);
        var win = "DDI System Inform - Sample Co › Sales Order Entry";
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1) };
        UiEvent App(DateTime at, string field, string val, string ct = "DataItem") { var e = Synthetic.Ev(at, Acts.Value, win, field, val, ct); e.By = ValueBy.App; return e; }
        var lines = new[] { ("1", "45.3426", "45.34"), ("5", "45.3426", "226.71"), ("20", "45.3426", "906.85") };
        for (var i = 0; i < 3; i++)
        {
            var at = t.AddMinutes(i);
            input.Events.Add(Synthetic.Ev(at, Acts.Value, win, "Customer", "0000001"));
            foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) input.Events.Add(App(at.AddMilliseconds(400), f, "x" + i, "Edit"));
            input.Events.Add(Synthetic.Ev(at.AddSeconds(5), Acts.Value, win, "Product", "100EX"));
            input.Events.Add(App(at.AddSeconds(5.4), "Product Description", "SAMPLE PRODUCT"));
            input.Events.Add(App(at.AddSeconds(5.4), "Net Price", lines[i].Item2));
            input.Events.Add(Synthetic.Ev(at.AddSeconds(9), Acts.Value, win, "Quant Ordered", lines[i].Item1));
            input.Events.Add(App(at.AddSeconds(9.4), "Price Extension", lines[i].Item3));
        }
        var r = Learner.Learn(input);
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Price Extension");
        Assert.Equal("Net Price × Quant Ordered", calc.Formula);
        Assert.Equal(3, calc.Fits); Assert.Equal(3, calc.Tested); Assert.False(calc.FromOcr);
        var links = r.Logic.Links.Where(l => l.When == "typed:Customer").Select(l => l.Field).ToList();
        Assert.Equal(new[] { "Branch", "Ship To", "Ship Via", "Whse", "Writer" }, links.OrderBy(x => x).ToArray());
        Assert.Contains(r.Logic.Links, l => l.When == "typed:Product" && l.Field == "Product Description");
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.Contains("Typing Customer fills Branch (seen 3× of 3), Ship To (seen 3× of 3)", md);
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 3× of 3)", md);
        Assert.DoesNotContain("0000001", md);
    }

    [Fact]
    public void A_lookup_list_that_opens_under_the_field_inside_the_window_is_a_dropdown()
    {
        var before = OrderLine("", "", "", "");
        var after = OrderLine("", "", "", "").ToList();
        after.Add(R(1, "List", "", "lstLookup", "20,250 400x120"));
        after.Add(R(2, "ListItem", "100EX | SAMPLE PRODUCT", "", "20,250 400x20"));
        after.Add(R(2, "ListItem", "100EXA | SAMPLE PRODUCT A", "", "20,270 400x20"));
        var lookup = FieldReads.NewLookup(before, after, "20,224 150x24");
        Assert.NotNull(lookup);
        Assert.Equal("Lookup", lookup!.Value.grid);
        Assert.Equal(3, lookup.Value.lines.Count); // header line plus two rows
        Assert.Null(FieldReads.NewLookup(before, after, "1500,224 150x24")); // a list far from the field is not its lookup
        Assert.Null(FieldReads.NewLookup(after, after, "20,224 150x24")); // nothing new
    }

    // ---------- 5. flows end when a document closes ----------

    [Fact]
    public void The_real_session_gives_many_flows_not_one_and_its_prompts_are_messages_not_screens()
    {
        var input = Input();
        var r = Learner.Learn(input);
        Assert.True(r.Flows.Sum(f => f.Count) >= 8, $"{r.Flows.Sum(f => f.Count)} flow runs; one 25-minute flow is useless");
        Assert.True(r.Flows.Count >= 4, $"{r.Flows.Count} distinct flows"); // 4 since 0.9: the vendor notes window the program pops up starts no flow
        Assert.Equal("DDI System Inform - Sample Co", r.MainWindow);
        Assert.True(r.Flows.All(f => f.MedianMs < 20 * 60_000), "a flow longer than 20 minutes survived: " + string.Join("; ", r.Flows.Select(f => $"{f.Key} {f.MedianMs / 60000:0.0} min")));
        Assert.DoesNotContain(r.Screens, s => s.Title is "Close?" or "No Selection Specified" or "Advanced Demand Forecasting and Purchasing");
        Assert.DoesNotContain(r.Screens, s => s.Title is "Sales" or "Pricing" or "&Sales - #" or "&Pricing - #"); // popup menus are not screens
        Assert.True(r.MessageWindows.TryGetValue("Close?", out var closes) && closes >= 12, $"Close? seen {closes} times as a message window (13 times in the session)");
        Assert.True(r.Health.DialogsSeen >= 12, $"{r.Health.DialogsSeen} dialog events: the prompts recorded as screens by the old collector count as messages");
        Assert.Contains(r.Screens, s => s.Title == "DDI System Inform - Sample Co › Purchase Order Entry" && s.Kind == ScreenKinds.Tab);
        var md = MemoryWriter.Render(r);
        Assert.DoesNotContain("Close?", md["logic.md"].Split("## Questions")[0].Split("## Screen: ").Skip(1).Select(s => s.Split('\n')[0]).ToList());
        var dialogs = Learner.DialogWindows(input);
        Assert.Contains("Close?", dialogs); Assert.Contains("No Selection Specified", dialogs); Assert.Contains("Advanced Demand Forecasting and Purchasing", dialogs);
    }

    [Fact]
    public void Flows_are_cut_at_the_main_window_a_closing_prompt_a_tab_close_and_a_leave_press()
    {
        var t = new DateTime(2026, 11, 19, 9, 0, 0);
        var main = "Main"; var a = "Main › Orders"; var b = "Main › Invoices";
        var events = new List<UiEvent>();
        void Ev(double sec, string act, string win, string name, string ct = "Edit") => events.Add(Synthetic.Ev(t.AddSeconds(sec), act, win, name, ct: ct));
        // 1: a document, Close? answered, a different document after it
        Ev(0, Acts.Foreground, main, main, "Window"); Ev(1, Acts.Value, a, "Customer"); Ev(2, Acts.Dialog, "Close?", "Close?", "Window"); Ev(3, Acts.Value, b, "Vendor");
        // 2: Close? answered with Cancel: the same document goes on (no cut), then a tab close event ends it
        Ev(5, Acts.Dialog, "Close?", "Close?", "Window"); Ev(6, Acts.Value, b, "Vendor"); Ev(7, Acts.Close, b, b, "Window");
        // 3: Exit pressed, then the main window
        Ev(10, Acts.Value, a, "Customer"); Ev(11, Acts.Invoke, a, "Exit", "Button"); Ev(12, Acts.Foreground, main, main, "Window");
        // 4: another document
        Ev(14, Acts.Value, b, "Vendor");
        var (flows, _, mainFound, _) = FlowLearner.Learn(events, null, new HashSet<string> { "Close?" });
        Assert.Equal(main, mainFound);
        Assert.Equal(4, flows.Sum(f => f.Count));
        Assert.Equal(2, flows.Single(f => f.Key == a).Count);
        Assert.Equal(2, flows.Single(f => f.Key == b).Count);
        Assert.DoesNotContain(flows, f => f.Key == "Close?");
        Assert.All(flows, f => Assert.DoesNotContain("Close?", f.CommonPath));
    }

    [Fact]
    public void A_long_run_with_many_steps_is_split_at_its_prompts()
    {
        var t = new DateTime(2026, 11, 19, 9, 0, 0);
        var events = new List<UiEvent>();
        var win = "Main › Price Matrix Master";
        for (var i = 0; i < 60; i++)
        {
            events.Add(Synthetic.Ev(t.AddSeconds(i * 30), Acts.Value, win, "Field " + i % 7));
            if (i % 12 == 11) events.Add(Synthetic.Ev(t.AddSeconds(i * 30 + 10), Acts.Dialog, "Close?", "Close?", ct: "Window"));
        }
        var (flows, _, _, _) = FlowLearner.Learn(events, null, new HashSet<string> { "Close?" });
        Assert.Equal(5, flows.Single().Count);
        Assert.True(flows.Single().MedianMs <= 6 * 60_000);
    }

    // ---------- 6. formulas from the pictures' text ----------

    private static OcrState Picture(DateTime t, string file, string qty, string price, string ext, string total)
    {
        OcrLine L(string text, int x, int y, int w = 0) => new() { Text = text, X = x, Y = y, W = w > 0 ? w : text.Length * 8, H = 16 };
        return new OcrState
        {
            Screen = "DDI System Inform - Sample Co › Sales Order Entry", File = "screens/shots/sales-order-entry/" + file + ".png.ocr.json", T = t, Label = file,
            Lines =
            {
                L("Customer", 20, 50), L("0000001", 100, 50), L("Ship To", 20, 90), L("Sample Co, Main St", 100, 90), L("Order Date", 400, 50), L("11/19/2026", 500, 50),
                L("Product", 20, 200), L("Product Description", 170, 200), L("Quant Ordered", 470, 200), L("Net Price", 570, 200), L("Price Extension", 670, 200),
                L("100EX", 20, 230), L("SAMPLE PRODUCT", 170, 230), L(qty, 470, 230), L(price, 570, 230), L(ext, 670, 230),
                L("Order Total", 600, 500), L(total, 700, 500), L("Page 1 of 1", 20, 900),
            },
        };
    }

    [Fact]
    public void Numbers_on_the_pictures_pair_with_their_labels_and_codes_are_not_numbers()
    {
        var pairs = OcrLogic.Pairs(Picture(DateTime.Now, "x", "5", "45.3426", "226.71", "226.71").Lines);
        Assert.Contains(pairs, p => p.label == "Quant Ordered" && p.value == 5m);
        Assert.Contains(pairs, p => p.label == "Net Price" && p.value == 45.3426m);
        Assert.Contains(pairs, p => p.label == "Price Extension" && p.value == 226.71m);
        Assert.Contains(pairs, p => p.label == "Order Total" && p.value == 226.71m);
        Assert.DoesNotContain(pairs, p => p.label == "Customer"); // 0000001 is a code
        Assert.DoesNotContain(pairs, p => p.label == "Order Date"); // a date
        Assert.True(OcrLogic.TryNumber("$1,234.50", out var v) && v == 1234.50m);
        Assert.True(OcrLogic.TryNumber("(12.00)", out var neg) && neg == -12m);
        Assert.False(OcrLogic.TryNumber("0000001", out _));
        Assert.False(OcrLogic.TryNumber("1000000001", out _));
        var inline = OcrLogic.Pairs(new List<OcrLine> { new() { Text = "Net Price: 45.34", X = 10, Y = 10, W = 120, H = 16 } });
        Assert.Contains(inline, p => p.label == "Net Price" && p.value == 45.34m);
    }

    [Fact]
    public void The_extension_formula_is_found_from_three_pictures_when_the_tree_gave_no_values()
    {
        var t = new DateTime(2026, 11, 19, 10, 12, 0);
        var win = "DDI System Inform - Sample Co › Sales Order Entry";
        var input = new LearnInput { AppName = "inform", Now = t.AddDays(1) };
        input.Events.Add(Synthetic.Ev(t, Acts.Value, win, "Product", "100EX"));
        input.OcrStates.Add(Picture(t.AddSeconds(1), "02-after-type-product", "1", "45.3426", "45.34", "48.97"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(5), Acts.Value, win, "Quant Ordered", "5"));
        input.OcrStates.Add(Picture(t.AddSeconds(6), "03-after-type-quant-ordered", "5", "45.3426", "226.71", "244.85"));
        input.Events.Add(Synthetic.Ev(t.AddSeconds(20), Acts.Value, win, "Quant Ordered", "20"));
        input.OcrStates.Add(Picture(t.AddSeconds(21), "04-after-type-quant-ordered", "20", "45.3426", "906.85", "979.40"));
        var r = Learner.Learn(input);
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Price Extension");
        Assert.Equal("Net Price × Quant Ordered", calc.Formula);
        Assert.Equal(3, calc.Fits); Assert.Equal(3, calc.Tested); Assert.True(calc.FromOcr);
        Assert.Contains("screens/shots/sales-order-entry/03-after-type-quant-ordered.png.ocr.json", calc.Evidence.Files);
        Assert.DoesNotContain(r.Logic.Calculations, c => c.Field == "Order Total" && c.Formula == "= Price Extension"); // the total carries tax: not a copy
        var md = MemoryWriter.Render(r);
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 3× of 3, from the pictures' text)", md["logic.md"]);
        Assert.Contains("\"source\": \"ocr\"", md["logic.json"]);
        // learning twice, or in two steps, gives the same memory
        var again = MemoryWriter.Render(Learner.Learn(input));
        Assert.Equal(md["logic.md"], again["logic.md"]);
        Learner.Learn(new LearnInput { AppName = "inform", Now = t.AddDays(1), Events = input.Events.Take(1).ToList(), OcrStates = input.OcrStates.Take(1).ToList() }, new LearnState(), out var state);
        Assert.Equal(md["logic.md"], MemoryWriter.Render(Learner.Learn(input, state, out _))["logic.md"]);
    }

    // ---------- 9. health tells the truth ----------

    [Fact]
    public void Health_says_what_the_collector_could_not_do_instead_of_nothing_is_missing()
    {
        var r = Learner.Learn(Input());
        var facts = PlainReport.FromLearn(r);
        Assert.True(facts.PressesFallbackOnly);
        Assert.True(facts.PromptScreens >= 1, "Close? was visited 42 times with Dialogs 0");
        Assert.Equal(38, facts.DumpFailPct);
        var lines = PlainReport.Lines(facts, withLastLearn: false);
        Assert.DoesNotContain(lines, l => l.Contains("Nothing is missing"));
        Assert.Contains(lines, l => l.Contains("(UIA 0, MSAA 0)"));
        Assert.Contains(lines, l => l.Contains("looks like a message box was recorded as a screen") || l.Contains("look like message boxes were recorded as screens"));
        Assert.Contains(lines, l => l.Contains("38% of screen reads failed"));
        var health = MemoryWriter.Render(r)["health.md"];
        Assert.Contains("38% of screen reads failed", health);
        Assert.Contains("recorded as a screen", health);
        Assert.Contains("First hour done:", PlainReport.TrayFirstHour(facts));
        Assert.DoesNotContain("there is nothing you need to do", PlainReport.TrayFirstHour(facts));
        var problems = Status().Problems();
        Assert.Contains(problems, p => p.Contains("38% of control tree reads failed"));

        var tabs = new PlainFacts { AppName = "x", Screens = 3, Actions = 10, TabsUnrecorded = true };
        Assert.Contains(PlainReport.Lines(tabs, false), l => l.Contains("A tab was selected but never recorded"));
        var fine = new PlainFacts { AppName = "x", Screens = 3, Actions = 10 };
        Assert.Contains(PlainReport.Lines(fine, false), l => l.Contains("Nothing is missing"));
        var status = new CollectorStatus { WindowsSeen = 60, DumpsOk = 100, DumpsFailed = 10, PressesMenu = 5, PressesInferred = 6, TabSelections = 6, TabScreens = 4 };
        Assert.Contains(status.Problems(), p => p.Contains("2 tab selection(s) left no tab screen record"));
    }

    // ---------- 8. backend discovery ----------

    private const string InstalledApplicationConfig = """
        <?xml version="1.0"?>
        <MachineConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Connections>
            <ConnectionInfo>
              <AccountPath>DDI</AccountPath>
              <HostName>192.0.2.10</HostName>
              <WindowsUser>REMOVED</WindowsUser>
              <WindowsPassword>REMOVED</WindowsPassword>
              <ConnectionName>Sample Co</ConnectionName>
              <IsDefault>false</IsDefault>
              <Port xsi:nil="true" />
              <UpgradePath>\\192.0.2.10\UPGRADE\inform_install\setup.exe</UpgradePath>
            </ConnectionInfo>
          </Connections>
        </MachineConfiguration>
        """;

    [Fact]
    public void The_vendor_config_under_ProgramData_names_the_server_and_the_stock_DSNs_are_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "aw-pd-" + Guid.NewGuid().ToString("N"));
        var vendor = Path.Combine(root, "DDISYSTEM", "Inform");
        Directory.CreateDirectory(vendor);
        var install = Path.Combine(root, "Program Files", "DDI System", "Inform");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllText(Path.Combine(vendor, "installedapplication.config"), InstalledApplicationConfig);
            File.WriteAllText(Path.Combine(install, "DDI inForm.exe"), "x");
            File.WriteAllText(Path.Combine(install, "DDI inForm.xml"), "<settings><theme>blue</theme></settings>");
            File.WriteAllText(Path.Combine(install, "DDiSystem.Excel.xml"), "<x/>");
            // the folder is found through the maker's name or the product's, one or two levels down
            Assert.Contains(BackendDetector.VendorFolders(new[] { root }, "DDI inForm", "DDI Inform", "DDI"), d => vendor.StartsWith(d, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(vendor, BackendDetector.VendorFolders(new[] { root }, "inform", null, null));
            var found = BackendDetector.FromFolders(new[] { vendor });
            var b = Assert.Single(found);
            Assert.Equal("192.0.2.10", b.Host); Assert.Equal("DDI", b.Database); Assert.Null(b.Port); Assert.Equal("installedapplication.config", b.Where);
            Assert.DoesNotContain("REMOVED", b.Describe());
            // an open connection of the process to the same host confirms it, whatever port it uses now
            var all = new List<BackendInfo> { b, new() { Kind = "unknown", Host = "192.0.2.10", Port = 60010, Where = "tcp connection" }, new() { Kind = "unknown", Host = "192.0.2.10", Port = 52243, Where = "tcp connection" } };
            var confirmed = BackendDetector.Confirm(all);
            Assert.Single(confirmed);
            Assert.Contains("tcp connection to that host", confirmed[0].Where);
            // the profile says what kind of server it is
            SetupDefaults.ApplyProfileBackendKind(confirmed, ProfileStore.ByName("ddi-inform"));
            Assert.Equal("universe", confirmed[0].Kind);
            Assert.Contains("universe, 192.0.2.10, database DDI", confirmed[0].Describe());
            // ports map to kinds
            Assert.Equal("universe", BackendDetector.KnownPorts[31438]); Assert.Equal("sqlserver", BackendDetector.KnownPorts[1433]);
            Assert.Equal("pervasive", BackendDetector.KnownPorts[1583]); Assert.Equal("oracle", BackendDetector.KnownPorts[1521]);
            foreach (var dsn in new[] { "MS Access Database", "Excel Files", "dBASE Files" }) Assert.Contains(dsn, BackendDetector.StockDsns);
            // the install folder's XML files are the program's own settings, never data sources
            var candidates = DataSourceScanner.Scan(Path.Combine(install, "DDI inForm.exe"), "DDI inForm", "DDI Inform");
            Assert.DoesNotContain(candidates, c => c.Path.EndsWith(".xml"));
            File.WriteAllText(Path.Combine(install, "local.sqlite"), "x");
            File.WriteAllText(Path.Combine(install, "stock.accdb"), "x");
            candidates = DataSourceScanner.Scan(Path.Combine(install, "DDI inForm.exe"), "DDI inForm", "DDI Inform");
            Assert.Contains(candidates, c => c.Path.EndsWith("stock.accdb"));
            Assert.DoesNotContain(candidates, c => c.Path.EndsWith(".xml"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Attributes_and_INI_style_lines_name_a_backend_too()
    {
        var xml = BackendDetector.ParseXmlConfig("<Server HostName=\"db01\" Port=\"1433\" Database=\"Stock\" />", "app.config");
        var b = Assert.Single(xml);
        Assert.Equal("sqlserver", b.Kind); Assert.Equal("db01", b.Host); Assert.Equal(1433, b.Port); Assert.Equal("Stock", b.Database);
        var ini = BackendDetector.ParseText("[server]\nhost=uv01\nport=31438\n", "inform.ini");
        Assert.Contains(ini, x => x.Host == "uv01");
        Assert.Contains(ini, x => x.Port == 31438 && x.Kind == "universe");
    }

    // ---------- the fixtures themselves ----------

    /// <summary>SHA-256 of the real strings (lower case); the strings themselves are kept outside the repository.</summary>
    private static readonly string[] ForbiddenHashes =
    {
        "03b009bc8f59b399d36222d389b036dba3bdf682fb683d9e19e84c0abab12348", "61fff898362d7177526d7b3cb137a53ea619bc98efc28f6327d9693a110446bd",
        "894d0732ac2d85de699ffbeaed0e919268c59d6d3ed3f4280ace267681d6d0ad", "864b24fdae91ef938ba12b344c1652425531133b3194c9114475269e17257c47",
        "64c47e55f48c8db98cb0d82154a31ceeed78c56fe898e6c1d7141a8897b80f0d", "2d820fcfdd4b6ecb9f208c323e889a2c39e49f6bf040105edc253014a1642f59",
        "6f31438ba5e9d3d02ae7c1914b053311db5f417d0cc72d41ce06ec5d91c55d19", "1234c5724f87da4c938b618b4a43cac6c28055eddbf4f4568972b0e7e95e1c2d",
        "9b45101315171aeba9aee6186e015f23408d24851037e3f8c6fa8ede22bb3da7", "6f08f9cfc3770b74b474456543aba50fb6f439db5c2b99f772b646f741ea17c9",
        "5a2a558c78d3717db731600c4f354fa1d9c84b556f108091a891f444f1bdec40", "dffda0f1b7e2841e6309915625eb9646810a5408258dd17cff33abb126430b02",
        "18b7c80b7335a0bf5f2da3676ce45acef892d67637e13ff2065a727d85d127d7", "844c63b9bc1643a92f574bdaef9f9be3926f9ecd98e63801e991de33f8df1fb2",
    };

    [Fact]
    public void Fixtures_hold_none_of_the_real_strings()
    {
        var forbidden = new HashSet<string>(ForbiddenHashes, StringComparer.OrdinalIgnoreCase);
        static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).ToLowerInvariant();
        var files = Directory.GetFiles(Fixtures);
        Assert.True(files.Length >= 9);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var token in Regex.Split(text, @"[^A-Za-z0-9.&]+").Where(t => t.Length >= 3).Select(t => t.Trim('.')).Distinct())
                Assert.False(forbidden.Contains(Hash(token)), $"a real string is in {Path.GetFileName(f)}");
            foreach (Match ip in Regex.Matches(text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"))
                Assert.StartsWith("192.0.2.", ip.Value); // documentation addresses only
            Assert.DoesNotMatch(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", text);
            Assert.DoesNotMatch(@"\(\d{3}\) ?\d{3}-\d{4}|\b\d{3}-\d{3}-\d{4}\b", text);
        }
        Assert.Contains("Writer One", File.ReadAllText(Path.Combine(Fixtures, "ddi-system-inform-sample-co.md")));
    }
}
