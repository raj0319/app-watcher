using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The surface a click is resolved on (<see cref="ClickSurfaces"/>) and the menu rule, on the sample app's workspace: the MDI
/// container's own button (Close workspace), a click on a menu item lying over a toolbar button, and the window that opens
/// right after a counted press. The real run's cases are in InformV11Tests.
/// </summary>
public class ClickSurfaceTests
{
    internal static ControlRow Row(int depth, string type, string name, string aid, string pos) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos };

    [Fact]
    public void A_click_on_the_workspace_chrome_is_resolved_on_the_workspace_and_a_click_in_a_document_on_the_document()
    {
        const long workspace = 10, mdiClient = 11, document = 12, docButton = 13;
        var w = new FakeWorld();
        w.Rows[workspace] = new()
        {
            Row(0, "Window", "Sample ERP - Workspace", "", "2660,830 738x507"),
            Row(1, "Button", "Close workspace", "btnCloseWorkspace", "2669,868 720x28"),
            Row(1, "Pane", "", "1376770", "2669,896 720x432"),
            Row(2, "Window", "Price Change", "PriceChangeView", "2703,930 538x367"),
            Row(3, "Button", "Exit", "btnDocExit", "3100,1216 88x28"),   // a stale copy of the document inside the workspace's tree
        };
        w.Rows[document] = new() { Row(0, "Window", "Price Change", "PriceChangeView", "2703,930 538x367"), Row(1, "Button", "Exit", "btnDocExit", "3100,1216 88x28") };
        w.Root[mdiClient] = workspace; w.Root[document] = workspace; w.Root[docButton] = workspace; w.Root[workspace] = workspace;
        w.Doc[document] = document; w.Doc[docButton] = document;
        w.Screen[workspace] = "Sample ERP - Workspace"; w.Screen[document] = "Sample ERP - Workspace › Price Change";
        w.At = (x, y) => y < 896 ? workspace : x >= 3100 && y >= 1216 ? docButton : mdiClient;
        var hit = ClickSurfaces.Resolve(w, 2988, 869, goingAway: 0, front: document, prevFront: document);
        Assert.Equal("Close workspace", hit!.Row.Name);
        Assert.Equal("Sample ERP - Workspace", hit.Screen);
        hit = ClickSurfaces.Resolve(w, 3140, 1230, 0, document, document);
        Assert.Equal(document, hit!.Hwnd);
        Assert.Equal("Sample ERP - Workspace › Price Change", hit.Screen);
        // a document with no tree of its own: the workspace's stale copy is never used for a point inside it
        w.Rows.Remove(document);
        Assert.Null(ClickSurfaces.Resolve(w, 3140, 1230, 0, document, document));
    }

    [Fact]
    public void A_click_over_an_open_menu_belongs_to_the_menu_and_the_window_it_opens_is_not_credited_again()
    {
        var w = new FakeWorld();
        const long main = 20;
        w.Rows[main] = new() { Row(0, "Window", "Sample ERP", "", "2800,1000 600x400"), Row(1, "Button", "Workspace", "btnWorkspace", "2959,1038 90x26") };
        w.Root[main] = main; w.Screen[main] = "Sample ERP"; w.At = (_, _) => main;
        var t = new ClickTracker();
        var t0 = new DateTime(2026, 11, 20, 19, 32, 12);
        t.MenuOpened();
        Assert.Equal(ClickKind.None, t.Observe(new InputSample(t0, 3000, 1035, ButtonDown: true, WentDown: true, KeyWentDown: false, Front: main), w).Kind);
        var o = t.Observe(new InputSample(t0.AddMilliseconds(80), 3000, 1035, false, false, false, main), w);
        Assert.Equal(ClickKind.Menu, o.Kind);
        Assert.Null(t.Decider.Pending);
        t.MenuClosed(t0.AddMilliseconds(600));
        t.Pressed("About", "menu", t0.AddMilliseconds(600));
        Assert.True(t.PressedWithin(1000, t0.AddMilliseconds(626)));
        Assert.False(t.MayCreditPointer(t0.AddMilliseconds(626)));
        // a plain click a few seconds later works as before
        t.Observe(new InputSample(t0.AddSeconds(5), 3000, 1048, true, true, false, main), w);
        var hit = t.Observe(new InputSample(t0.AddSeconds(5).AddMilliseconds(90), 3000, 1048, false, false, false, main), w);
        Assert.Equal("Workspace", hit.Hit!.Row.Name);
        // the window it opened came while the live read was in flight: the confirmation waits for the read, then counts
        Assert.Null(t.Confirm(t0.AddSeconds(5).AddMilliseconds(300), "window opened", "Sample ERP", out var deferred));
        Assert.True(deferred);
        Assert.Equal("Workspace", t.LiveRead(hit.ClickedAt, null).Confirmed!.Name);
    }
}

/// <summary>A recorded run's windows for the click tracker: trees per handle, which handle is under a point, which are shown.</summary>
internal sealed class FakeWorld : IWindowWorld
{
    public readonly Dictionary<long, List<ControlRow>> Rows = new();
    public readonly Dictionary<long, long> Root = new();
    public readonly Dictionary<long, long> Doc = new();
    public readonly Dictionary<long, string> Screen = new();
    public readonly HashSet<long> Hidden = new();
    public Func<int, int, long> At = (_, _) => 0;
    public Func<int, int, long, bool> Menu = (_, _, _) => false;
    public IReadOnlyList<ControlRow>? RowsOf(long hwnd) => Rows.TryGetValue(hwnd, out var r) ? r : null;
    public bool IsVisible(long hwnd) => !Hidden.Contains(hwnd);
    public long WindowAt(int x, int y) => At(x, y);
    public long RootOf(long hwnd) => Root.TryGetValue(hwnd, out var r) ? r : hwnd;
    public long DocumentOf(long hwnd) => Doc.TryGetValue(hwnd, out var d) ? d : 0;
    public string ScreenOf(long hwnd) => Screen.TryGetValue(hwnd, out var s) ? s : "";
    public bool MenuPopupAt(int x, int y, long goingAway) => Menu(x, y, goingAway);
}
