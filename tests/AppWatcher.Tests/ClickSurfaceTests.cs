using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The two collector gaps the first scenario scorecard found on the sample app (2026-11-20): a press on the MDI container's
/// own button (Close workspace) went unrecorded because only the document's tree was hit-tested, and a click on a menu
/// drop-down item was credited to the toolbar button lying under the popup, then counted again when the item's window opened.
/// </summary>
public class ClickSurfaceTests
{
    private static ControlRow Row(int depth, string type, string name, string aid, string pos) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos };

    [Fact]
    public void A_click_is_resolved_on_the_destroyed_window_then_the_front_then_the_previous_front_each_with_its_mdi_container()
    {
        const long workspace = 10, document = 11, main = 20;
        long ContainerOf(long h) => h == document ? workspace : 0;
        // the Close workspace click: the foreground switched to the main window before the collector heard of the click, so the
        // document that was in front (and its container, which owns the button) must be tried, at the click's own point
        var surfaces = TreeHitTest.Surfaces(destroyed: 0, front: main, prevFront: document, x: 2988, y: 869, prevX: 3100, prevY: 1230, moved: true, ContainerOf);
        Assert.Equal(new[] { (main, 2988, 869), (document, 2988, 869), (workspace, 2988, 869), (document, 3100, 1230), (workspace, 3100, 1230) }, surfaces.ToArray());
        // the destroy of a document: its tree first, then its container, then the window in front
        surfaces = TreeHitTest.Surfaces(destroyed: document, front: document, prevFront: document, x: 1, y: 1, prevX: 1, prevY: 1, moved: false, ContainerOf);
        Assert.Equal(new[] { (document, 1, 1), (workspace, 1, 1) }, surfaces.ToArray());
        // and with the real rows of the sample app's workspace, the container's button is the hit
        var workspaceRows = new List<ControlRow>
        {
            Row(0, "Window", "Sample ERP - Workspace", "", "2660,830 738x507"),
            Row(1, "Button", "Close workspace", "btnCloseWorkspace", "2669,868 720x28"),
            Row(1, "Pane", "", "1376770", "2669,896 720x432"),
            Row(2, "Window", "Price Change", "PriceChangeView", "2703,930 538x367"),
            Row(3, "Button", "Exit", "btnDocExit", "3100,1216 88x28"),
        };
        var documentRows = workspaceRows.Where(r => r.Depth >= 2).ToList();
        Assert.Null(TreeHitTest.ButtonAt(documentRows, 2988, 869));
        Assert.Equal("Close workspace", TreeHitTest.ButtonAt(workspaceRows, 2988, 869)!.Name);
    }

    [Fact]
    public void A_click_over_an_open_menu_popup_has_no_target_in_the_window_beneath_and_the_window_it_opens_is_not_credited_again()
    {
        var d = new PressDecider();
        var t = new DateTime(2026, 11, 20, 19, 32, 12);
        // the toolbar button the recorded tree finds under the popup
        d.Clicked(new PressCandidate("Workspace", "btnWorkspace", "Button", "Sample ERP"), 3000, 1035, t, overPopup: true);
        Assert.Null(d.Pending);
        Assert.Null(d.Confirm(t.AddMilliseconds(600)));
        // the menu item is counted by the menu path when the popup closes; the About window that opens 26 ms later is its doing
        d.Pressed("About", t.AddMilliseconds(600));
        Assert.True(d.PressedWithin(1000, t.AddMilliseconds(626)));
        Assert.False(d.PressedWithin(1000, t.AddMilliseconds(1700)));
        // a plain click keeps working
        d.Clicked(new PressCandidate("Refresh", "btnRefresh", "Button", "Sample ERP"), 2900, 1035, t.AddSeconds(5));
        Assert.Equal("Refresh", d.Confirm(t.AddSeconds(5).AddMilliseconds(300))!.Name);
    }
}
