using System.Xml.Linq;
using AppWatcher.Core.Events;
using AppWatcher.Core.Export;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sql;
using Xunit;

namespace AppWatcher.Tests;

public class IncrementalAndExportTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0);

    /// <summary>Two days of synthetic work: orders typed and saved, with effects.</summary>
    private static LearnInput Build(int days)
    {
        var input = new LearnInput { AppName = "demo", Now = Now };
        var t = new DateTime(2026, 1, 10, 9, 0, 0);
        var id = 100;
        for (var d = 0; d < days; d++)
        {
            var day = t.AddDays(d);
            for (var k = 0; k < 4; k++)
            {
                var s = day.AddMinutes(k * 7);
                var name = $"Customer {d}-{k}";
                var amount = (50 + d * 10 + k).ToString();
                input.Events.Add(Synthetic.Ev(s, Acts.Foreground, "Main", "Main", ct: "Window", src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 1}"));
                input.Events.Add(Synthetic.Ev(s.AddSeconds(4), Acts.Invoke, "Main", "New Order", ct: "Button", src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 1}")); // the click that opens it
                input.Events.Add(Synthetic.Ev(s.AddSeconds(5), Acts.Foreground, "New Order", "New Order", ct: "Window", src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 2}"));
                input.Events.Add(Synthetic.Ev(s.AddSeconds(10), Acts.Value, "New Order", "Customer", name, src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 3}"));
                input.Events.Add(Synthetic.Ev(s.AddSeconds(15), Acts.Value, "New Order", "Amount", amount, src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 4}"));
                input.Events.Add(Synthetic.Ev(s.AddSeconds(20), Acts.Invoke, "New Order", "Save", ct: "Button", src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 5}"));
                var diff = new DiffResult();
                diff.Tables.Add(Synthetic.Added("app.db", "orders", Synthetic.Row(("id", (++id).ToString()), ("customer", name), ("amount", amount + ".00"), ("status", "open"))));
                input.Effects.Add(Synthetic.Effect(s.AddSeconds(22), "New Order", diff, $"fx-{d}-{k}.json"));
                input.Events.Add(Synthetic.Ev(s.AddSeconds(30), Acts.Foreground, "Main", "Main", ct: "Window", src: $"events/{day:yyyy-MM-dd}.jsonl#L{k * 6 + 6}"));
            }
        }
        input.Latest = Synthetic.Snapshot("app.db", ("orders", Synthetic.Table(new[] { "id", "customer", "amount", "status" }, new[] { "id" },
            new[] { "1", "A", "10.00", "open" }, new[] { "2", "B", "20.00", "paid" }, new[] { "3", "C", "30.00", "open" }, new[] { "4", "D", "40.00", "paid" }, new[] { "5", "E", "50.00", "open" })));
        return input;
    }

    [Fact]
    public void Learn_twice_gives_identical_memory()
    {
        var a = MemoryWriter.Render(Learner.Learn(Build(3)));
        var b = MemoryWriter.Render(Learner.Learn(Build(3)));
        foreach (var name in MemoryWriter.Files) Assert.Equal(Strip(a[name]), Strip(b[name]));
    }

    [Fact]
    public void Incremental_run_matches_full_rebuild()
    {
        var full = Learner.Learn(Build(3));
        var day1 = Build(1);
        var r1 = Learner.Learn(day1, new LearnState(), out var state);
        Assert.NotNull(state.Watermark);
        Assert.Equal(4, state.EffectFiles.Count);
        var r2 = Learner.Learn(Build(3), state, out var state2);
        Assert.Equal(12, state2.EffectFiles.Count);
        var fullText = MemoryWriter.Render(full);
        var incText = MemoryWriter.Render(r2);
        foreach (var name in MemoryWriter.Files) Assert.Equal(Strip(fullText[name]), Strip(incText[name]));
        Assert.Equal(full.FieldMappings.Count, r2.FieldMappings.Count);
        Assert.Equal(full.Flows.Single().Count, r2.Flows.Single().Count);
        Assert.True(r2.Incremental);
        Assert.False(r1.Incremental);
    }

    [Fact]
    public void Open_session_is_not_committed_until_it_closes()
    {
        var input = Build(1);
        input.Now = input.Events.Max(e => e.T).AddMinutes(5); // last session still open
        var r = Learner.Learn(input, new LearnState(), out var state);
        Assert.Null(state.Watermark);
        Assert.Equal(1, r.SessionCount); // still reported in the result
        Assert.Equal(4, r.EffectCount);
    }

    [Fact]
    public void Mapping_needs_three_distinct_values_or_sql_to_be_seen()
    {
        var two = Learner.Learn(Build(1));
        var customer = Assert.Single(two.FieldMappings, m => m.Field == "Customer");
        Assert.Equal(4, customer.ValueHashes.Count); // 4 saves with 4 distinct names
        Assert.True(customer.IsSeen);

        var input = Build(1);
        input.Effects = input.Effects.Take(2).ToList(); // only two observations
        var r = Learner.Learn(input);
        var c2 = Assert.Single(r.FieldMappings, m => m.Field == "Customer");
        Assert.False(c2.IsSeen);
        Assert.Equal("candidate", c2.Status);
        Assert.Contains("candidate", MemoryWriter.Render(r)["screens.md"]);

        // one captured SQL statement makes it seen and outranks a conflicting correlation
        input.SqlStatements.Add(new SqlStatement
        {
            T = input.Effects[0].T.AddMilliseconds(-500), Kind = "insert", Table = "orders", Columns = new() { "customer", "amount" },
            Values = new() { ["customer"] = "Customer 0-0", ["amount"] = "50" }, Source = "sql/x.xel#1", Text = "INSERT ...",
        });
        var r3 = Learner.Learn(input);
        var c3 = Assert.Single(r3.FieldMappings, m => m.Field == "Customer");
        Assert.True(c3.IsSeen);
        Assert.Equal(1, c3.SqlSeen);
        Assert.Equal("app.db / orders", c3.Table); // sql table name resolved to the snapshot's qualified name
        Assert.Contains(r3.ScreenWrites, w => w.Screen == "New Order" && w.Table == "app.db / orders" && w.SqlCount == 1);
        Assert.Single(r3.SqlWrites);
    }

    [Fact]
    public void State_round_trips_through_json()
    {
        var input = Build(2);
        Learner.Learn(input, new LearnState(), out var state);
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "learn-state.json");
            state.Save(path);
            var loaded = LearnState.Load(path);
            Assert.Equal(state.Watermark, loaded.Watermark);
            Assert.Equal(state.Mappings.Count, loaded.Mappings.Count);
            Assert.Equal(state.Flows.Values.First().Durations.Count, loaded.Flows.Values.First().Durations.Count);
            var a = MemoryWriter.Render(Learner.Learn(Build(2), state, out _));
            var b = MemoryWriter.Render(Learner.Learn(Build(2), loaded, out _));
            Assert.Equal(Strip(a["screens.md"]), Strip(b["screens.md"]));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Health_page_reports_counts_and_advice()
    {
        var r = Learner.Learn(Build(2));
        Assert.Equal(2, r.Health.Days);
        Assert.Equal(8, r.Health.TriggerInvokes);
        Assert.Equal(100, r.Health.SavesWithDiffPct);
        Assert.Equal(2, r.Health.FieldsSeen);
        Assert.Equal(2, r.Health.FieldsMapped);
        var md = MemoryWriter.Render(r)["health.md"];
        Assert.Contains("Saves with a data diff | 100%", md);
        Assert.Contains("day", md);
    }

    [Fact]
    public void Value_debouncer_commits_one_event_per_typed_value()
    {
        var d = new ValueDebouncer();
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        d.Push(Synthetic.Ev(t0, Acts.Value, "Form", "Name", "a"), t0);
        d.Push(Synthetic.Ev(t0.AddMilliseconds(200), Acts.Value, "Form", "Name", "ab"), t0.AddMilliseconds(200));
        d.Push(Synthetic.Ev(t0.AddMilliseconds(400), Acts.Value, "Form", "Name", "abc"), t0.AddMilliseconds(400));
        Assert.Empty(d.Flush(t0.AddMilliseconds(900)));          // only 500 ms since the last change
        var flushed = d.Flush(t0.AddMilliseconds(1300));
        var e = Assert.Single(flushed);
        Assert.Equal("abc", e.Val);
        Assert.Equal(0, d.PendingCount);

        d.Push(Synthetic.Ev(t0.AddSeconds(5), Acts.Value, "Form", "City", "Os"), t0.AddSeconds(5));
        var onFocusLeave = d.Flush(t0.AddSeconds(5).AddMilliseconds(10), all: true);
        Assert.Equal("Os", Assert.Single(onFocusLeave).Val);
    }

    [Fact]
    public void Xes_log_has_one_trace_per_session_and_opens_as_xml()
    {
        var input = Build(2);
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-xes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "events.xes");
            var traces = XesWriter.Write(path, input.Events, input.Effects, "demo");
            Assert.Equal(2, traces);
            var doc = XDocument.Load(path);
            XNamespace ns = "http://www.xes-standard.org/";
            var traceElems = doc.Root!.Elements(ns + "trace").ToList();
            Assert.Equal(2, traceElems.Count);
            var events = traceElems[0].Elements(ns + "event").ToList();
            Assert.Contains(events, e => e.Elements(ns + "string").Any(s => s.Attribute("key")!.Value == "concept:name" && s.Attribute("value")!.Value == "New Order"));
            Assert.Contains(events, e => e.Elements(ns + "string").Any(s => s.Attribute("key")!.Value == "concept:name" && s.Attribute("value")!.Value.StartsWith("save:")));
            Assert.All(events, e => Assert.Contains(e.Elements(ns + "date"), x => x.Attribute("key")!.Value == "time:timestamp"));
            Assert.DoesNotContain("Customer 0-0", File.ReadAllText(path)); // no typed values in the log
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string Strip(string md) => string.Join("\n", md.Split('\n').Where(l => !l.StartsWith("Last learn:")));
}
