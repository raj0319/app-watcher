using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>The trainee's knowledge: what fills what, screen calculations, what nobody used, hover tips, questions.</summary>
public class LogicTests
{
    private static readonly DateTime Day1 = new(2026, 3, 2, 9, 0, 0);

    private static UiEvent Typed(DateTime t, string win, string field, string val) => Synthetic.Ev(t, Acts.Value, win, field, val);
    private static UiEvent Filled(DateTime t, string win, string field, string val) { var e = Synthetic.Ev(t, Acts.Value, win, field, val); e.By = ValueBy.App; return e; }

    /// <summary>One order line: the user types quantity, price and discount; the program shows the line total and looks up the customer name.</summary>
    private static IEnumerable<UiEvent> OrderLine(DateTime t, string qty, string price, string disc, string total, string code, string name)
    {
        yield return Typed(t, "Orders", "Customer code", code);
        yield return Filled(t.AddMilliseconds(300), "Orders", "Customer name", name);
        yield return Typed(t.AddSeconds(2), "Orders", "Quantity", qty);
        yield return Typed(t.AddSeconds(4), "Orders", "Unit price", price);
        yield return Typed(t.AddSeconds(6), "Orders", "Discount %", disc);
        yield return Filled(t.AddSeconds(6.3), "Orders", "Line total", total);
    }

    private static LearnInput Orders(params DateTime[] days)
    {
        var input = new LearnInput { AppName = "demo", Now = days.Max().AddDays(1) };
        var lines = new[] { ("4", "12.50", "10", "45.00", "C001", "Alpha Ltd"), ("3", "20", "5", "57.00", "C002", "Beta Traders"), ("10", "1.99", "15", "16.92", "C003", "Gamma Co") };
        foreach (var d in days)
            for (var i = 0; i < lines.Length; i++)
            {
                var (q, p, ds, tot, c, n) = lines[i];
                input.Events.AddRange(OrderLine(d.AddMinutes(i), q, p, ds, tot, c, n));
            }
        return input;
    }

    [Fact]
    public void Learns_the_calculation_and_what_the_program_fills_in()
    {
        var r = Learner.Learn(Orders(Day1));
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Line total");
        Assert.Equal("Quantity × Unit price × (1 − Discount % / 100)", calc.Formula);
        Assert.Equal(3, calc.Fits);
        Assert.Equal(3, calc.Tested);
        Assert.False(calc.WithExceptions);
        var link = Assert.Single(r.Logic.Links, l => l.Field == "Customer name");
        Assert.Equal("typed:Customer code", link.When);
        Assert.Equal(3, link.Seen);

        var memory = MemoryWriter.Render(r);
        Assert.Contains("Line total = Quantity × Unit price × (1 − Discount % / 100) (seen 3× of 3)", memory["logic.md"]);
        Assert.Contains("Typing Customer code fills Customer name (seen 3× of 3)", memory["logic.md"]);
        Assert.Contains("\"formula\": \"Quantity × Unit price × (1 − Discount % / 100)\"", memory["logic.json"]);
        // values typed or shown never reach the memory, only names, formulas and counts
        foreach (var text in memory.Values)
            foreach (var value in new[] { "Alpha Ltd", "Beta Traders", "C001", "12.50", "57.00" })
                Assert.DoesNotContain(value, text);
        // a value the program filled in is not counted as typed by the user
        Assert.False(r.Screens.Single(s => s.Title == "Orders").FieldTyped.ContainsKey("Line total"));
    }

    [Fact]
    public void A_total_reported_just_before_the_typed_value_that_caused_it_is_still_explained()
    {
        // what Windows really reports: the program recalculates inside the typing, so the total arrives ~20 ms first
        var input = new LearnInput { AppName = "demo", Now = Day1.AddDays(1) };
        var t = Day1;
        input.Events.Add(Typed(t, "Quote", "Unit price", "12.50"));
        input.Events.Add(Typed(t.AddSeconds(1), "Quote", "Customer code", "C001"));
        foreach (var (i, qty, total) in new[] { (0, "4", "50.00"), (1, "3", "37.50"), (2, "7", "87.50") })
        {
            var at = t.AddSeconds(3 + 2 * i);
            input.Events.Add(Filled(at, "Quote", "Line total", total));
            input.Events.Add(Typed(at.AddMilliseconds(20), "Quote", "Quantity", qty));
        }
        var r = Learner.Learn(input);
        var calc = Assert.Single(r.Logic.Calculations, c => c.Field == "Line total");
        Assert.Equal("Quantity × Unit price", calc.Formula);
        Assert.Equal(3, calc.Fits);
        Assert.Contains(r.Logic.Links, l => l.When == "typed:Quantity" && l.Field == "Line total" && l.Seen == 3);
        Assert.DoesNotContain(r.Logic.Links, l => l.When == "typed:Customer code" && l.Field == "Line total");
    }

