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
        public long DocumentOf(long hwnd) => 0;
        public string ScreenOf(long hwnd) => Screens.GetValueOrDefault(hwnd, "");
        public bool MenuPopupAt(int x, int y, long goingAway) => false;
    }
}
