using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// One flow per document visit (the open of the MDI child until its close): the program's own popup, the prompt that answers
/// the Exit and the document's sub-tabs credited to the main window never split it (r3 of 2026-11-20: New Purchase Order was 2
/// for 1 open, Vendor 3 for 2).
/// </summary>
public class FlowsR4Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 16, 0, 0);
    private const string Main = "Main";
    private const string Notes = "Vendor - 00001 \"ACME\" Internal Notes";
    private static readonly HashSet<string> Dialogs = new() { "Close?" };

    private static void Ev(List<UiEvent> list, double sec, string act, string win, string name, string ct = "Edit", string? by = null)
    {
        var e = Synthetic.Ev(T0.AddSeconds(sec), act, win, name, ct: ct, src: $"events/test.jsonl#L{list.Count + 1}");
        e.By = by;
        list.Add(e);
    }

    private static void PriceChange(List<UiEvent> ev, double at)
    {
        Ev(ev, at, Acts.Invoke, Main, "Price Change", "Button");
        Ev(ev, at + 1, Acts.Select, Main + " › Price Change", "Price Change", "TabItem");
        Ev(ev, at + 10, Acts.Close, Main + " › Price Change", Main + " › Price Change", "Window");
        Ev(ev, at + 10.05, Acts.Focus, Main, Main, "Pane");
        Ev(ev, at + 10.07, Acts.Invoke, Main + " › Price Change", "Exit", "Button");
    }

    [Fact]
    public void New_purchase_order_with_Exit_then_Close_prompt_then_OK_then_the_main_window_is_one_flow()
    {
        const string Po = Main + " › New Purchase Order";
        var ev = new List<UiEvent>();
        Ev(ev, 0, Acts.Foreground, Main, Main, "Window");
        Ev(ev, 1, Acts.Invoke, Main, "Purchase Order", "Button");
        Ev(ev, 2, Acts.Select, Main, "Purchase Order Entry", "TabItem");
        Ev(ev, 7.7, Acts.Select, Po, "New Purchase Order", "TabItem");     // the strip read as the document comes in front
        Ev(ev, 7.705, Acts.Value, Po, "Branch", by: ValueBy.App);
        Ev(ev, 7.708, Acts.Foreground, Notes, Notes, "Window");            // the vendor's notes, opened by the program
        Ev(ev, 8.5, Acts.Open, Notes, Notes, "Window");
        Ev(ev, 9.7, Acts.Open, Po, Po, "Window");
        Ev(ev, 29.5, Acts.Select, Main, "Main", "TabItem");                // the document's sub-tab, credited to the main window
        Ev(ev, 48, Acts.Foreground, Po, Po, "Window");
        Ev(ev, 49, Acts.Foreground, Notes, Notes, "Window");
        Ev(ev, 49.1, Acts.Foreground, Po, Po, "Window");
        Ev(ev, 49.2, Acts.Invoke, Notes, "Exit", "Button");
        Ev(ev, 49.21, Acts.Close, Notes, Notes, "Window");
        Ev(ev, 64, Acts.Foreground, "Close?", "Close?", "Window");          // Exit asks first
        Ev(ev, 64.6, Acts.Dialog, "Close?", "Close?", "Window");
        Ev(ev, 68.2, Acts.Foreground, Po, Po, "Window");
        Ev(ev, 68.3, Acts.Close, Po, Po, "Window");
        Ev(ev, 68.34, Acts.Focus, Main, Main, "Pane");
        Ev(ev, 68.36, Acts.Invoke, "Close?", "OK", "Button");
        Ev(ev, 68.37, Acts.Close, "Close?", "Close?", "Window");
        PriceChange(ev, 75);

        var (flows, _, main, _) = FlowLearner.Learn(ev, null, Dialogs);
        Assert.Equal(Main, main);
        var po = Assert.Single(flows, f => f.Key == Po);
        Assert.Equal(1, po.Count);
        Assert.InRange(po.MedianMs / 1000, 60.5, 60.7); // from the document in front to its close
        Assert.Contains(po.StepControls[Po], c => c.name == "tab Main");
        Assert.Equal(1, Assert.Single(flows, f => f.Key == Main + " › Price Change").Count);
        Assert.DoesNotContain(flows, f => f.Key.Contains("Internal Notes") || f.Key == "Close?");
    }

    [Fact]
    public void Vendor_visit_with_the_notes_popup_opening_by_itself_and_a_sub_tab_click_is_one_flow()
    {
        const string Vendor = Main + " › Vendor - 00001 \"ACME\"";
        var ev = new List<UiEvent>();
        Ev(ev, 0, Acts.Foreground, Main, Main, "Window");
        Ev(ev, 1, Acts.Invoke, Main, "Vendor Master", "Button");
        Ev(ev, 2, Acts.Select, Main, "Vendor Master", "TabItem");
        Ev(ev, 8, Acts.Value, Main, "Master Search");
        Ev(ev, 9.0, Acts.Select, Vendor, "Vendor - 00001 \"ACME\"", "TabItem"); // the vendor loaded: its document's own tab
        Ev(ev, 9.009, Acts.Foreground, Notes, Notes, "Window");                   // 9 ms later the program pops the notes (once)
        Ev(ev, 9.6, Acts.Open, Notes, Notes, "Window");
        Ev(ev, 10.1, Acts.Foreground, Vendor, Vendor, "Window");
        Ev(ev, 18, Acts.Select, Main, "General", "TabItem");                     // sub-tab clicks credited to the main window
        Ev(ev, 24, Acts.Select, Main, "Drop Ship Charges", "TabItem");
        Ev(ev, 40, Acts.Foreground, Vendor, Vendor, "Window");
        Ev(ev, 52, Acts.Invoke, Vendor, "Exit", "Button");
        Ev(ev, 52.1, Acts.Close, Vendor, Vendor, "Window");
        Ev(ev, 52.2, Acts.Foreground, Main, Main, "Window");
        PriceChange(ev, 60);

        var visits = FlowLearner.Visits(ev);
        Assert.True(Assert.Single(visits, v => v.Win == Titles.Key(Notes)).ShownByProgram); // the document's own tab is not a click

        var (flows, _, _, _) = FlowLearner.Learn(ev, null, Dialogs);
        var key = Titles.Key(Vendor);
        var vendor = Assert.Single(flows, f => f.Key == key);
        Assert.Equal(1, vendor.Count);
        Assert.InRange(vendor.MedianMs / 1000, 43.0, 43.2);
        Assert.Equal(new[] { key }, vendor.CommonPath);
        Assert.Contains(vendor.StepControls[key], c => c.name == "tab General");
        Assert.Contains(vendor.StepControls[key], c => c.name == "press Exit");
        Assert.Equal(1, Assert.Single(flows, f => f.Key == Main + " › Price Change").Count);
        Assert.DoesNotContain(flows, f => f.Key.Contains("Internal Notes"));
    }

    [Fact]
    public void A_main_window_visit_with_a_press_between_two_visits_of_a_document_still_cuts()
    {
        // a press on the main window (a toolbar or menu pick) is the person's own step there, not a sub-tab of the document
        const string Doc = Main + " › Price Change";
        var ev = new List<UiEvent>();
        Ev(ev, 0, Acts.Foreground, Main, Main, "Window");
        Ev(ev, 1, Acts.Invoke, Main, "Price Change", "Button");
        Ev(ev, 2, Acts.Select, Doc, "Price Change", "TabItem");
        Ev(ev, 10, Acts.Invoke, Main, "Report", "Button");
        Ev(ev, 20, Acts.Foreground, Doc, Doc, "Window");
        Ev(ev, 30, Acts.Invoke, Doc, "Save", "Button");
        var (flows, _, _, _) = FlowLearner.Learn(ev, null, Dialogs);
        Assert.Equal(2, Assert.Single(flows, f => f.Key == Doc).Count);
    }
}