    [Fact]
    public void Incremental_learning_gives_the_same_logic_as_a_full_learn()
    {
        var day2 = Day1.AddDays(1);
        var full = MemoryWriter.Render(Learner.Learn(Orders(Day1, day2)));
        var first = Orders(Day1); first.Now = day2.AddDays(1);
        Learner.Learn(first, new LearnState(), out var state);
        var again = MemoryWriter.Render(Learner.Learn(Orders(Day1, day2), state, out _));
        Assert.Equal(full["logic.md"], again["logic.md"]);
        Assert.Equal(full["logic.json"], again["logic.json"]);
    }

    [Fact]
    public void A_number_the_program_fills_without_a_fitting_formula_becomes_a_question()
    {
        var input = new LearnInput { AppName = "demo", Now = Day1.AddDays(1) };
        var pairs = new[] { ("7", "131"), ("2", "977"), ("5", "4") };
        for (var i = 0; i < pairs.Length; i++)
        {
            var t = Day1.AddMinutes(i);
            input.Events.Add(Typed(t, "Loyalty", "Visits", pairs[i].Item1));
            input.Events.Add(Filled(t.AddMilliseconds(400), "Loyalty", "Points", pairs[i].Item2));
        }
        var r = Learner.Learn(input);
        Assert.Contains(r.Logic.Unexplained, c => c.Field == "Points" && c.Tested == 3);
        Assert.Contains(r.Logic.Questions, q => q.Text.Contains("fills Points itself (3×), but I could not work out how"));
    }

    [Fact]
    public void Lists_what_nobody_used_what_buttons_lead_to_and_the_hover_tips()
    {
        var input = new LearnInput { AppName = "demo", Now = Day1.AddDays(1) };
        input.Screens.Add(new ScreenRecord
        {
            Title = "Orders", Kind = ScreenKinds.Screen,
            Controls =
            {
                new ControlRow { Depth = 0, Type = "Window", Name = "Orders" },
                new ControlRow { Depth = 1, Type = "Button", Name = "Post" },
                new ControlRow { Depth = 1, Type = "Button", Name = "Print" },
                new ControlRow { Depth = 1, Type = "Button", Name = "Close" },
                new ControlRow { Depth = 1, Type = "MenuItem", Name = "Reports" },
                new ControlRow { Depth = 1, Type = "ComboBox", Name = "Type" },
                new ControlRow { Depth = 2, Type = "Button", Name = "Open" },
            },
        });
        var t = Day1;
        var menu = Synthetic.Ev(t, Acts.Menu, "Orders", "File", ct: "Menu"); menu.Items = new() { "New order", "Export" };
        input.Events.Add(menu);
        var choose = Synthetic.Ev(t.AddSeconds(1), Acts.Invoke, "Orders", "New order", ct: "MenuItem"); choose.How = PressHow.Menu;
        input.Events.Add(choose);
        for (var i = 0; i < 2; i++)
        {
            var at = t.AddMinutes(1 + i);
            input.Events.Add(Synthetic.Ev(at, Acts.Invoke, "Orders", "Post", ct: "Button"));
            input.Events.Add(Synthetic.Ev(at.AddMilliseconds(500), Acts.Dialog, "Orders", "Validation", "Amount must be a number.", ct: "Window"));
            var hint = Synthetic.Ev(at.AddSeconds(5), Acts.Hint, "Orders", "Post", "Post the order to the ledger", ct: "Button");
            input.Events.Add(hint);
        }
        var r = Learner.Learn(input);
        Assert.Contains(r.Logic.NotUsed, n => n.Kind == "button" && n.Name == "Print");
        Assert.DoesNotContain(r.Logic.NotUsed, n => n.Name is "Post" or "Close" or "New order" or "Open"); // "Open" is the dropdown's own arrow
        Assert.Contains(r.Logic.NotUsed, n => n.Kind == "menu item" && n.Name == "Export");
        Assert.Contains(r.Logic.NotUsed, n => n.Kind == "menu item" && n.Name == "Reports");
        var lead = Assert.Single(r.Logic.Leads, l => l.Button == "Post");
        Assert.Equal("Validation", lead.Opens);
        Assert.True(lead.Message);
        Assert.Equal(2, lead.Seen);
        Assert.Contains(r.Logic.Hints, h => h.Control == "Post" && h.Text == "Post the order to the ledger" && h.Seen == 2);
        Assert.Contains(r.Logic.Questions, q => q.Text.StartsWith("What does Print on Orders do?"));
        var md = MemoryWriter.Render(r)["logic.md"];
        Assert.Contains("Post opens Validation (a message) (seen 2×)", md);
        Assert.Contains("Post: \"Post the order to the ledger\" (seen 2×)", md);
        Assert.Contains("Menu items nobody chose (2): Export, Reports", md);
    }

    [Fact]
    public void Formula_search_prefers_the_simplest_formula_that_fits()
    {
        var fits = LogicLearner.Formulas(30m, new List<(string n, decimal v)> { ("Price", 10m), ("Quantity", 3m) });
        Assert.Contains("Price × Quantity", fits);
        Assert.DoesNotContain("Price + Quantity", fits);
        Assert.True(LogicLearner.Close(16.92m, 10m * 1.99m * (1 - 15m / 100)));
    }
}
