using AppWatcher.Core.Events;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// v0.12, the "best of every version" release: the rules added after the v0.11 run on the real program, with synthetic data.
/// Each test names the row of docs/test-v0.11-inform.md it serves.
/// </summary>
public class BestOfTests
{
    // Typed codes: Inform shows the customer's name where its code was typed; the re-read sees the name.
    [Theory]
    [InlineData("0002776", "Some Customer", 1.0, true)]     // the record's name replaced the code: the program's
    [InlineData("00283", "ACME SUPPLIES INC", 3.5, true)]
    [InlineData("100EX", "100EX, LSP FLOOR DRAIN", 0.8, true)] // the code followed by the record's description
    [InlineData("0002", "0002776", 1.0, false)]        // more keys: still typing
    [InlineData("0002776", "0002", 1.0, false)]        // keys taken back
    [InlineData("0002776", "", 1.0, false)]            // cleared by the user
    [InlineData("0002776", "Some Customer", 12.0, false)]  // too long after the keys: a new read, not a replacement
    [InlineData(null, "Some Customer", 1.0, false)]        // no own value event: nothing to compare with
    public void A_reread_that_replaces_the_typed_text_is_the_programs(string? own, string now, double seconds, bool replaced)
    {
        Assert.Equal(replaced, FieldReads.IsReplacement(own, now, seconds));
    }

