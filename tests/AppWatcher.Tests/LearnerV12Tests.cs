using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The learner rows of the v0.11 run against a tabbed-MDI ERP, on synthetic events shaped like the run's (no value of the run):
/// a code read twice is no formula, a grid row keeps its last known cells and the typed operand wins a tie, the program's
/// text replacing a typed code is no typing, one flow per document visit, and names on one line.
/// </summary>
public class LearnerV12Tests
{
    private const string Main = "Shop";
    private const string Order = "Shop › New Sales Order";
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0);

    private sealed class Log
    {
        public readonly List<UiEvent> Events = new();
        public UiEvent Add(DateTime t, string win, string act, string name, string? val = null, string ct = "Edit", string? by = null, string aid = "", string? row = null, string? how = null)
        {
            var e = new UiEvent { T = t, Win = win, Act = act, Name = name, Val = val, Ct = ct, By = by, Aid = aid, Row = row, How = how, Source = $"events/2026-11-20.jsonl#L{Events.Count + 1}" };
            Events.Add(e);
            return e;
        }
    }

    private static string Strip(string md) => string.Join("\n", md.Split('\n').Where(l => !l.StartsWith("Last learn:")));
    private static string LogicMd(List<UiEvent> ev, DateTime? now = null) => MemoryWriter.Render(Learner.Learn(new LearnInput { Events = ev, Now = now }))["logic.md"];

    [Fact]
    public void A_code_read_twice_is_not_a_formula()
    {
        // Whse "02" filled twice while Product Line "0087" stayed on the screen: 87 × 0.023 = 2.001 fits both times, but it is one
        // set of inputs seen twice (v0.11 wrote "Whse = Product Line × 0.023 (seen 2× of 3)")
        var log = new Log();
        log.Add(T0, Main, Acts.Value, "Product Line", "0087", by: ValueBy.App, aid: "srcProductLine");
        log.Add(T0.AddSeconds(40), Main, Acts.Value, "Whse", "02", by: ValueBy.App, aid: "srcWarehouse");
        log.Add(T0.AddMinutes(12), Main, Acts.Value, "Whse", "02", by: ValueBy.App, aid: "srcWarehouse");
        var s = new LearnState();
        LogicLearner.Accumulate(s, log.Events);
        Assert.Equal(1, LogicLearner.DistinctInputs(s, Main, "Whse", "Product Line × 0.023"));
        var md = LogicMd(log.Events);
        Assert.DoesNotContain("Whse = ", md);
        Assert.DoesNotContain("Whse matched", md);
    }

    [Fact]
    public void A_formula_needs_three_distinct_sets_of_inputs()
    {
        var log = new Log();
        void Line(DateTime t, string qty, string price, string total)
        {
            log.Add(t, Main, Acts.Value, "Quantity", qty);
            log.Add(t.AddSeconds(1), Main, Acts.Value, "Price", price);
            log.Add(t.AddSeconds(1.2), Main, Acts.Value, "Total", total, by: ValueBy.App);
        }
        Line(T0, "2", "10", "20");
        Line(T0.AddMinutes(1), "2", "10", "20");
        Assert.DoesNotContain("Total = ", LogicMd(log.Events.ToList()));                   // the same inputs twice: a question
        Line(T0.AddMinutes(2), "3", "10", "30");
        Line(T0.AddMinutes(3), "4", "12.5", "50");
        Assert.Contains("Total = Price × Quantity (seen 4× of 4)", LogicMd(log.Events.ToList()));
    }

    /// <summary>One order as the v0.11 collector writes it: grid cells keyed by row, wrapped column names, a back-order column.</summary>
    private static void PricedOrder(Log log, DateTime t0)
    {
        log.Add(t0, Order, Acts.Open, Order, ct: "Window");
        var b = t0.AddSeconds(17);
        log.Add(b, Order, Acts.Value, "Product\r\nNumber", "100EX", ct: "DataItem", row: "line 1", how: "read");
        foreach (var (n, v) in new[] { ("Price\r\nExtension", "45.34"), ("Net\r\nPrice", "45.3426"), ("Quant\r\nB/O", "1"), ("Quant\r\nTo Ship", "0"), ("Quant\r\nOrdered", "1"), ("Ln", "1") })
            log.Add(b, Order, Acts.Value, n, v, ct: "DataItem", by: ValueBy.App, row: "line 1");
        // Enter: the quantity, the new extension and back-order; the empty line 2 the program adds in the same read
        var q5 = t0.AddSeconds(26);
        log.Add(q5, Order, Acts.Value, "Price\r\nExtension", "0", ct: "DataItem", by: ValueBy.App, row: "line 2");
        log.Add(q5, Order, Acts.Value, "Net\r\nPrice", "0", ct: "DataItem", by: ValueBy.App, row: "line 2");
        log.Add(q5, Order, Acts.Value, "Ln", "2", ct: "DataItem", by: ValueBy.App, row: "line 2");
        log.Add(q5, Order, Acts.Value, "Price\r\nExtension", "226.71", ct: "DataItem", by: ValueBy.App, row: "line 1");
        log.Add(q5, Order, Acts.Value, "Quant\r\nB/O", "5", ct: "DataItem", by: ValueBy.App, row: "line 1");
        log.Add(q5, Order, Acts.Value, "Quant\r\nOrdered", "5", ct: "DataItem", row: "line 1", how: "read");
        var q20 = t0.AddSeconds(33);
        log.Add(q20, Order, Acts.Value, "Price\r\nExtension", "906.85", ct: "DataItem", by: ValueBy.App, row: "line 1");
        log.Add(q20, Order, Acts.Value, "Quant\r\nB/O", "20", ct: "DataItem", by: ValueBy.App, row: "line 1");
        log.Add(q20, Order, Acts.Value, "Quant\r\nOrdered", "20", ct: "DataItem", row: "line 1", how: "read");
        // a page change re-reads the row unchanged: no new observation
        var again = t0.AddSeconds(60);
        log.Add(again, Order, Acts.Select, "Detail", "Detail", ct: "TabItem");
        foreach (var (n, v) in new[] { ("Price\r\nExtension", "906.85"), ("Net\r\nPrice", "45.3426"), ("Quant\r\nB/O", "20"), ("Quant\r\nOrdered", "20") })
            log.Add(again.AddSeconds(0.3), Order, Acts.Value, n, v, ct: "DataItem", by: ValueBy.App, row: "line 1");
        log.Add(t0.AddSeconds(90), Order, Acts.Close, Order, ct: "Window");
    }

    [Fact]
    public void Price_Extension_is_Quant_Ordered_times_Net_Price_from_the_row_seen_whole()
    {
        var log = new Log();
        PricedOrder(log, T0);
        var md = LogicMd(log.Events);
        // the net price was read once and kept with its row; the ordered quantity (typed) wins over the back-order quantity
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 3× of 3)", md);
        Assert.DoesNotContain("× Quant B/O", md);
        Assert.DoesNotContain("Quant B/O is copied", md);
        // the unchanged re-read after choosing a page is no fill
        Assert.DoesNotContain("Choosing Detail fills", md);
        // names on one line
        Assert.DoesNotContain("Net\r\nPrice", md);
        Assert.DoesNotContain("Net\nPrice", md);
        Assert.DoesNotContain("Quant\r\nOrdered", md);
        Assert.DoesNotContain("Net  Price", md);
    }

    [Fact]
    public void Names_are_on_one_line_when_events_are_read()
    {
        Assert.Equal("Net Price", Titles.OneLine("Net\r\nPrice"));
        Assert.Equal("Quant To Ship", Titles.OneLine("Quant\r\nTo  Ship "));
        Assert.Equal("Customer", Titles.OneLine("Customer"));
        var log = new Log();
        var e = log.Add(T0, Order, Acts.Value, "Net\r\nPrice", "1", ct: "DataItem", by: ValueBy.App, row: "line 1");
        Learner.Learn(new LearnInput { Events = log.Events });
        Assert.Equal("Net Price", e.FieldKey);
    }

    /// <summary>The customer code typed in a box with no name (AutomationId srcCustomer) before the document has its title,
    /// the header filled 0.6 s later in the main window, the box re-read with the customer's name, then the document's title.</summary>
    private static void CustomerOrder(Log log, DateTime t0)
    {
        log.Add(t0, Main, Acts.Value, "", "0000001", aid: "srcCustomer");
        var f = t0.AddSeconds(0.6);
        foreach (var (name, aid, val) in new[] { ("Ship To", "srcShipTo", "SAME"), ("Writer", "srcWriter", "Writer One"), ("Ship Via", "srcShipVia", "PICK UP"), ("Branch", "srcBranch", "02 - Sample Co"), ("Whse", "srcWarehouse", "MAIN WH") })
            log.Add(f, Main, Acts.Value, name, val, by: ValueBy.App, aid: aid);
        log.Add(f, Main, Acts.Value, "Customer", "Test Customer", aid: "srcCustomer", how: "read");
        log.Add(t0.AddSeconds(2.7), Order, Acts.Select, "New Sales Order", ct: "TabItem", how: "read");
        log.Add(t0.AddSeconds(5), Order, Acts.Open, Order, ct: "Window");
        log.Add(t0.AddSeconds(60), Order, Acts.Close, Order, ct: "Window");
    }

    [Fact]
    public void Typing_the_customer_code_fills_the_header_even_when_the_box_shows_the_name_after()
    {
        var log = new Log();
        CustomerOrder(log, T0);
        var once = LogicMd(log.Events.ToList());
        var line = once.Split('\n').FirstOrDefault(l => l.Contains("Typing Customer fills", StringComparison.Ordinal)) ?? "";
        foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) Assert.True(line.Contains(f + " (seen 1× of 1)", StringComparison.Ordinal), once);
        Assert.DoesNotContain("Customer (2×", once); // the name the program put in the box is not a second typing

        CustomerOrder(log, T0.AddMinutes(14));
        var twice = LogicMd(log.Events.ToList());
        line = twice.Split('\n').FirstOrDefault(l => l.Contains("Typing Customer fills", StringComparison.Ordinal)) ?? "";
        foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Branch", "Whse" }) Assert.True(line.Contains(f + " (seen 2× of 2)", StringComparison.Ordinal), twice);
    }

    [Fact]
    public void An_echo_is_only_a_read_of_the_same_box_right_after_the_typing()
    {
        var log = new Log();
        log.Add(T0, Main, Acts.Value, "", "0000001", aid: "srcCustomer");
        var echo = log.Add(T0.AddSeconds(0.6), Main, Acts.Value, "Customer", "Test Customer", aid: "srcCustomer", how: "read");
        var later = log.Add(T0.AddSeconds(5), Main, Acts.Value, "Customer", "0000002", aid: "srcCustomer", how: "read"); // typed again later
        var other = log.Add(T0.AddSeconds(5.5), Main, Acts.Value, "Attention", "Someone", how: "read");                   // another box
        var echoes = LogicLearner.Echoes(log.Events);
        Assert.Contains(echo, echoes);
        Assert.DoesNotContain(later, echoes);
        Assert.DoesNotContain(other, echoes);
    }

    /// <summary>A document visit as the v0.11 collector writes it: the document's tab selected, the window opened, the main
    /// window's strip read selecting the same tab, inner pages, the close, the main window's focus, then Exit (heard at the destroy).</summary>
    private static void ProductVisit(Log log, DateTime t0)
    {
        const string doc = "Shop › Product - 100EX";
        log.Add(t0, Main, Acts.Value, "", "100EX", aid: "srcMasterSearch");
        log.Add(t0.AddSeconds(0.5), doc, Acts.Select, "Product - 100EX", ct: "TabItem", how: "read");
        log.Add(t0.AddSeconds(0.6), doc, Acts.Open, doc, ct: "Window");
        log.Add(t0.AddSeconds(2), Main, Acts.Select, "Product - 100EX", "Product - 100EX", ct: "TabItem", how: "read");
        log.Add(t0.AddSeconds(8), doc, Acts.Select, "Current Warehouse", "Current Warehouse", ct: "TabItem", how: "read");
        log.Add(t0.AddSeconds(12), doc, Acts.Select, "Overview", "Overview", ct: "TabItem", how: "read");
        log.Add(t0.AddSeconds(30), doc, Acts.Close, doc, ct: "Window");
        log.Add(t0.AddSeconds(30.03), Main, Acts.Focus, Main, ct: "Pane");
        log.Add(t0.AddSeconds(30.05), doc, Acts.Invoke, "Exit", ct: "Button", how: "inferred");
        log.Add(t0.AddSeconds(45), Main, Acts.Select, "Analysis", "Analysis", ct: "TabItem", how: "read");
    }

    [Fact]
    public void One_flow_per_document_visit()
    {
        var log = new Log();
        for (var i = 0; i < 3; i++) ProductVisit(log, T0.AddMinutes(i * 3));
        var r = Learner.Learn(new LearnInput { Events = log.Events });
        var flow = r.Flows.Single(f => f.Key.Contains("Product", StringComparison.Ordinal));
        Assert.Equal(3, flow.Count);
        Assert.True(flow.MedianMs > 20000, $"median {flow.MedianMs} ms");
        Assert.Contains(flow.StepControls.SelectMany(s => s.Value), c => c.Item1 == "press Exit" && c.Item2 == 3);
    }

    [Fact]
    public void The_new_rules_learn_the_same_twice_and_incrementally()
    {
        List<UiEvent> Build(int sessions)
        {
            var log = new Log();
            for (var i = 0; i < sessions; i++)
            {
                var t = T0.AddHours(i * 2);
                CustomerOrder(log, t);
                PricedOrder(log, t.AddMinutes(2));
                ProductVisit(log, t.AddMinutes(5));
            }
            return log.Events;
        }
        var now = T0.AddDays(1);
        var full = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Build(3), Now = now }));
        var again = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Build(3), Now = now }));
        Learner.Learn(new LearnInput { Events = Build(1), Now = now }, new LearnState(), out var state);
        var inc = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Build(3), Now = now }, state, out _));
        foreach (var name in MemoryWriter.Files)
        {
            Assert.Equal(Strip(full[name]), Strip(again[name]));
            Assert.Equal(Strip(full[name]), Strip(inc[name]));
        }
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 9× of 9)", full["logic.md"]);
    }
}
