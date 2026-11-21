using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The learner rows of the first v0.12 run against a tabbed-MDI ERP, on synthetic events shaped like the run's (no value or
/// name of the run): names of tabs and actions are masked like titles, partial codes typed for a lookup are no typing,
/// an editor over a grid cell is the cell's column, and a code typed before its document took its title belongs to the document.
/// </summary>
public class LearnerR2Tests
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

        /// <summary>The program prices the line: description, unit, price and extension of row "line 1".</summary>
        public void Priced(DateTime t, string code)
        {
            Add(t, Order, Acts.Value, "Product Number", code, ct: "DataItem", by: ValueBy.App, row: "line 1");
            Add(t, Order, Acts.Value, "Product Description", "WIDGET, BLUE", ct: "DataItem", by: ValueBy.App, row: "line 1");
            Add(t, Order, Acts.Value, "Quant UM", "EA", ct: "DataItem", by: ValueBy.App, row: "line 1");
            Add(t, Order, Acts.Value, "Net Price", "12.5000", ct: "DataItem", by: ValueBy.App, row: "line 1");
        }
    }

    private static Dictionary<string, string> Render(LearnInput input) => MemoryWriter.Render(Learner.Learn(input));

    private static string Section(string md, string screen)
    {
        var lines = md.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "## Screen: " + screen);
        if (start < 0) return "";
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    [Fact]
    public void Names_of_tabs_and_actions_are_masked_like_titles_in_every_memory_file()
    {
        // the main window's strip has a tab per open document, named after its record; one was pressed, one never used
        const string customerTab = "Customer - 0004711 \"JANE SAMPLE\"";
        const string vendorTab = "Vendor - 00999 \"ACME SUPPLY\"";
        var input = new LearnInput { AppName = "demo", Now = T0.AddDays(1) };
        input.Screens.Add(new ScreenRecord
        {
            Title = Main, Kind = ScreenKinds.Screen,
            Controls =
            {
                new ControlRow { Depth = 0, Type = "Window", Name = Main },
                new ControlRow { Depth = 1, Type = "Button", Name = "Sales Order" },
                new ControlRow { Depth = 1, Type = "Tab", Name = "" },
                new ControlRow { Depth = 2, Type = "TabItem", Name = customerTab },
                new ControlRow { Depth = 2, Type = "TabItem", Name = vendorTab },
            },
        });
        var log = new Log();
        log.Add(T0, Main, Acts.Open, Main, ct: "Window");
        log.Add(T0.AddSeconds(5), Main, Acts.Invoke, customerTab, ct: "Button", how: PressHow.Inferred);
        log.Add(T0.AddSeconds(9), Main, Acts.Invoke, "Sales Order", ct: "Button", how: PressHow.Inferred);
        input.Events.AddRange(log.Events);
        var files = Render(input);
        foreach (var (name, text) in files)
        {
            Assert.DoesNotContain("JANE SAMPLE", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ACME SUPPLY", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0004711", text);
            Assert.DoesNotContain("00999", text);
        }
        // still there, masked: the unused tab under "Not used", the pressed one among the screen's actions
        Assert.Contains("Vendor - # \"…\"", files["logic.md"]);
        Assert.Contains("Customer - # '…'", files["screens.md"]); // a table cell holds double quotes as single quotes
        Assert.Equal("Customer - # \"…\"", Titles.Name(customerTab));
    }

    [Fact]
    public void A_removed_line_of_an_older_memory_is_masked_in_the_changelog()
    {
        var saved = Titles.MaskPatterns;
        var dir = Path.Combine(Path.GetTempPath(), "aw-r2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Titles.MaskPatterns = new List<string> { "Shop Co - *", "Customer - * \"*\"" };
            Directory.CreateDirectory(dir);
            // what an older learner wrote: a record's name in a tab list, a table cell and a JSON string
            File.WriteAllText(Path.Combine(dir, "logic.md"), "# Logic\n- **Not used while I watched (tabs):** Audit, Customer - 0004711 \"JANE SAMPLE\", Freight\n");
            File.WriteAllText(Path.Combine(dir, "screens.md"), "# Screens\nActions: New, Customer - 0004711 'JANE SAMPLE', Exit\n## Screen: Shop Co - BIG CLIENT LLC › New Sales Order\n");
            File.WriteAllText(Path.Combine(dir, "logic.json"), "{\n  \"name\": \"Customer - 0004711 \\\"JANE SAMPLE\\\"\"\n}\n");
            MemoryWriter.Write(dir, Learner.Learn(new LearnInput { AppName = "demo", Now = T0 }));
            var log = File.ReadAllText(Path.Combine(dir, "changelog.md"));
            Assert.Contains("- removed:", log);
            Assert.DoesNotContain("JANE SAMPLE", log);
            Assert.DoesNotContain("BIG CLIENT", log);
            Assert.Contains("Customer - 0004711 \"…\"", log);
            Assert.Contains("Shop Co - … › New Sales Order", log);
        }
        finally { Titles.MaskPatterns = saved; try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_partial_code_followed_by_a_lookup_is_no_typing_of_the_field()
    {
        // one product code typed and priced; then twice a partial code typed to search the lookup (a dropdown follows) and no pick:
        // v0.12 counted three typings with one fill and lost "Typing Product Number fills …"
        var log = new Log();
        log.Add(T0, Order, Acts.Open, Order, ct: "Window");
        log.Add(T0.AddSeconds(10), Order, Acts.Value, "Product Number", "100EX", aid: "4711");
        log.Priced(T0.AddSeconds(10.6), "100EX");
        foreach (var (at, code) in new[] { (40, "100"), (48, "10") })
        {
            log.Add(T0.AddSeconds(at), Order, Acts.Value, "Product Number", code, aid: "4711");
            var list = log.Add(T0.AddSeconds(at + 1), Order, Acts.Dropdown, "Product Number", code, aid: "4711");
            list.Items = new() { "Number | Description", "100EX | WIDGET, BLUE" };
        }
        var s = new LearnState();
        LogicLearner.Accumulate(s, log.Events);
        Assert.Equal(1, s.Causes[LearnState.Key(Order, "typed:Product Number")]);
        var md = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = log.Events, Now = T0.AddDays(1) }))["logic.md"];
        var line = md.Split('\n').FirstOrDefault(l => l.Contains("Typing Product Number fills", StringComparison.Ordinal)) ?? "";
        foreach (var f in new[] { "Product Description", "Quant UM", "Net Price" }) Assert.Contains(f + " (seen 1× of 1)", line);
    }

    [Fact]
    public void The_grid_editor_is_its_column_and_a_value_the_grid_never_took_is_no_typing()
    {
        // the editor over the cell has no name the first time (its id is a number), the cell takes its value; the program shows the
        // product's page at once (two tabs read together); later two partial codes in the named editor that the grid never took
        var log = new Log();
        log.Add(T0, Order, Acts.Open, Order, ct: "Window");
        var typed = log.Add(T0.AddSeconds(10), Order, Acts.Value, "", "100EX", aid: "12256940");
        log.Add(T0.AddSeconds(10.4), Order, Acts.Select, "Product", ct: "TabItem", how: "read");
        log.Add(T0.AddSeconds(10.4), Order, Acts.Select, "Inventory", ct: "TabItem", how: "read");
        log.Priced(T0.AddSeconds(10.6), "100EX");
        log.Add(T0.AddSeconds(40), Order, Acts.Value, "Product Number", "100", aid: "12256940");
        log.Add(T0.AddSeconds(48), Order, Acts.Value, "Product Number", "10", aid: "12256940");
        log.Add(T0.AddSeconds(60), Order, Acts.Select, "Details", ct: "TabItem", how: "read");

        Assert.Equal("Edit", typed.FieldKey);
        var md = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = log.Events, Now = T0.AddDays(1) }))["logic.md"];
        Assert.Equal("Product Number", typed.FieldKey);
        var line = md.Split('\n').FirstOrDefault(l => l.Contains("Typing Product Number fills", StringComparison.Ordinal)) ?? "";
        foreach (var f in new[] { "Product Description", "Quant UM", "Net Price" }) Assert.Contains(f + " (seen 1× of 1)", line);
        Assert.DoesNotContain("Choosing Inventory fills", md);
        Assert.DoesNotContain("Typing Edit", md);

        // a filter box whose value never shows in a cell is not a grid editor: its typings all count
        var other = new Log();
        other.Add(T0, Order, Acts.Value, "Customer", "4711");
        other.Add(T0.AddSeconds(30), Order, Acts.Value, "Customer", "4712");
        Assert.Empty(GridEditors.PartialCodes(other.Events));
    }

    [Fact]
    public void A_code_typed_before_its_document_took_its_title_is_the_documents_rule()
    {
        // the customer code is typed in the main window, the program fills the header still under the main window's title, and the
        // document "› New Sales Order" opens 4.9 s after the typing (v0.12 listed the rule under the main window)
        var log = new Log();
        foreach (var t0 in new[] { T0, T0.AddMinutes(20) })
        {
            log.Add(t0, Main, Acts.Value, "", "0000001", aid: "srcCustomer");
            foreach (var (name, aid, val) in new[] { ("Ship To", "srcShipTo", "SAME"), ("Writer", "srcWriter", "Writer One"), ("Ship Via", "srcShipVia", "PICK UP"), ("Whse", "srcWarehouse", "02") })
                log.Add(t0.AddSeconds(0.6), Main, Acts.Value, name, val, by: ValueBy.App, aid: aid);
            log.Add(t0.AddSeconds(0.6), Main, Acts.Value, "Customer", "SAMPLE CUSTOMER", aid: "srcCustomer", how: "read"); // the program's echo
            log.Add(t0.AddSeconds(2.8), Order, Acts.Select, "New Sales Order", ct: "TabItem", how: "read");
            log.Add(t0.AddSeconds(4.9), Order, Acts.Open, Order, ct: "Window");
            log.Add(t0.AddSeconds(60), Order, Acts.Close, Order, ct: "Window");
        }
        var md = MemoryWriter.Render(Learner.Learn(new LearnInput { Events = log.Events, Now = T0.AddDays(1) }))["logic.md"];
        var doc = Section(md, Order);
        var main = Section(md, Main);
        Assert.Contains("Typing Customer fills", doc);
        foreach (var f in new[] { "Ship To", "Writer", "Ship Via", "Whse" }) Assert.Contains(f + " (seen 2× of 2)", doc);
        Assert.DoesNotContain("Typing Customer fills", main);

        // a press between the typing and the document is what opened it: the typing stays on the main window
        var pressed = new Log();
        pressed.Add(T0, Main, Acts.Value, "Customer", "0000001", aid: "srcCustomer");
        pressed.Add(T0.AddSeconds(1), Main, Acts.Invoke, "Sales Order", ct: "Button");
        pressed.Add(T0.AddSeconds(2), Order, Acts.Open, Order, ct: "Window");
        Assert.Empty(LogicLearner.TypedBeforeTitle(pressed.Events));
    }

    [Fact]
    public void Learning_the_new_rows_twice_or_incrementally_gives_the_same_memory()
    {
        var log = new Log();
        log.Add(T0, Main, Acts.Value, "", "0000001", aid: "srcCustomer");
        log.Add(T0.AddSeconds(0.6), Main, Acts.Value, "Ship To", "SAME", by: ValueBy.App, aid: "srcShipTo");
        log.Add(T0.AddSeconds(4.9), Order, Acts.Open, Order, ct: "Window");
        log.Add(T0.AddSeconds(10), Order, Acts.Value, "", "100EX", aid: "12256940");
        log.Priced(T0.AddSeconds(10.6), "100EX");
        log.Add(T0.AddSeconds(40), Order, Acts.Value, "Product Number", "100", aid: "12256940");
        // a second session an hour later
        var t1 = T0.AddHours(1);
        log.Add(t1, Order, Acts.Value, "Product Number", "200EX", aid: "12256940");
        log.Priced(t1.AddSeconds(0.5), "200EX");

        static List<UiEvent> Copy(List<UiEvent> ev) => ev.Select(e => new UiEvent { T = e.T, Win = e.Win, Act = e.Act, Name = e.Name, Val = e.Val, Ct = e.Ct, By = e.By, Aid = e.Aid, Row = e.Row, How = e.How, Source = e.Source }).ToList();
        static string Strip(Dictionary<string, string> d) => string.Join("\n", d.OrderBy(k => k.Key).Select(k => k.Key + "\n" + string.Join("\n", k.Value.Split('\n').Where(l => !l.StartsWith("Last learn:")))));
        var now = t1.AddDays(1);
        var full = Strip(MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Copy(log.Events), Now = now })));
        var again = Strip(MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Copy(log.Events), Now = now })));
        Assert.Equal(full, again);
        // the first session committed, then the second learned on top of the saved state
        var first = Copy(log.Events.Where(e => e.T < t1).ToList());
        Learner.Learn(new LearnInput { Events = first, Now = now }, new LearnState(), out var state);
        Assert.NotNull(state.Watermark);
        var incremental = Strip(MemoryWriter.Render(Learner.Learn(new LearnInput { Events = Copy(log.Events), Now = now }, state, out _)));
        Assert.Equal(full, incremental);
        Assert.Contains("Typing Product Number fills", full);
    }
}