    // Prompt buttons and plain tab strips: the click raises no event; the 50 ms read of the button hears it. Its point is where
    // the pointer was when the button was first seen down (the up may be read after the pointer moved on).
    [Fact]
    public void A_click_is_placed_where_the_button_was_first_seen_down()
    {
        var w = new FakeWorld();
        w.Trees[Doc] = new List<ControlRow>
        {
            new ControlRow { Depth = 0, Type = "Window", Name = "Vendor", Position = "0,150 1900x900" },
            new ControlRow { Depth = 1, Type = "TabItem", Name = "Main", Position = "100,930 70x24" },
            new ControlRow { Depth = 1, Type = "TabItem", Name = "General", Position = "170,930 80x24" },
        };
        w.Screens[Doc] = "Main › Vendor";
        w.At = (_, _) => Doc;
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 15, 57, 13);
        Assert.Equal(ClickKind.None, t.Observe(new InputSample(t0, 130, 942, true, true, false, Doc), w).Kind);        // down at Main
        var o = t.Observe(new InputSample(t0.AddMilliseconds(100), 200, 940, false, false, false, Doc), w);            // up read after a move onto General
        Assert.Equal(ClickKind.Tab, o.Kind);
        Assert.Equal("Main", o.Hit!.Row.Name);
        Assert.Equal((130, 942), (o.X, o.Y));
        // one sample that saw the button go down and come up again (a very fast click) is one click, at the pointer then
        var s = new ClickSampler();
        Assert.True(s.Sample(buttonDownNow: false, wentDownSinceLast: true));
        Assert.False(s.Sample(buttonDownNow: false, wentDownSinceLast: false));
    }

    // The click on OK of a message box with nothing for five seconds before it, heard at the next pointer move: one press of OK.
    [Fact]
    public void The_message_box_click_with_no_event_before_it_is_one_press_of_OK()
    {
        var w = new FakeWorld();
        w.Trees[Box] = new List<ControlRow>
        {
            new ControlRow { Depth = 0, Type = "Window", Name = "Close?", AutomationId = "", Position = "600,400 300x150" },
            new ControlRow { Depth = 1, Type = "Text", Name = "All changes will be lost. Continue?", AutomationId = "", Position = "620,420 260x40" },
            new ControlRow { Depth = 1, Type = "Button", Name = "OK", AutomationId = "2", Position = "690,500 80x26" },
            new ControlRow { Depth = 1, Type = "Button", Name = "Cancel", AutomationId = "1", Position = "790,500 80x26" },
        };
        w.Screens[Box] = "Close?";
        w.At = (x, y) => x is >= 600 and < 900 && y is >= 400 and < 550 ? Box : 0;
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 12, 24, 18);
        // the box came to the front; nothing at all for 5 s
        Assert.Equal(ClickKind.None, t.Observe(new InputSample(t0, 700, 450, false, false, false, Box), w).Kind);
        // the click (down and up) at OK, heard by the 50 ms read: down, then up
        t.Observe(new InputSample(t0.AddSeconds(5), 730, 513, true, true, false, Box), w);
        var o = t.Observe(new InputSample(t0.AddSeconds(5.05), 731, 513, false, false, false, Box), w);
        Assert.Equal(ClickKind.Target, o.Kind);
        Assert.Equal("OK", o.Hit!.Row.Name);
        // the box goes away: the press is confirmed once
        // the box closes while the live read of the point is in flight: the confirmation waits for it, then counts OK once
        Assert.Null(t.Confirm(t0.AddSeconds(5.1), "window closed", "Close?", out var deferred));
        Assert.True(deferred);
        Assert.Equal("OK", t.LiveRead(o.ClickedAt, null).Confirmed?.Name);
        Assert.Null(t.Confirm(t0.AddSeconds(5.2), "window changed", "Close?", out _));
    }

    // Add Line Item: nothing changes after it (Inform added the empty line already); the tree and the live read agreeing is the press.
    [Fact]
    public void A_button_the_tree_and_the_live_read_agree_on_is_pressed_at_once()
    {
        var w = new FakeWorld();
        w.Trees[Doc] = new List<ControlRow>
        {
            new ControlRow { Depth = 0, Type = "Window", Name = "New Sales Order", Position = "0,150 1900x900" },
            new ControlRow { Depth = 1, Type = "Button", Name = "Add Line Item", AutomationId = "btnAddLine", Position = "150,690 80x22" },
            new ControlRow { Depth = 1, Type = "Button", Name = "Exit", AutomationId = "cmdExit", Position = "1830,970 60x26" },
        };
        w.Screens[Doc] = "Main › New Sales Order";
        w.At = (_, _) => Doc;
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 12, 23, 8);
        t.Observe(new InputSample(t0, 188, 699, true, true, false, Doc), w);
        var o = t.Observe(new InputSample(t0.AddMilliseconds(80), 188, 699, false, false, false, Doc), w);
        Assert.True(o.NeedsLiveRead);
        var r = t.LiveRead(o.ClickedAt, new LiveElement("Add Line Item", "btnAddLine", "Button", "150,690 80x22", "Main › New Sales Order", true));
        Assert.Equal("Add Line Item", r.Confirmed?.Name);
        // a live element that is another control where the tree has the button: no press
        var t2 = new ClickTracker();
        t2.Observe(new InputSample(t0, 188, 699, true, true, false, Doc), w);
        var o2 = t2.Observe(new InputSample(t0.AddMilliseconds(80), 188, 699, false, false, false, Doc), w);
        Assert.Null(t2.LiveRead(o2.ClickedAt, new LiveElement("Line Items", "grid", "DataGrid", "0,300 1900x400", "Main › New Sales Order", true)).Confirmed);
    }

    // Lookups: 1 right + 2 false in v0.11 (a master search showing only its header; "12" typed with the product list still up).
    [Fact]
    public void A_lookup_has_a_row_with_what_was_typed()
    {
        var header = "Record Id | Description | PLine";
        Assert.True(FieldReads.LookupMatchesTyped("100", new[] { header, "100EX | FLOOR DRAIN | 12", "100A | DRAIN | 12" }));
        Assert.False(FieldReads.LookupMatchesTyped("0002776", new[] { "Record Id | Customer Name | City", "Record Id | Customer Name | City" }));
        // an Infragistics grid exposes only bands of column names: still a list to choose from
        Assert.True(FieldReads.LookupMatchesTyped("100", new[] { header, "Cart | Record Id | Description | PLine" }));
        // the editor still holds the previous line's code ("100EX") while "100" is typed on the next line: the rows start alike
        Assert.True(FieldReads.LookupMatchesTyped("100EX", new[] { header, "1000136890 | DRAIN BODY | 02" }));
        // what was typed is unknown (a silent grid editor): a list with rows is the lookup, a header alone is not
        Assert.True(FieldReads.LookupMatchesTyped("", new[] { header, "100EX | FLOOR DRAIN | 02" }));
        Assert.False(FieldReads.LookupMatchesTyped("", new[] { header, header }));
    }

    // Program fills recorded as typed (r1: 6): the Attention field filled when the Header page showed, combos set when a record loads.
    [Fact]
    public void A_changed_field_is_the_users_only_after_a_key_or_a_click_on_it()
    {
        var t0 = new DateTime(2026, 11, 20, 14, 52, 50);
        var none = (0, 0, DateTime.MinValue);
        // a key went down after the focus arrived: typed
        Assert.True(FieldReads.UserChanged("Edit", "10,10 100x20", t0.AddSeconds(2), typingAt: t0.AddSeconds(1), focusArrived: t0, none));
        // the focus arrived (the program moved it) after the last key, and the field changed: the program's
        Assert.False(FieldReads.UserChanged("Edit", "10,10 100x20", t0.AddSeconds(4), typingAt: t0, focusArrived: t0.AddSeconds(3), none));
        // never a key: the program's
        Assert.False(FieldReads.UserChanged("Edit", "10,10 100x20", t0, DateTime.MinValue, DateTime.MinValue, none));
        // a combo the program set; then one the user clicked open
        Assert.False(FieldReads.UserChanged("ComboBox", "10,10 100x20", t0.AddSeconds(1), DateTime.MinValue, DateTime.MinValue, (500, 500, t0)));
        Assert.True(FieldReads.UserChanged("ComboBox", "10,10 100x20", t0.AddSeconds(1), DateTime.MinValue, DateTime.MinValue, (50, 20, t0)));
    }

    // The vendor's name written where "00283" was typed, read in the same batch as the record's other fields (r8).
    [Fact]
    public void A_record_loads_name_longer_than_the_keys_is_the_programs()
    {
        Assert.True(FieldReads.LongerThanKeys("EVERY SUPPLY COMPANY INC", keysSeen: 4));   // 0, 2, 8, 3
        Assert.False(FieldReads.LongerThanKeys("0002776", keysSeen: 4));                   // 0, 2, 7, 6
        Assert.False(FieldReads.LongerThanKeys("100EX", keysSeen: 4));
    }

    // Duplicates (r9): the grid editor's "2" while the cell read already shows "20", and an editor copy 1.5 s before the cell read.
    [Fact]
    public void A_grid_editors_copy_is_dropped_when_the_cell_read_shows_it_or_more()
    {
        var d = new ValueDebouncer();
        var t0 = new DateTime(2026, 11, 20, 18, 52, 57);
        var cell = Synthetic.Ev(t0.AddMilliseconds(1544), Acts.Value, "Main › New Sales Order", "Quant Ordered", "20");
        cell.Ct = "DataItem";
        var editor = Synthetic.Ev(t0, Acts.Value, "Main › New Sales Order", "Line Items", "2");
        editor.OverGridCell = true;
        d.Push(editor, t0);
        d.Push(cell, t0.AddMilliseconds(1544));
        var all = d.Flush(t0.AddSeconds(10), all: true);
        Assert.Equal("20", Assert.Single(all).Val);
    }

    // Privacy (r14): the notes box was labelled with the vendor's name shown above it, which is the quoted part of its window title.
    [Fact]
    public void A_label_that_is_the_records_name_from_the_title_is_data()
    {
        var title = "Vendor - 00283 \"SOME SUPPLIER INC\" Internal Notes";
        Assert.True(AppWatcher.Core.Learning.Titles.IsQuotedInTitle("SOME SUPPLIER INC", title));
        Assert.True(AppWatcher.Core.Learning.Titles.IsQuotedInTitle("SUPPLIER", title));
        Assert.False(AppWatcher.Core.Learning.Titles.IsQuotedInTitle("Notes", title));
        Assert.False(AppWatcher.Core.Learning.Titles.IsQuotedInTitle("Ship To", "Main › New Sales Order"));
    }

    // Lookups (r13–r15): the list window closed before its tree could be read; its shape and place decide.
    [Fact]
    public void A_list_window_under_the_field_shown_right_after_a_key_is_a_lookup_by_its_shape()
    {
        var field = "250,420 110x22";
        Assert.True(FieldReads.LookupByShape(field, "250,444 600x300", secondsSinceKey: 0.4));
        Assert.False(FieldReads.LookupByShape(field, "250,444 600x300", secondsSinceKey: 5));   // no key just before
        Assert.False(FieldReads.LookupByShape(field, "250,444 90x20", secondsSinceKey: 0.4));    // tip-sized
        Assert.False(FieldReads.LookupByShape(field, "1400,900 600x300", secondsSinceKey: 0.4)); // nowhere near the field
    }

    // Strays: "100" typed and "1000136890" read in the cell are two values (the cell is not a late keystroke of the editor);
    // "000277" and "0002776" are one; a value is held while more keys went down than it has characters.
    [Fact]
    public void The_editor_and_cell_merge_only_for_a_late_keystroke_and_a_value_waits_for_its_keys()
    {
        var t0 = new DateTime(2026, 11, 20, 21, 40, 54);
        var d = new ValueDebouncer();
        var editor = Synthetic.Ev(t0, Acts.Value, "Main › New Sales Order", "Line Items", "100"); editor.OverGridCell = true;
        var cell = Synthetic.Ev(t0.AddMilliseconds(800), Acts.Value, "Main › New Sales Order", "Product Number", "1000136890"); cell.Ct = "DataItem";
        d.Push(editor, t0); d.Push(cell, t0.AddMilliseconds(800));
        Assert.Equal(2, d.Flush(t0.AddSeconds(10), all: true).Count);

        var d2 = new ValueDebouncer();
        var partial = Synthetic.Ev(t0, Acts.Value, "Main", "Customer", "00027"); partial.KeysSeen = 7;
        d2.Push(partial, t0);
        Assert.Empty(d2.Flush(t0.AddMilliseconds(1200)));                       // 7 keys, 5 characters: the rest is on its way
        var whole = Synthetic.Ev(t0.AddSeconds(2.2), Acts.Value, "Main", "Customer", "0002776"); whole.KeysSeen = 7;
        d2.Push(whole, t0.AddSeconds(2.2));
        Assert.Equal("0002776", Assert.Single(d2.Flush(t0.AddSeconds(4))).Val);
    }

    // Review findings: a short quoted record name never renames a real label; a greyed-out button is never pressed.
    [Fact]
    public void Short_record_names_rename_nothing_and_a_disabled_button_is_not_pressed()
    {
        Assert.False(AppWatcher.Core.Learning.Titles.NameIsRecordName("Net Price", "NET"));
        Assert.False(AppWatcher.Core.Learning.Titles.NameIsRecordName("Product", "PRO"));
        Assert.True(AppWatcher.Core.Learning.Titles.NameIsRecordName("ACME SUPPLIES INC", "ACME SUPPLIES INC"));
        Assert.True(AppWatcher.Core.Learning.Titles.NameIsRecordName("Notes for ACME SUPPLIES", "ACME SUPPLIES"));

        var w = new FakeWorld();
        w.Trees[Doc] = new List<ControlRow> { new ControlRow { Depth = 0, Type = "Window", Name = "Order", Position = "0,0 800x600" }, new ControlRow { Depth = 1, Type = "Button", Name = "Save", Position = "10,10 60x24" } };
        w.Screens[Doc] = "Main › Order"; w.At = (_, _) => Doc;
        var t = new ClickTracker(); var t0 = new DateTime(2026, 11, 23, 9, 0, 0);
        t.Observe(new InputSample(t0, 30, 20, true, true, false, Doc), w);
        var o = t.Observe(new InputSample(t0.AddMilliseconds(90), 30, 20, false, false, false, Doc), w);
        Assert.Null(t.LiveRead(o.ClickedAt, new LiveElement("Save", "cmdSave", "Button", "10,10 60x24", "Main › Order", true, Enabled: false)).Confirmed);
    }

    // Duplicates (r22): a nameless editor's copy of the value a named cell of the same window reports is dropped.
    [Fact]
    public void A_nameless_editors_copy_of_a_named_cells_value_is_dropped()
    {
        var t0 = new DateTime(2026, 11, 23, 11, 32, 15);
        var d = new ValueDebouncer();
        var editor = Synthetic.Ev(t0.AddMilliseconds(10), Acts.Value, "Main › New Sales Order", "", "1000136890");
        var cell = Synthetic.Ev(t0, Acts.Value, "Main › New Sales Order", "Product Number", "1000136890"); cell.Ct = "DataItem";
        d.Push(cell, t0); d.Push(editor, t0.AddMilliseconds(10));
        Assert.Equal("Product Number", Assert.Single(d.Flush(t0.AddSeconds(10), all: true)).Name);
    }

    // Second review: a typed number the grid shows formatted is still the user's; only the box over this cell counts.
    [Fact]
    public void A_typed_number_shown_formatted_is_still_typed_and_only_the_box_over_the_cell_counts()
    {
        Assert.True(FieldReads.ShowsTyped("5.00", "5"));
        Assert.True(FieldReads.ShowsTyped("$5.00", "5"));
        Assert.True(FieldReads.ShowsTyped("100EX", "100EX"));
        Assert.True(FieldReads.ShowsTyped("1000136890", "100"));
        Assert.False(FieldReads.ShowsTyped("1", "100EX"));
        Assert.True(FieldReads.Overlaps("600,400 80x22", "598,398 90x26"));
        Assert.False(FieldReads.Overlaps("600,400 80x22", "300,400 200x26"));
    }

    // r27: the session's first document had no tree of its own; its Exit is found in the main window's read of a moment ago.
    [Fact]
    public void A_document_with_no_tree_of_its_own_is_hit_tested_on_the_main_windows_fresh_read()
    {
        const long main = 1, doc = 7;
        var w = new FakeWorld();
        w.At = (_, _) => doc; w.Doc = h => h == doc ? doc : 0;
        w.Screens[doc] = "Main › Customer Master";
        w.Fresh[main] = new List<ControlRow> { new ControlRow { Depth = 0, Type = "Window", Name = "Main", Position = "0,0 1920x1040" }, new ControlRow { Depth = 3, Type = "Button", Name = "Exit", AutomationId = "cmdExit", Position = "1830,970 64x26" } };
        var rootOf = new Dictionary<long, long> { [doc] = main };
        var hit = ClickSurfaces.Resolve(new RootWorld(w, rootOf), 1862, 983, 0, doc, 0);
        Assert.Equal("Exit", hit?.Row.Name);
    }

    private sealed class RootWorld : IWindowWorld
    {
        private readonly FakeWorld _w; private readonly Dictionary<long, long> _root;
        public RootWorld(FakeWorld w, Dictionary<long, long> root) { _w = w; _root = root; }
        public IReadOnlyList<ControlRow>? RowsOf(long hwnd) => _w.RowsOf(hwnd);
        public bool IsVisible(long hwnd) => true;
        public long WindowAt(int x, int y) => _w.WindowAt(x, y);
        public long RootOf(long hwnd) => _root.GetValueOrDefault(hwnd, hwnd);
        public long DocumentOf(long hwnd) => ((IWindowWorld)_w).DocumentOf(hwnd);
        public string ScreenOf(long hwnd) => _w.ScreenOf(hwnd);
        public bool MenuPopupAt(int x, int y, long goingAway) => false;
        public IReadOnlyList<ControlRow>? FreshRowsOf(long hwnd) => _w.FreshRowsOf(hwnd);
    }

    private const long Box = 4, Doc = 2;

    private sealed class FakeWorld : IWindowWorld
    {
        public readonly Dictionary<long, IReadOnlyList<ControlRow>> Trees = new();
        public readonly Dictionary<long, string> Screens = new();
        public Func<int, int, long> At = (_, _) => 0;
        public IReadOnlyList<ControlRow>? RowsOf(long hwnd) => Trees.GetValueOrDefault(hwnd);
        public bool IsVisible(long hwnd) => Trees.ContainsKey(hwnd);
        public long WindowAt(int x, int y) => At(x, y);
        public long RootOf(long hwnd) => hwnd;
        public string ScreenOf(long hwnd) => Screens.GetValueOrDefault(hwnd, "");
        public bool MenuPopupAt(int x, int y, long goingAway) => false;
        public readonly Dictionary<long, IReadOnlyList<ControlRow>> Fresh = new();
        public Func<long, long> Doc = _ => 0;
        public IReadOnlyList<ControlRow>? FreshRowsOf(long hwnd) => Fresh.GetValueOrDefault(hwnd);
        long IWindowWorld.DocumentOf(long hwnd) => Doc(hwnd);
    }
}
