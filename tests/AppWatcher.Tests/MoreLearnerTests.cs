using AppWatcher.Core;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;
using Xunit;

namespace AppWatcher.Tests;

public class MoreLearnerTests
{
    [Fact]
    public void Sequences_are_found_from_effects_and_dense_snapshot_ranges()
    {
        var t0 = new DateTime(2026, 1, 1);
        var effects = new List<EffectRecord>();
        for (var i = 0; i < 4; i++)
        {
            var d = new DiffResult();
            d.Tables.Add(Synthetic.Added("app.db", "invoices", Synthetic.Row(("id", (i + 1).ToString()), ("number", $"INV-{1000 + i}"), ("note", "x"))));
            effects.Add(Synthetic.Effect(t0.AddMinutes(i), "Invoice", d, $"s{i}.json"));
        }
        var rows = Enumerable.Range(1, 8).Select(i => new[] { i.ToString(), $"INV-{999 + i}", "x" }).ToArray();
        var snap = Synthetic.Snapshot("app.db", ("invoices", Synthetic.Table(new[] { "id", "number", "note" }, new[] { "id" }, rows)));

        var seqs = SequenceLearner.Learn(snap, effects);

        var number = Assert.Single(seqs, s => s.Column == "number");
        Assert.Equal("INV-", number.Prefix);
        Assert.Equal(3, number.Evidence.Seen); // effects 2,3,4 continue the previous max by one
        Assert.True(number.Dense);
        Assert.False(number.Evidence.Inferred);
        var id = Assert.Single(seqs, s => s.Column == "id");
        Assert.True(id.Dense);
        Assert.DoesNotContain(seqs, s => s.Column == "note");

        // one "previous max + 1" pair on a column with repeated values is not a sequence
        var fx = new List<EffectRecord>();
        var d1 = new DiffResult(); d1.Tables.Add(Synthetic.Added("app.db", "orders", Synthetic.Row(("id", "7"), ("customer_id", "2"))));
        var d2 = new DiffResult(); d2.Tables.Add(Synthetic.Added("app.db", "orders", Synthetic.Row(("id", "8"), ("customer_id", "3"))));
        fx.Add(Synthetic.Effect(t0, "Order", d1, "a.json")); fx.Add(Synthetic.Effect(t0.AddMinutes(1), "Order", d2, "b.json"));
        var orderRows = Enumerable.Range(1, 8).Select(i => new[] { i.ToString(), (i % 5 + 1).ToString() }).ToArray();
        var snap2 = Synthetic.Snapshot("app.db", ("orders", Synthetic.Table(new[] { "id", "customer_id" }, new[] { "id" }, orderRows)));
        var seqs2 = SequenceLearner.Learn(snap2, fx);
        Assert.DoesNotContain(seqs2, s => s.Column == "customer_id");
        Assert.Contains(seqs2, s => s.Column == "id");
    }

