using AppWatcher.Core.Learning;
using AppWatcher.Core.Snapshots;
using Xunit;

namespace AppWatcher.Tests;

public class StatusFlowTests
{
    private static EffectRecord Change(DateTime t, string key, string from, string to, string file)
    {
        var td = new TableDiff { Db = "app.db", Table = "orders" };
        td.Changed[key] = new Dictionary<string, ChangedValue> { ["status"] = new() { Old = from, New = to } };
        td.ChangedRows[key] = Synthetic.Row(("id", key), ("status", to));
        var d = new DiffResult(); d.Tables.Add(td);
        return Synthetic.Effect(t, "Order", d, file);
    }

    private static EffectRecord Insert(DateTime t, string key, string status, string file)
    {
        var d = new DiffResult();
        d.Tables.Add(Synthetic.Added("app.db", "orders", Synthetic.Row(("id", key), ("status", status), ("amount", "12.50"))));
        return Synthetic.Effect(t, "Order", d, file);
    }

    [Fact]
    public void Orders_transitions_by_time_and_lists_never_seen_ones()
    {
        var t0 = new DateTime(2026, 1, 1);
        var snap = Synthetic.Snapshot("app.db", ("orders", Synthetic.Table(new[] { "id", "status", "amount" }, new[] { "id" },
            new[] { "1", "paid", "1.5" }, new[] { "2", "open", "2.5" }, new[] { "3", "open", "3.5" }, new[] { "4", "shipped", "4.5" }, new[] { "5", "paid", "5.5" }, new[] { "6", "open", "6.5" })));
        var effects = new List<EffectRecord>
        {
            Insert(t0, "7", "open", "a.json"),
            Change(t0.AddMinutes(1), "7", "open", "paid", "b.json"),
            Change(t0.AddMinutes(2), "7", "paid", "shipped", "c.json"),
            Change(t0.AddMinutes(3), "2", "open", "paid", "d.json"),
            Change(t0.AddMinutes(4), "3", "open", "cancelled", "e.json"),
        };

        var flows = StatusFlowLearner.Learn(snap, effects);

        var f = Assert.Single(flows);
        Assert.Equal("status", f.Column);
        Assert.Equal(new[] { "open", "paid", "shipped", "cancelled" }, f.States);
        Assert.Equal(1, f.Initial["open"]);
        var openPaid = Assert.Single(f.Transitions, t => t.From == "open" && t.To == "paid");
        Assert.Equal(2, openPaid.Evidence.Seen);
        Assert.Contains("effects/b.json", openPaid.Evidence.Files);
        Assert.Contains(("shipped", "open"), f.NeverSeen);
        Assert.DoesNotContain(("open", "paid"), f.NeverSeen);
        Assert.False(f.Evidence.Inferred);
        Assert.DoesNotContain(flows, x => x.Column == "amount"); // free decimals are not a status
    }

    [Fact]
    public void Small_domain_column_in_snapshot_only_is_inferred()
    {
        var rows = Enumerable.Range(1, 10).Select(i => new[] { i.ToString(), i % 2 == 0 ? "active" : "closed" }).ToArray();
        var snap = Synthetic.Snapshot("app.db", ("accounts", Synthetic.Table(new[] { "id", "state" }, new[] { "id" }, rows)));
        var f = Assert.Single(StatusFlowLearner.Learn(snap, new List<EffectRecord>()));
        Assert.True(f.Evidence.Inferred);
        Assert.Empty(f.Transitions);
        Assert.Empty(f.NeverSeen);
        Assert.Equal(2, f.States.Count);
    }
}
