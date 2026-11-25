using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Which window the user works in after the one in front went away (synthetic window facts).</summary>
public class FrontWindowTests
{
    private const long Main = 100, Search = 200, List = 300, Menu = 400, Other = 900;
    private static WindowFacts W(long h, string title, bool visible = true, bool program = true, bool menu = false, bool excluded = false) =>
        new(h, title, visible, program, menu, excluded);

    [Fact]
    public void An_untitled_lookup_list_in_front_hands_over_to_the_main_window_never_to_itself()
    {
        // Inform r39: the search form was hidden, the lookup list under Product Number took the front
        var pick = FrontWindow.Pick(new[] { W(List, ""), W(Main, "Main") }, current: Search);
        Assert.Equal(Main, pick?.Hwnd);
    }

    [Fact]
    public void A_list_with_no_owner_changes_nothing()
    {
        Assert.Null(FrontWindow.Pick(new[] { W(List, "") }, current: Main));
    }

    [Fact]
    public void A_hidden_form_still_reported_in_front_changes_nothing_until_windows_moves_on()
    {
        Assert.Null(FrontWindow.Pick(new[] { W(Search, "Customer search", visible: false) }, current: Search));
    }

    [Fact]
    public void A_titled_window_of_the_program_in_front_is_taken()
    {
        Assert.Equal(Main, FrontWindow.Pick(new[] { W(Main, "Main") }, current: Search)?.Hwnd);
    }

    [Fact]
    public void The_window_already_in_front_changes_nothing()
    {
        Assert.Null(FrontWindow.Pick(new[] { W(Main, "Main") }, current: Main));
    }

    [Fact]
    public void A_menu_popup_hands_over_to_its_owner()
    {
        Assert.Equal(Main, FrontWindow.Pick(new[] { W(Menu, "Sales - 0", menu: true), W(Main, "Main") }, current: Search)?.Hwnd);
    }

    [Fact]
    public void Another_program_in_front_or_an_excluded_window_is_never_taken()
    {
        Assert.Null(FrontWindow.Pick(new[] { W(Other, "Notepad", program: false) }, current: Search));
        Assert.Null(FrontWindow.Pick(new[] { W(Main, "Payroll", excluded: true) }, current: Search));
    }
}
