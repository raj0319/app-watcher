using AppWatcher.Core.Learning;
using AppWatcher.Core.Snapshots;
using Xunit;

namespace AppWatcher.Tests;

public class FormulaFitterTests
{
    private static readonly string[] LineCols = { "id", "order_id", "qty", "price", "line_total" };
    private static readonly string[] OrderCols = { "id", "subtotal", "tax", "total" };

    private static DataSnapshot Shop()
    {
        var lines = new List<string?[]>();
        var orders = new List<string?[]>();
        var rnd = new Random(7);
        var lineId = 1;
        for (var o = 1; o <= 12; o++)
        {
            decimal subtotal = 0;
            for (var l = 0; l < 3; l++)
            {
                var qty = rnd.Next(1, 9);
                var price = Math.Round((decimal)rnd.NextDouble() * 90 + 1, 2);
                var lt = Math.Round(qty * price, 2);
                subtotal += lt;
                lines.Add(new[] { (lineId++).ToString(), o.ToString(), qty.ToString(), price.ToString("0.00"), lt.ToString("0.00") });
            }
            var tax = Math.Round(subtotal * 0.25m, 2);
            orders.Add(new[] { o.ToString(), subtotal.ToString("0.00"), tax.ToString("0.00"), (subtotal + tax).ToString("0.00") });
        }
        return Synthetic.Snapshot("shop.db",
            ("order_lines", Synthetic.Table(LineCols, new[] { "id" }, lines.ToArray())),
            ("orders", Synthetic.Table(OrderCols, new[] { "id" }, orders.ToArray())));
    }

    [Fact]
    public void Finds_product_rate_sum_and_addition_formulas()
    {
        var snap = Shop();
        var relations = RelationFinder.Learn(snap, new List<EffectRecord>());
        Assert.Contains(relations, r => r.FromTable == "shop.db / order_lines" && r.FromColumn == "order_id" && r.ToTable == "shop.db / orders");

        var formulas = FormulaFitter.Learn(snap, new List<EffectRecord>(), new List<FieldMapping>(), relations);

        // inverse readings (qty = line_total / price) are dropped when the forward formula exists
        Assert.Single(formulas, f => f.Table == "shop.db / order_lines");
        var lineTotal = Assert.Single(formulas, f => f.Table == "shop.db / order_lines" && f.Column == "line_total");
        Assert.Equal("line_total = qty × price", lineTotal.Expression);
        Assert.Equal(36, lineTotal.Explained);
        Assert.Equal(36, lineTotal.Tested);
        Assert.True(lineTotal.Rounded2);

        var tax = Assert.Single(formulas, f => f.Table == "shop.db / orders" && f.Column == "tax");
        // tax = 25% of subtotal is also 20% of total: both are true, one is the headline, the other an alternative
        var taxForms = new[] { tax.Expression }.Concat(tax.Alternatives).ToList();
        Assert.Contains("tax = subtotal × 0.25", taxForms);
        Assert.Contains("tax = total × 0.2", taxForms);
        Assert.Equal(12, tax.Explained);

        var total = Assert.Single(formulas, f => f.Table == "shop.db / orders" && f.Column == "total");
        Assert.Equal("total = subtotal + tax", total.Expression);
        Assert.Contains("total = subtotal × (1 + 0.25)", total.Alternatives);

        var subtotal = Assert.Single(formulas, f => f.Table == "shop.db / orders" && f.Column == "subtotal");
        Assert.Equal("subtotal = sum of shop.db / order_lines.line_total where order_id = id", subtotal.Expression);
        Assert.Equal(12, subtotal.Explained);
        Assert.True(subtotal.Evidence.Inferred); // no effect showed it changing untyped
    }

    [Fact]
    public void Untyped_changes_in_effects_make_the_formula_seen_not_inferred()
    {
        var snap = Shop();
        var relations = RelationFinder.Learn(snap, new List<EffectRecord>());
        var td = new TableDiff { Db = "shop.db", Table = "orders" };
        td.Changed["1"] = new Dictionary<string, ChangedValue> { ["tax"] = new() { Old = "1", New = "2" }, ["total"] = new() { Old = "5", New = "6" } };
        td.ChangedRows["1"] = Synthetic.Row(("id", "1"), ("subtotal", "8"), ("tax", "2"), ("total", "6"));
        var diff = new DiffResult(); diff.Tables.Add(td);
        var effects = new List<EffectRecord> { Synthetic.Effect(DateTime.Now, "Order", diff, "fx1.json") };

        var formulas = FormulaFitter.Learn(snap, effects, new List<FieldMapping>(), relations);
        var tax = Assert.Single(formulas, f => f.Column == "tax");
        Assert.False(tax.Evidence.Inferred);
        Assert.Equal(1, tax.UntypedChanges);
        Assert.Contains("effects/fx1.json", tax.Evidence.Files);
    }

    [Fact]
    public void Random_columns_do_not_get_a_formula()
    {
        var rnd = new Random(3);
        var rows = Enumerable.Range(1, 30).Select(i => new[] { i.ToString(), rnd.Next(1, 100).ToString(), rnd.Next(1, 100).ToString(), rnd.Next(1, 1000).ToString() }).ToArray();
        var snap = Synthetic.Snapshot("x.db", ("t", Synthetic.Table(new[] { "id", "a", "b", "c" }, new[] { "id" }, rows)));
        var formulas = FormulaFitter.Learn(snap, new List<EffectRecord>(), new List<FieldMapping>(), new List<Relation>());
        Assert.Empty(formulas);
    }

    [Fact]
    public void Close_tolerates_two_decimal_rounding()
    {
        Assert.True(FormulaFitter.Close(12.345m, 12.35m));
        Assert.True(FormulaFitter.Close(12.344m, 12.34m));
        Assert.False(FormulaFitter.Close(12.3m, 12.4m));
    }
}