    [Fact]
    public void Validation_rule_from_dialog_after_value_change()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);
        var events = new List<UiEvent>
        {
            Synthetic.Ev(t0, Acts.Value, "Customer", "Email", "not-an-email", src: "events/a.jsonl#L1"),
            Synthetic.Ev(t0.AddSeconds(1), Acts.Dialog, "Customer", "Error", "Please enter a valid e-mail address", ct: "Window", src: "events/a.jsonl#L2"),
            Synthetic.Ev(t0.AddMinutes(5), Acts.Value, "Customer", "Email", "still-wrong", src: "events/a.jsonl#L3"),
            Synthetic.Ev(t0.AddMinutes(5).AddSeconds(2), Acts.Dialog, "Customer", "Error", "Please enter a valid e-mail address", ct: "Window", src: "events/a.jsonl#L4"),
            Synthetic.Ev(t0.AddMinutes(9), Acts.Dialog, "Customer", "Info", "Saved", ct: "Window", src: "events/a.jsonl#L5"), // no cause nearby
        };
        var rules = ValidationRuleLearner.Learn(events);
        var r = Assert.Single(rules);
        Assert.Equal("Email", r.Field);
        Assert.Equal("mixed", r.Pattern);
        Assert.Equal("Please enter a valid e-mail address", r.Message);
        Assert.Equal(2, r.Evidence.Seen);
        Assert.Equal("events/a.jsonl#L2", r.Evidence.Files[0]);
    }

    [Fact]
    public void Flows_group_window_sequences_with_counts_median_and_back_step()
    {
        var t = new DateTime(2026, 1, 1, 8, 0, 0);
        var events = new List<UiEvent>();
        void Visit(string win, int seconds, string? control = null)
        {
            events.Add(Synthetic.Ev(t, Acts.Foreground, win, win, ct: "Window"));
            if (control is not null) events.Add(Synthetic.Ev(t.AddSeconds(1), Acts.Invoke, win, control, ct: "Button"));
            t = t.AddSeconds(seconds);
        }
        for (var i = 0; i < 3; i++)
        {
            Visit("Main", 60);
            Visit("New Order", 30 + i * 10, "Add line");
            Visit("Pick Item", 10);
            Visit("New Order", 5, "Save");
            Visit("Main", 60);
        }
        Visit("Reports", 20);
        Visit("Main", 10);

        var (flows, nav, main, sessions) = FlowLearner.Learn(events);
        Assert.Equal("Main", main);
        Assert.Equal(1, sessions);
        var order = Assert.Single(flows, f => f.Key == "New Order");
        Assert.Equal(3, order.Count);
        Assert.Equal(new[] { "New Order", "Pick Item", "New Order" }, order.CommonPath);
        Assert.Equal("Pick Item", order.BackFrom);
        Assert.Equal(3, order.BackFromCount);
        Assert.InRange(order.MedianMs, 50_000, 52_000); // durations 41 s, 51 s, 61 s
        Assert.Contains(order.StepControls["New Order"], c => c.name == "Save" && c.count == 3);
        Assert.Single(flows, f => f.Key == "Reports");
        Assert.Contains(nav, e => e.From == "Main" && e.To == "New Order" && e.Count == 3);
    }

    [Fact]
    public void Memory_files_carry_confidence_and_no_raw_values()
    {
        var input = new LearnInput { AppName = "demo" };
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        input.Events.Add(Synthetic.Ev(t0, Acts.Value, "New Customer", "Name", "Secret Person Name"));
        input.Events.Add(Synthetic.Ev(t0.AddSeconds(3), Acts.Invoke, "New Customer", "Save", ct: "Button"));
        var diff = new DiffResult();
        diff.Tables.Add(Synthetic.Added("app.db", "customers", Synthetic.Row(("id", "1"), ("name", "Secret Person Name"))));
        input.Effects.Add(Synthetic.Effect(t0.AddSeconds(5), "New Customer", diff, "fx.json"));
        input.Latest = Synthetic.Snapshot("app.db", ("customers", Synthetic.Table(new[] { "id", "name" }, new[] { "id" }, new[] { "1", "Secret Person Name" })));

        var result = Learner.Learn(input);
        var files = MemoryWriter.Render(result);
        Assert.Equal(5, files.Count);
        foreach (var (name, text) in files)
        {
            Assert.DoesNotContain("Secret Person", text);
            Assert.True(text.Contains("seen ") || text.Contains("inferred"), name + " has no confidence tag");
        }
        Assert.Contains("New Customer › Name (seen 1×)", files["data-model.md"]);
        Assert.Contains("effects/fx.json", files["screens.md"]);

        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-mem-" + Guid.NewGuid().ToString("N"));
        try
        {
            var changes = MemoryWriter.Write(dir, result);
            Assert.Equal(5, changes.Count);
            Assert.True(File.Exists(Path.Combine(dir, "changelog.md")));
            var again = MemoryWriter.Write(dir, result);
            Assert.Empty(again);
            Assert.Contains("No changes since the last learn.", File.ReadAllText(Path.Combine(dir, "changelog.md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Masking_hides_passwords_and_personal_values()
    {
        Assert.Equal("***", Masking.ForEvent("hunter2", "Password", "", false));
        Assert.Equal("***", Masking.ForEvent("1234", "Enter PIN", "", false));
        Assert.Equal("***", Masking.ForEvent("x", "Name", "pwdBox", true));
        Assert.Equal("Ann", Masking.ForEvent("Ann", "Name", "", false));
        Assert.Equal("<email>", Masking.ForExport("a@b.com"));
        Assert.Equal("<number>", Masking.ForExport("4111111111111111"));
        Assert.Equal("Oslo", Masking.ForExport("Oslo"));
        Assert.Equal("Aaa-####", Masking.Shape("Abc-1234"));
    }

    [Fact]
    public void Password_record_round_trips_through_dpapi()
    {
        var record = PasswordHasher.CreateRecord("open sesame");
        Assert.True(PasswordHasher.Verify(record, "open sesame"));
        Assert.False(PasswordHasher.Verify(record, "open sesam"));
        var blob = Dpapi.Protect(record);
        Assert.NotEqual(record, blob);
        Assert.Equal(record, Dpapi.Unprotect(blob));
    }

    [Fact]
    public void Title_keys_collapse_numbers_and_dirty_markers()
    {
        Assert.Equal("Order # - Draft", Titles.Key("Order 123 - *Draft"));
        Assert.Equal("Untitled - Notepad", Titles.Key("*Untitled - Notepad"));
        Assert.Equal(Titles.Key("Invoice 17"), Titles.Key("Invoice 2041"));
    }
}
