using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Dialogs drawn inside a web page (synthetic trees shaped like Chromium's).</summary>
public class InPageDialogTests
{
    private static ControlRow R(int depth, string type, string name = "") => new() { Depth = depth, Type = type, Name = name, Position = "0,0 10x10" };

    [Fact]
    public void A_message_inside_a_page_is_a_dialog_and_a_named_form_is_a_screen()
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "Sample Web ERP"),
            R(1, "Pane"),
            R(2, "Document", "Sample Web ERP"),
            R(3, "Edit", "Customer code"),
            R(3, "Window", "Customer code must have at least 3 characters."),
            R(4, "Text", "Customer code must have at least 3 characters."),
            R(4, "Button", "OK"),
            R(3, "Window", "New Customer"),
            R(4, "Text", "New Customer"),
            R(4, "Edit", "Code"),
            R(4, "Button", "OK"),
            R(4, "Button", "Cancel"),
        };
        var found = DialogDetector.InPage(rows);
        Assert.Equal(2, found.Count);
        Assert.True(found[0].message);
        Assert.Equal("Customer code must have at least 3 characters.", found[0].body);
        Assert.Equal("New Customer", found[1].title);
        Assert.False(found[1].message);
    }

    [Fact]
    public void A_window_outside_a_page_or_without_buttons_is_not_one()
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "Main"),
            R(1, "Window", "Document 1"),   // an MDI document: not inside a web page
            R(2, "Text", "Saved."),
            R(2, "Button", "OK"),
            R(1, "Document", "Page"),
            R(2, "Window", "Banner"),        // no button
            R(3, "Text", "Welcome"),
        };
        Assert.Empty(DialogDetector.InPage(rows));
    }

    [Fact]
    public void A_WPF_tab_page_is_read_from_its_tab_item_never_from_the_menu()
    {
        var rows = new List<ControlRow>
        {
            R(0, "Window", "Sample WPF ERP"),
            R(1, "Menu"), R(2, "MenuItem", "Customer Master"), R(2, "MenuItem", "Orders"), R(2, "MenuItem", "Quote"), R(2, "MenuItem", "Help"),
            R(1, "Tab"),
            R(2, "TabItem", "Customer Master"), R(3, "Text", "Customer Master"),
            R(2, "TabItem", "Orders"), R(3, "Text", "Orders"), R(3, "DataGrid", "Order lines"), R(4, "HeaderItem", "Quantity"), R(4, "DataItem", "Line"), R(5, "Edit", ""),
            R(2, "TabItem", "Quote"), R(3, "Text", "Quote"),
        };
        var doc = TabDocuments.Document(rows, "Orders");
        Assert.Equal("TabItem", doc[0].Type);
        Assert.Contains(doc, r => r.Type == "DataGrid");
        Assert.DoesNotContain(doc, r => r.Type == "MenuItem");
    }
}
