using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// Order-line formulas of the r7 run, on synthetic events shaped like the run's (no value of the run): the back-order quantity
/// is the typed quantity copied, not the extension divided by the price (that is the extension's product read backwards), and
/// a row read while the typed quantity was still in the grid's editor is no exception to the extension's formula.
/// </summary>
public class FormulasR7Tests
{
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
    private static string LogicMd(List<UiEvent> ev) => MemoryWriter.Render(Learner.Learn(new LearnInput { Events = ev, Now = T0.AddDays(1) }))["logic.md"];

    private static void Cells(Log log, DateTime t, params (string n, string v)[] cells)
    {
        foreach (var (n, v) in cells) log.Add(t, Order, Acts.Value, n, v, ct: "DataItem", by: ValueBy.App, row: "line 1");
    }

    /// <summary>
    /// One order line: the product priced with quantity 1, then the quantities typed in the row (the program writes the new
    /// extension and back-order in the same read). <paramref name="staleQty"/>: one more quantity typed in the grid's editor
    /// whose value reaches the log <paramref name="editorLate"/> seconds after the row was read, the quantity cell not re-read.
    /// </summary>
    private static void OrderLine(Log log, DateTime t0, decimal price, int[] qtys, int? staleQty = null, double editorLate = 0.7)
    {
        log.Add(t0, Order, Acts.Open, Order, ct: "Window");
        var p = price.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Ext(int q) => Math.Round(price * q, 2, MidpointRounding.AwayFromZero).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var b = t0.AddSeconds(10);
        Cells(log, b, ("Price Extension", Ext(1)), ("Net Price", p), ("Quant B/O", "1"), ("Quant To Ship", "0"), ("Quant Ordered", "1"));
        var t = b;
        foreach (var q in qtys)
        {
            t = t.AddSeconds(8);
            Cells(log, t, ("Price Extension", Ext(q)), ("Quant B/O", q.ToString()));
            log.Add(t, Order, Acts.Value, "Quant Ordered", q.ToString(), ct: "DataItem", row: "line 1", how: "read");
        }
        if (staleQty is { } sq)
        {
            t = t.AddSeconds(8);
            Cells(log, t, ("Price Extension", Ext(sq)), ("Quant B/O", sq.ToString()));
            log.Add(t.AddSeconds(editorLate), Order, Acts.Value, "Line Items", sq.ToString(), aid: "gridLIs");
        }
        log.Add(t.AddSeconds(30), Order, Acts.Close, Order, ct: "Window");
    }

    [Fact]
    public void The_back_order_quantity_is_a_copy_not_the_extension_divided_by_the_price()
    {
        var log = new Log();
        OrderLine(log, T0, 45.3426m, new[] { 5, 20 });
        OrderLine(log, T0.AddMinutes(5), 12.5m, new[] { 3, 7 });
        var md = LogicMd(log.Events);
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 6× of 6)", md);
        Assert.Contains("Quant B/O is copied from Quant Ordered (seen 6× of 6)", md);
        Assert.DoesNotContain("÷", md);
    }

    [Fact]
    public void A_row_read_before_the_typed_quantity_reached_it_is_no_exception()
    {
        // the second order: the quantity 7 typed in the grid's editor, the row read with the new extension while the quantity
        // cell still shows 3; the editor's value comes 0.7 s later
        var log = new Log();
        OrderLine(log, T0, 45.3426m, new[] { 5, 20 });
        OrderLine(log, T0.AddMinutes(5), 12.5m, new[] { 3 }, staleQty: 7);
        var md = LogicMd(log.Events);
        Assert.Contains("Price Extension = Net Price × Quant Ordered (seen 5× of 5)", md);
        Assert.Contains("Quant B/O is copied from Quant Ordered (seen 5× of 5)", md);
        Assert.DoesNotContain("exceptions", md);
    }

    [Fact]
    public void A_row_whose_typed_quantity_never_came_is_still_an_observation()
    {
        // the same read with no typing after it (or typing long after): the row shows what the program shows, so it counts
        var log = new Log();
        OrderLine(log, T0, 45.3426m, new[] { 5, 20 });
        OrderLine(log, T0.AddMinutes(5), 12.5m, new[] { 3 }, staleQty: 7, editorLate: 6);
        var md = LogicMd(log.Events);
        Assert.DoesNotContain("Price Extension = Net Price × Quant Ordered (seen 5× of 5)", md);
        Assert.Contains("of 6", md);
    }

    [Fact]
    public void A_quotient_is_kept_when_the_user_types_its_dividend()
    {
        var s = new LearnState();
        var key = (string f, string formula) => LearnState.Key(Order, f, formula);
        var ev = new Evidence(); ev.Add("e#L1"); ev.Add("e#L2"); ev.Add("e#L3");
        s.CalcFits[key("Price Extension", "Net Price × Quant B/O")] = ev;
        s.CalcFits[key("Quant B/O", "Price Extension ÷ Net Price")] = ev;
        s.CalcFits[key("Margin", "Price Extension − Cost")] = ev;
        s.CalcFits[key("Price Extension", "Cost + Margin")] = ev;
        Assert.True(LogicLearner.Rearranged(s, Order, "Quant B/O", "Price Extension ÷ Net Price", 3));
        Assert.True(LogicLearner.Rearranged(s, Order, "Margin", "Price Extension − Cost", 3));
        // a total the user types, divided by a quantity, gives the unit price: that quotient is the program's formula
        s.Causes[LearnState.Key(Order, "typed:Price Extension")] = 3;
        Assert.False(LogicLearner.Rearranged(s, Order, "Quant B/O", "Price Extension ÷ Net Price", 3));
        // no product of the dividend to read it from: the quotient stands
        Assert.False(LogicLearner.Rearranged(new LearnState(), Order, "Quant B/O", "Price Extension ÷ Net Price", 3));
        // the product fitted far less often than the quotient: not the same relation's evidence
        var s2 = new LearnState();
        var one = new Evidence(); one.Add("e#L1");
        s2.CalcFits[key("Price Extension", "Net Price × Quant B/O")] = one;
        Assert.False(LogicLearner.Rearranged(s2, Order, "Quant B/O", "Price Extension ÷ Net Price", 6));
    }

    [Fact]
    public void Learns_the_same_twice_and_incrementally()
    {
        List<UiEvent> Build(int orders)
        {
            var log = new Log();
            for (var i = 0; i < orders; i++)
                OrderLine(log, T0.AddHours(i * 2), 10m + i * 3.25m, new[] { 3 + i, 11 }, staleQty: 4 + i);
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
        Assert.DoesNotContain("÷", full["logic.md"]);
    }
}
