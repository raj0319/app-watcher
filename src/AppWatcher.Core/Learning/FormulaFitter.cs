using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Tests simple formulas (a+b, a×b, a×rate, sum of child rows, ...) against the rows we have.</summary>
public static class FormulaFitter
{
    public static readonly decimal[] Rates = { 0.05m, 0.0625m, 0.07m, 0.075m, 0.08m, 0.0825m, 0.09m, 0.1m, 0.12m, 0.125m, 0.13m, 0.15m, 0.18m, 0.2m, 0.21m, 0.25m, 0.3m, 0.5m };
    private const int MaxRows = 3000;
    private const int MaxNumericColumns = 15;

    public static List<Formula> Learn(DataSnapshot? latest, List<EffectRecord> effects, List<FieldMapping> mappings, List<Relation> relations)
    {
        var result = new List<Formula>();
        if (latest is null) return result;

        var tables = new Dictionary<string, TableSnapshot>();
        foreach (var (db, ts) in latest.Databases)
            foreach (var (name, t) in ts)
                if (!t.Skipped && t.Rows.Count >= 2) tables[RelationFinder.Qualified(db, name)] = t;

        // columns that changed in effects without a typed value for them
        var untyped = new Dictionary<(string, string), Evidence>();
        var mappedCols = new HashSet<(string, string)>(mappings.Select(m => (m.Table, m.Column)));
        foreach (var fx in effects)
            foreach (var td in fx.Diff.Tables)
            {
                var cols = td.Changed.Values.SelectMany(c => c.Keys).Distinct();
                foreach (var c in cols)
                {
                    if (mappedCols.Contains((td.QualifiedName, c))) continue;
                    if (!untyped.TryGetValue((td.QualifiedName, c), out var ev)) untyped[(td.QualifiedName, c)] = ev = new Evidence();
                    ev.Add(fx.Source);
                }
            }

        foreach (var (q, t) in tables)
        {
            var rows = t.Rows.Values.Take(MaxRows).ToList();
            var numeric = NumericColumns(t, rows).Take(MaxNumericColumns).ToList();
            if (numeric.Count == 0) continue;
            var parsed = numeric.ToDictionary(c => c, c => rows.Select(r => r.TryGetValue(c, out var v) && Values.TryDecimal(v, out var d) ? d : (decimal?)null).ToArray());
            var tableFormulas = new List<(Formula f, Candidate best)>();

            foreach (var y in numeric)
            {
                var ys = parsed[y];
                var candidates = new List<Candidate>();
                candidates.AddRange(FitBinary(y, ys, numeric, parsed));
                candidates.AddRange(FitRate(y, ys, numeric, parsed));
                candidates.AddRange(FitChildren(q, t, y, relations, tables));
                // best = most rows explained; on a tie prefer sum of children, then a+b / a×b, then rates, then a-b / a÷b
                var best = candidates.Where(c => c.Tested >= 2).OrderByDescending(c => c.Explained).ThenBy(c => c.Rank).ThenBy(c => c.Expr.Length).FirstOrDefault();
                if (best is null) continue;
                var (expr, explained, tested) = (best.Expr, best.Explained, best.Tested);
                if (explained < 2 || (double)explained / tested < 0.6) continue;
                var f = new Formula { Table = q, Column = y, Expression = expr, Explained = explained, Tested = tested, Rounded2 = AllTwoDecimals(ys) };
                f.Alternatives = candidates.Where(c => c != best && c.Explained == explained && c.Tested >= 2).OrderBy(c => c.Rank).Select(c => c.Expr).Take(3).ToList();
                f.Evidence.Inferred = !untyped.ContainsKey((q, y));
                f.Evidence.Files.Add("snapshots/before.json");
                if (untyped.TryGetValue((q, y), out var ev))
                {
                    f.UntypedChanges = ev.Seen;
                    foreach (var file in ev.Files) f.Evidence.Add(file, 0);
                    f.Evidence.Seen = ev.Seen;
                }
                tableFormulas.Add((f, best));
            }

            // drop inverse readings: when total = qty × price is known, "qty = total / price" adds nothing (unless qty itself was seen changing untyped)
            foreach (var (f, best) in tableFormulas)
            {
                if (best.Rank == 4 && f.UntypedChanges == 0 &&
                    tableFormulas.Any(o => o.best.Rank <= 3 && o.best.Operands.Contains(f.Column) && best.Operands.Contains(o.f.Column)))
                    continue;
                result.Add(f);
            }
        }
        return result.OrderBy(f => f.Table).ThenBy(f => f.Column).ToList();
    }

