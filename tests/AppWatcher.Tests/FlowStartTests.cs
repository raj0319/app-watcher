using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// A document of the workspace gets its title late, so its open is written at the first input inside it, or at its close when
/// the user only looked (r15 of 2026-11-20: Price Change 1 visit of 182 ms although it was open about 15 s). The visit starts at
/// the main window's press of the document's button or the strip's select of its tab, when nothing else happened in between.
/// </summary>
public class FlowStartTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 16, 0, 0);
    private const string Main = "Main";
    private const string Pc = Main + " › Price Change";
    private static readonly HashSet<string> Dialogs = new() { "Close?" };

    private static void Ev(List<UiEvent> list, double sec, string act, string win, string name, string ct = "Edit", string? by = null)
    {
        var e = Synthetic.Ev(T0.AddSeconds(sec), act, win, name, ct: ct, src: $"events/test.jsonl#L{list.Count + 1}");
        e.By = by;
        list.Add(e);
    }

    /// <summary>Price Change opened at <paramref name="press"/> (or by its tab only), the strip at +0.6 s, the program's fills, the late open and the Exit.</summary>
    private static List<UiEvent> PriceChange(double press, double open, Action<List<UiEvent>>? between = null, bool withPress = true)
    {
        var ev = new List<UiEvent>();
        Ev(ev, 0, Acts.Foreground, Main, Main, "Window");
        Ev(ev, 1, Acts.Focus, Main, Main, "Pane");
        if (withPress) Ev(ev, press, Acts.Invoke, Main, "Price Change", "Button");
        Ev(ev, press + 0.3, Acts.Value, Main, "Change Date", by: ValueBy.App);
        Ev(ev, press + 0.3, Acts.Value, Main, "Rollup Change", "ComboBox", ValueBy.App);
        Ev(ev, press + 0.6, Acts.Select, Main, "Price Change", "TabItem");
        between?.Invoke(ev);
        Ev(ev, open, Acts.Select, Pc, "Price Change", "TabItem");
        Ev(ev, open + 0.004, Acts.Open, Pc, Pc, "Window");
        Ev(ev, open + 0.1, Acts.Close, Pc, Pc, "Window");
        Ev(ev, open + 0.15, Acts.Focus, Main, Main, "Pane");
        Ev(ev, open + 0.17, Acts.Invoke, Pc, "Exit", "Button");
        Ev(ev, open + 10, Acts.Focus, Main, Main, "Pane");
        return ev;
    }

    [Fact]
    public void A_document_whose_open_is_written_late_starts_at_the_press_that_opened_it()
    {
        var ev = PriceChange(10, 24.9);

        var visits = FlowLearner.Visits(ev);
        var pc = Assert.Single(visits, v => v.Win == Pc);
        Assert.Equal(T0.AddSeconds(10), pc.Start);
        Assert.Equal(1, pc.Controls.Count(c => c == "tab Price Change"));   // the strip's select is the program's, not a second step
        Assert.Contains("press Price Change", visits.First(v => v.Win == Main).Controls); // the press stays on the main window

        var (flows, _, main, _) = FlowLearner.Learn(ev, null, Dialogs);
        Assert.Equal(Main, main);
        var f = Assert.Single(flows, x => x.Key == Pc);
        Assert.Equal(1, f.Count);
        Assert.InRange(f.MedianMs / 1000, 14.95, 15.1);
    }

    [Fact]
    public void The_strip_select_alone_starts_the_visit_and_typing_before_the_open_is_the_documents()
    {
        var ev = PriceChange(10, 24.9, l => Ev(l, 20, Acts.Value, Main, "Product"), withPress: false);

        var pc = Assert.Single(FlowLearner.Visits(ev), v => v.Win == Pc);
        Assert.Equal(T0.AddSeconds(10.6), pc.Start);
        Assert.Contains("type Product", pc.Controls);
    }

    [Fact]
    public void A_press_of_another_button_between_the_select_and_the_open_keeps_the_late_start()
    {
        var ev = PriceChange(10, 24.9, l => Ev(l, 15, Acts.Invoke, Main, "Customer Master", "Button"));

        var pc = Assert.Single(FlowLearner.Visits(ev), v => v.Win == Pc);
        Assert.Equal(T0.AddSeconds(24.9), pc.Start);
        var f = Assert.Single(FlowLearner.Learn(ev, null, Dialogs).flows, x => x.Key == Pc);
        Assert.InRange(f.MedianMs / 1000, 0.05, 0.2);
    }

    [Fact]
    public void A_select_more_than_30_seconds_before_the_open_keeps_the_late_start()
    {
        var ev = PriceChange(10, 45);

        var pc = Assert.Single(FlowLearner.Visits(ev), v => v.Win == Pc);
        Assert.Equal(T0.AddSeconds(45), pc.Start);
    }

    [Fact]
    public void A_menu_opened_or_another_window_in_between_keeps_the_late_start()
    {
        var menu = PriceChange(10, 24.9, l => Ev(l, 15, Acts.Menu, Main, "File", "Menu"));
        Assert.Equal(T0.AddSeconds(24.9), Assert.Single(FlowLearner.Visits(menu), v => v.Win == Pc).Start);

        var other = PriceChange(10, 24.9, l => Ev(l, 15, Acts.Foreground, Main + " › Customer Master", Main + " › Customer Master", "Window"));
        Assert.Equal(T0.AddSeconds(24.9), Assert.Single(FlowLearner.Visits(other), v => v.Win == Pc).Start);
    }

    [Fact]
    public void Backdated_visits_learn_the_same_whole_or_session_by_session()
    {
        var ev = PriceChange(10, 24.9);
        ev.AddRange(PriceChange(10, 24.9).Select(e => { e.T = e.T.AddHours(2); return e; }));

        var (flows, _, _, sessions) = FlowLearner.Learn(ev, null, Dialogs);
        Assert.Equal(2, sessions);
        var state = new LearnState();
        foreach (var s in FlowLearner.Sessions(ev)) { state.Sessions++; FlowLearner.Accumulate(state, s, null, Dialogs); }
        var (again, _, _) = FlowLearner.Finalize(state);
        var f = Assert.Single(flows, x => x.Key == Pc);
        Assert.Equal(2, f.Count);
        Assert.Equal(f.MedianMs, Assert.Single(again, x => x.Key == Pc).MedianMs);
    }
}
