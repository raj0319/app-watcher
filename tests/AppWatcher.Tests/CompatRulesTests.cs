using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Rules found by running the watcher on other programs (Excel, PowerShell ISE, Character Map, a Qt program): synthetic data.</summary>
public class CompatRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 25, 10, 0, 0);
    private static DateTime At(double s) => T0.AddSeconds(s);

    [Theory]
    [InlineData("Settings Alt+S", "Settings")]
    [InlineData("OK Enter", "OK")]
    [InlineData("Expand all\tCtrl + Num+", "Expand all")]
    [InlineData("Refresh F5", "Refresh")]
    [InlineData("Save Ctrl+S", "Save")]
    [InlineData("Confirm Delete", "Confirm Delete")]
    [InlineData("Customer Master", "Customer Master")]
    [InlineData("Enter", "Enter")]
    public void A_shortcut_in_a_name_is_dropped_never_the_words(string name, string expected) =>
        Assert.Equal(expected, Labels.WithoutShortcut(name));

    [Fact]
    public void The_item_clicked_inside_the_menu_wins_over_the_menu_bar_item_highlighted_before()
    {
        // PowerShell ISE: Tools was focused, Options... clicked; its focus event arrived after the menu closed
        Assert.Equal("Options...", MenuChoice.Pick(("Options...", At(1.9)), ("Tools", At(0.2)), openedAt: At(0.3), closedAt: At(2.0), escaped: false));
        // a Qt program: no focus events for items at all
        Assert.Equal("Options", MenuChoice.Pick(("Options Ctrl+O", At(1.9)), ("", DateTime.MinValue), At(0.3), At(2.0), false));
    }

    [Fact]
    public void A_late_focus_event_still_names_the_choice_and_the_menu_bar_item_alone_never_does()
    {
        Assert.Equal("Undo", MenuChoice.Pick(("", DateTime.MinValue), ("Undo", At(2.3)), At(0.3), At(2.0), false));
        Assert.Null(MenuChoice.Pick(("", DateTime.MinValue), ("Tools", At(0.2)), At(0.3), At(2.0), false));
        Assert.Null(MenuChoice.Pick(("Options", At(1.9)), ("Options", At(1.9)), At(0.3), At(2.0), escaped: true));
    }

    [Fact]
    public void The_same_value_filled_again_within_ten_seconds_is_no_new_fill()
    {
        Assert.True(FieldReads.RepeatFill("Quantity", At(0), "Quantity", At(3)));
        Assert.False(FieldReads.RepeatFill("100", At(0), "120", At(3)));          // recalculated: a new fill
        Assert.False(FieldReads.RepeatFill("Quantity", At(0), "Quantity", At(20))); // long after: counted again
        Assert.False(FieldReads.RepeatFill(null, DateTime.MinValue, "x", At(1)));
    }

    [Fact]
    public void A_change_after_a_click_on_a_button_is_the_programs_not_the_keys()
    {
        // typed "Order 1042" (its own event recorded it at 1.0 s), clicked Select at 2.0 s outside the box, the box now ends in "!"
        var box = "100,100 200x20";
        Assert.True(FieldReads.ChangedAfterClick("Order 1042", At(1.0), At(0.9), (400, 300, At(2.0)), box, "Order 1042!", At(2.3)));
        // the click was in the box itself: the user's
        Assert.False(FieldReads.ChangedAfterClick("Order 1042", At(1.0), At(0.9), (150, 110, At(2.0)), box, "Order 1042!", At(2.3)));
        // a key went down after the click: the keys'
        Assert.False(FieldReads.ChangedAfterClick("Order 1042", At(1.0), At(2.1), (400, 300, At(2.0)), box, "Order 1042!", At(2.3)));
        // the keys' value was never reported on its own: nothing to compare (a web page reports on leaving)
        Assert.False(FieldReads.ChangedAfterClick(null, DateTime.MinValue, At(0.9), (400, 300, At(2.0)), box, "Order 1042", At(2.3)));
    }

    [Fact]
    public void A_button_renamed_by_its_press_and_told_by_two_paths_is_one_press()
    {
        // Character Map: Search clicked at 0.0, MSAA told "Search" at 0.1, UI Automation told "Reset" at 0.3
        Assert.True(PressDecider.SameClick(At(0.1), "msaa", "uia", lastClickAt: At(0.0), lastKeyAt: DateTime.MinValue, now: At(0.3)));
        // the click heard a moment after the press it made: still one click
        Assert.True(PressDecider.SameClick(At(0.1), "msaa", "uia", At(0.2), DateTime.MinValue, At(0.3)));
        // a second click came in between: two presses
        Assert.False(PressDecider.SameClick(At(0.1), "msaa", "uia", At(0.6), DateTime.MinValue, At(0.7)));
        // Enter went down in between: two presses
        Assert.False(PressDecider.SameClick(At(0.1), "uia", "uia", At(0.0), At(0.2), At(0.3)));
        // a menu choice or an inferred press is decided elsewhere
        Assert.False(PressDecider.SameClick(At(0.1), "menu", "uia", At(0.0), DateTime.MinValue, At(0.3)));
        // over a second later: another press
        Assert.False(PressDecider.SameClick(At(0.1), "uia", "uia", At(0.0), DateTime.MinValue, At(1.3)));
    }

    [Fact]
    public void A_spreadsheet_cell_name_gives_its_row_and_column()
    {
        Assert.Equal((4, 2), FieldReads.CellAddress("C5"));
        Assert.Equal((0, 26), FieldReads.CellAddress("AA1"));
        Assert.Null(FieldReads.CellAddress("Quantity"));
        Assert.Null(FieldReads.CellAddress("A0"));
    }

    [Fact]
    public void A_cell_the_user_went_into_and_typed_in_reports_a_typed_value()
    {
        // Excel: the focus came into B2 at 1.0 s, keys at 1.2 s, the value after Tab moved on at 1.6 s
        Assert.True(FieldReads.CommittedCell(At(1.0), At(1.2), At(1.6)));
        // keys only long before the visit: a recalculation, not typing
        Assert.False(FieldReads.CommittedCell(At(10.0), At(1.2), At(10.5)));
        // never visited: a formula's result
        Assert.False(FieldReads.CommittedCell(DateTime.MinValue, At(1.2), At(1.6)));
    }

    [Theory]
    [InlineData("Detail Section, Code", "Code")]
    [InlineData("Form Header Section, Search", "Search")]
    [InlineData("Customer Code collapsed", "Customer Code")]
    [InlineData("Customer Name", "Customer Name")]
    public void Access_section_and_state_words_are_not_part_of_a_field_name(string name, string expected) =>
        Assert.Equal(expected, Labels.WithoutSection(name));

    [Fact]
    public void A_ribbons_tabs_are_toolbars_never_documents()
    {
        Assert.True(TabDocuments.IsRibbonPart("Ribbon Tabs", ""));
        Assert.True(TabDocuments.IsRibbonPart("", "NetUIRibbonTab"));
        Assert.False(TabDocuments.IsRibbonPart("Documents", "TabControl"));
    }
}