    public static List<string> NumericColumns(TableSnapshot t, List<Dictionary<string, string?>> rows)
    {
        var list = new List<string>();
        foreach (var c in t.Columns)
        {
            if (t.PrimaryKey.Count == 1 && t.PrimaryKey[0] == c) continue;
            if (RelationFinder.LooksLikeId(c)) continue;
            var cl = c.ToLowerInvariant();
            if (cl.Contains("date") || cl.Contains("time") || cl.Contains("phone") || cl.Contains("zip") || cl.Contains("postal")) continue;
            int nonNull = 0, numeric = 0;
            var distinct = new HashSet<decimal>();
            foreach (var r in rows)
            {
                if (!r.TryGetValue(c, out var v) || string.IsNullOrEmpty(v)) continue;
                nonNull++;
                if (Values.TryDecimal(v, out var d)) { numeric++; distinct.Add(d); }
            }
            if (nonNull >= 2 && numeric >= nonNull * 0.8 && distinct.Count >= 2) list.Add(c);
        }
        return list;
    }

    internal sealed record Candidate(string Expr, int Explained, int Tested, int Rank, params string[] Operands);

    private static List<Candidate> FitBinary(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed)
    {
        var list = new List<Candidate>();
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i] == y) continue;
            for (var j = i + 1; j < cols.Count; j++)
            {
                if (cols[j] == y) continue;
                var a = parsed[cols[i]]; var b = parsed[cols[j]];
                foreach (var (name, fn) in Ops)
                {
                    int explained = 0, tested = 0;
                    var da = new HashSet<decimal>(); var db = new HashSet<decimal>();
                    for (var r = 0; r < ys.Length; r++)
                    {
                        if (ys[r] is null || a[r] is null || b[r] is null) continue;
                        var v = fn(a[r]!.Value, b[r]!.Value);
                        if (v is null) continue;
                        tested++;
                        da.Add(a[r]!.Value); db.Add(b[r]!.Value);
                        if (Close(v.Value, ys[r]!.Value)) explained++;
                    }
                    if (da.Count < 2 || db.Count < 2 || explained == 0) continue;
                    list.Add(new Candidate($"{y} = {Fmt(name, cols[i], cols[j])}", explained, tested, name is "+" or "*" ? 2 : 4, cols[i], cols[j]));
                }
            }
        }
        return list;
    }

    private static readonly (string name, Func<decimal, decimal, decimal?> fn)[] Ops =
    {
        ("+", (a, b) => a + b),
        ("-", (a, b) => a - b),
        ("r-", (a, b) => b - a),
        ("*", (a, b) => a * b),
        ("/", (a, b) => b == 0 ? null : a / b),
        ("r/", (a, b) => a == 0 ? null : b / a),
    };

    private static string Fmt(string op, string a, string b) => op switch
    {
        "+" => $"{a} + {b}",
        "-" => $"{a} - {b}",
        "r-" => $"{b} - {a}",
        "*" => $"{a} × {b}",
        "/" => $"{a} / {b}",
        "r/" => $"{b} / {a}",
        _ => $"{a} ? {b}"
    };

    private static List<Candidate> FitRate(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed)
    {
        var list = new List<Candidate>();
        foreach (var c in cols)
        {
            if (c == y) continue;
            var a = parsed[c];
            foreach (var r in Rates)
            {
                var forms = new (string expr, Func<decimal, decimal?> fn)[]
                {
                    ($"{y} = {c} × {r:0.####}", v => v * r),
                    ($"{y} = {c} × (1 + {r:0.####})", v => v * (1 + r)),
                    ($"{y} = {c} × (1 - {r:0.####})", v => v * (1 - r)),
                    ($"{y} = {c} / (1 + {r:0.####})", v => v / (1 + r)),
                };
                foreach (var (expr, fn) in forms)
                {
                    int explained = 0, tested = 0;
                    var distinct = new HashSet<decimal>();
                    for (var i = 0; i < ys.Length; i++)
                    {
                        if (ys[i] is null || a[i] is null || a[i] == 0) continue;
                        tested++;
                        distinct.Add(a[i]!.Value);
                        var v = fn(a[i]!.Value);
                        if (v is not null && Close(v.Value, ys[i]!.Value)) explained++;
                    }
                    if (distinct.Count < 2 || explained == 0) continue;
                    list.Add(new Candidate(expr, explained, tested, 3, c));
                }
            }
        }
        return list;
    }

    private static List<Candidate> FitChildren(string q, TableSnapshot t, string y, List<Relation> relations, Dictionary<string, TableSnapshot> tables)
    {
        var list = new List<Candidate>();
        if (t.PrimaryKey.Count != 1) return list;
        var pk = t.PrimaryKey[0];
        foreach (var rel in relations.Where(r => r.ToTable == q && r.ToColumn == pk))
        {
            if (!tables.TryGetValue(rel.FromTable, out var child)) continue;
            var childRows = child.Rows.Values.ToList();
            var numeric = NumericColumns(child, childRows.Take(MaxRows).ToList());
            var groups = childRows.GroupBy(r => r.TryGetValue(rel.FromColumn, out var v) ? v ?? "" : "").ToDictionary(g => g.Key, g => g.ToList());
            var parentRows = t.Rows.Values.Take(MaxRows).Where(r => r.TryGetValue(pk, out var k) && k is not null && groups.ContainsKey(k)).ToList();
            if (parentRows.Count < 2) continue;

            // count(child)
            {
                int explained = 0, tested = 0;
                foreach (var pr in parentRows)
                {
                    if (!Values.TryDecimal(pr[y], out var yv)) continue;
                    tested++;
                    if (Close(groups[pr[pk]!].Count, yv)) explained++;
                }
                if (explained > 0) list.Add(new Candidate($"{y} = count of {rel.FromTable} rows where {rel.FromColumn} = {pk}", explained, tested, 1));
            }
            foreach (var x in numeric)
            {
                int explained = 0, tested = 0;
                foreach (var pr in parentRows)
                {
                    if (!Values.TryDecimal(pr[y], out var yv)) continue;
                    decimal sum = 0; var any = false;
                    foreach (var cr in groups[pr[pk]!])
                        if (cr.TryGetValue(x, out var xv) && Values.TryDecimal(xv, out var d)) { sum += d; any = true; }
                    if (!any) continue;
                    tested++;
                    if (Close(sum, yv)) explained++;
                }
                if (explained > 0) list.Add(new Candidate($"{y} = sum of {rel.FromTable}.{x} where {rel.FromColumn} = {pk}", explained, tested, 1));
            }
        }
        return list;
    }

    public static bool Close(decimal computed, decimal actual)
    {
        var r2 = Math.Round(computed, 2, MidpointRounding.AwayFromZero);
        var tol = 0.005m + Math.Abs(actual) * 0.000001m;
        return Math.Abs(r2 - actual) <= tol || Math.Abs(computed - actual) <= tol
            || Math.Abs(Math.Round(computed, 2, MidpointRounding.ToEven) - actual) <= tol;
    }

    private static bool AllTwoDecimals(decimal?[] ys)
    {
        var any = false;
        foreach (var y in ys)
        {
            if (y is null) continue;
            any = true;
            if (Math.Round(y.Value, 2) != y.Value) return false;
        }
        return any;
    }
}
