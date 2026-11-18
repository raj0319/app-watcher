using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;
using Xunit;

namespace AppWatcher.Tests;

public class FieldMapperTests
{
    [Fact]
    public void Typed_values_found_in_changed_columns_become_mappings_with_counts()
    {
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        var events = new List<UiEvent>();
        var effects = new List<EffectRecord>();
        for (var i = 0; i < 3; i++)
        {
            var t = t0.AddMinutes(i * 5);
            var name = "Customer " + i;
            events.Add(Synthetic.Ev(t, Acts.Value, "New Customer", "Name", name));
            events.Add(Synthetic.Ev(t.AddSeconds(5), Acts.Value, "New Customer", "City", "Oslo"));
            events.Add(Synthetic.Ev(t.AddSeconds(6), Acts.Value, "New Customer", "Password", "***"));
            events.Add(Synthetic.Ev(t.AddSeconds(10), Acts.Invoke, "New Customer", "Save", ct: "Button"));
            var diff = new DiffResult();
            diff.Tables.Add(Synthetic.Added("app.db", "customers", Synthetic.Row(("id", (100 + i).ToString()), ("full_name", name), ("city", "OSLO"), ("pw", "***"))));
            diff.Tables.Add(Synthetic.Added("app.db", "audit", Synthetic.Row(("id", (1 + i).ToString()), ("what", "insert customers"))));
            effects.Add(Synthetic.Effect(t.AddSeconds(12), "New Customer", diff, $"fx{i}.json"));
        }

        var (mappings, writes) = FieldMapper.Learn(events, effects);

        var nameMap = Assert.Single(mappings, m => m.Field == "Name");
        Assert.Equal("app.db / customers", nameMap.Table);
        Assert.Equal("full_name", nameMap.Column);
        Assert.Equal(3, nameMap.Evidence.Seen);
        Assert.Contains("effects/fx0.json", nameMap.Evidence.Files);
        var cityMap = Assert.Single(mappings, m => m.Field == "City");
        Assert.Equal("city", cityMap.Column); // case-insensitive match
        Assert.DoesNotContain(mappings, m => m.Field == "Password"); // masked values never map

        Assert.Equal(2, writes.Count);
        Assert.All(writes, w => { Assert.True(w.Always); Assert.Equal(3, w.Count); Assert.Equal(3, w.ScreenEffects); });
    }

    [Fact]
    public void Values_typed_on_another_screen_or_too_long_ago_do_not_count()
    {
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        var events = new List<UiEvent>
        {
            Synthetic.Ev(t0.AddMinutes(-30), Acts.Value, "Invoice", "Amount", "250"),
            Synthetic.Ev(t0, Acts.Value, "Other Screen", "Amount", "250"),
        };
        var diff = new DiffResult();
        diff.Tables.Add(Synthetic.Added("app.db", "invoices", Synthetic.Row(("id", "1"), ("amount", "250.00"))));
        var effects = new List<EffectRecord> { Synthetic.Effect(t0.AddSeconds(2), "Invoice", diff) };
        var (mappings, writes) = FieldMapper.Learn(events, effects);
        Assert.Empty(mappings);
        var w = Assert.Single(writes);
        Assert.False(w.Always); // only one save seen: not enough to say "always"
    }

    [Fact]
    public void Numeric_values_match_regardless_of_formatting()
    {
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
        var events = new List<UiEvent> { Synthetic.Ev(t0, Acts.Value, "Invoice", "Amount", "1,250.5") };
        var diff = new DiffResult();
        diff.Tables.Add(Synthetic.Added("app.db", "invoices", Synthetic.Row(("id", "1"), ("amount", "1250.50"))));
        var (mappings, _) = FieldMapper.Learn(events, new List<EffectRecord> { Synthetic.Effect(t0.AddSeconds(2), "Invoice", diff) });
        var m = Assert.Single(mappings);
        Assert.Equal("amount", m.Column);
    }
}
