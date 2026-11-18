using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>
/// Explains calculated columns. Candidates are tried in order and the first stage that explains at least
/// 95% of the rows wins: identity copy, sum of child rows, product of two columns, percentage of a column
/// (rates seen in the data plus common tax rates), then a least-squares linear fit over up to 3 columns.
/// Rows the formula does not explain are reported as exceptions.
/// </summary>
public static class FormulaFitter
{
    public static readonly decimal[] CommonRates = { 0.05m, 0.0625m, 0.07m, 0.075m, 0.08m, 0.0825m, 0.09m, 0.1m, 0.12m, 0.125m, 0.13m, 0.15m, 0.18m, 0.2m, 0.21m, 0.25m, 0.3m, 0.5m };
    public const double MinExplained = 0.95;
    private const int MaxRows = 3000;
    private const int MaxNumericColumns = 15;
    private const int MaxLinearColumns = 8;

    internal sealed record Candidate(string Expr, int Explained, int Tested, string Method, List<string> ResidualKeys, params string[] Operands);

    /// <summary>Whole-history wrapper (tests): untyped evidence comes from effects whose changed columns had no typed match.</summary>
    public static List<Formula> Learn(DataSnapshot? latest, List<EffectRecord> effects, List<FieldMapping> mappings, List<Relation> relations)
    {
        var state = new LearnState();
        var mapped = new HashSet<string>(mappings.Select(m => LearnState.Key(m.Table, m.Column)));
        foreach (var fx in effects)
            foreach (var td in fx.Diff.Tables)
                foreach (var c in td.Changed.Values.SelectMany(f => f.Keys).Distinct())
                {
                    var key = LearnState.Key(td.QualifiedName, c);
                    if (mapped.Contains(key)) continue;
                    if (!state.Untyped.TryGetValue(key, out var ev)) state.Untyped[key] = ev = new Evidence();
                    ev.Add(fx.Source);
                }
        return Fit(latest, state.Untyped, relations);
    }

    /// <summary>Per effect: columns whose new value was not typed on the screen just before are "untyped changes".</summary>
    public static void AccumulateUntyped(LearnState state, List<UiEvent> sessionEvents, EffectRecord fx)
    {
        var screen = Titles.Key(fx.Win);
        var typed = FieldMapper.TypedBefore(sessionEvents, screen, fx.T).Select(t => t.value).ToList();
        foreach (var td in fx.Diff.Tables)
            foreach (var fields in td.Changed.Values)
                foreach (var (col, cv) in fields)
                {
                    if (cv.New is null) continue;
                    if (typed.Any(t => Values.SameValue(t, cv.New))) continue;
                    var key = LearnState.Key(td.QualifiedName, col);
                    if (!state.Untyped.TryGetValue(key, out var ev)) state.Untyped[key] = ev = new Evidence();
                    ev.Add(fx.Source);
                }
    }

    public static List<Formula> Fit(DataSnapshot? latest, Dictionary<string, Evidence> untyped, List<Relation> relations)
    {
        var result = new List<Formula>();
        if (latest is null) return result;
        var tables = new Dictionary<string, TableSnapshot>();
        foreach (var (db, ts) in latest.Databases)
            foreach (var (name, t) in ts)
                if (!t.Skipped && t.Rows.Count >= 3) tables[RelationFinder.Qualified(db, name)] = t;

        foreach (var (q, t) in tables)
        {
            var rows = t.Rows.Values.Take(MaxRows).ToList();
            var keys = t.Rows.Keys.Take(MaxRows).ToList();
            var numeric = NumericColumns(t, rows).Take(MaxNumericColumns).ToList();
            if (numeric.Count < 2 && !relations.Any(r => r.ToTable == q)) continue;
            var parsed = numeric.ToDictionary(c => c, c => rows.Select(r => r.TryGetValue(c, out var v) && Values.TryDecimal(v, out var d) ? d : (decimal?)null).ToArray());
            var found = new List<(Formula f, Candidate c)>();

            foreach (var y in numeric)
            {
                var ys = parsed[y];
                var stages = new Func<List<Candidate>>[]
                {
                    () => FitIdentity(y, ys, numeric, parsed, keys),
                    () => FitChildren(q, t, y, relations, tables),
                    () => FitProduct(y, ys, numeric, parsed, keys),
                    () => FitRate(y, ys, numeric, parsed, keys),
                    () => FitLinear(y, ys, numeric, parsed, keys),
                };
                Candidate? best = null; List<Candidate> stageList = new();
                foreach (var stage in stages)
                {
                    stageList = stage().Where(c => c.Tested >= 3 && c.Explained >= 3).ToList();
                    var top = stageList.OrderByDescending(c => c.Explained).ThenBy(c => c.Expr.Length).ThenBy(c => c.Expr).FirstOrDefault();
                    if (top is not null && (double)top.Explained / top.Tested >= MinExplained) { best = top; break; }
                }
                if (best is null) continue;
                var f = new Formula
                {
                    Table = q, Column = y, Expression = best.Expr, Method = best.Method, Explained = best.Explained, Tested = best.Tested,
                    ResidualKeys = best.ResidualKeys.Take(3).ToList(), Rounded2 = AllTwoDecimals(ys),
                    Alternatives = stageList.Where(c => c != best && c.Explained == best.Explained).OrderBy(c => c.Expr).Select(c => c.Expr).Take(3).ToList(),
                };
                f.Evidence.Inferred = true;
                f.Evidence.Files.Add("snapshots/before.json");
                if (untyped.TryGetValue(LearnState.Key(q, y), out var ev) && ev.Seen > 0)
                {
                    f.UntypedChanges = ev.Seen;
                    f.Evidence.Inferred = false;
                    f.Evidence.Seen = ev.Seen;
                    foreach (var file in ev.Files) if (f.Evidence.Files.Count < 3 && !f.Evidence.Files.Contains(file)) f.Evidence.Files.Add(file);
                }
                found.Add((f, best));
            }

            // drop inverse readings: when total = qty × price is known, "qty = total / price" (a linear fit) adds nothing
            foreach (var (f, c) in found)
            {
                if (c.Method == "linear" && f.UntypedChanges == 0 &&
                    found.Any(o => o.c.Method != "linear" && o.c.Operands.Contains(f.Column) && c.Operands.Contains(o.f.Column)))
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
            if (nonNull >= 3 && numeric >= nonNull * 0.8 && distinct.Count >= 2) list.Add(c);
        }
        return list;
    }

    private static List<Candidate> FitIdentity(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed, List<string> keys)
    {
        var list = new List<Candidate>();
        foreach (var c in cols)
        {
            if (c == y) continue;
            var a = parsed[c];
            int explained = 0, tested = 0; var residual = new List<string>();
            for (var i = 0; i < ys.Length; i++)
            {
                if (ys[i] is null || a[i] is null) continue;
                tested++;
                if (Close(a[i]!.Value, ys[i]!.Value)) explained++; else if (residual.Count < 3) residual.Add(keys[i]);
            }
            if (explained > 0) list.Add(new Candidate($"{y} = {c}", explained, tested, "identity", residual, c));
        }
        return list;
    }

    private static List<Candidate> FitProduct(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed, List<string> keys)
    {
        var list = new List<Candidate>();
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i] == y) continue;
            for (var j = i + 1; j < cols.Count; j++)
            {
                if (cols[j] == y) continue;
                var a = parsed[cols[i]]; var b = parsed[cols[j]];
                int explained = 0, tested = 0; var residual = new List<string>();
                var da = new HashSet<decimal>(); var db = new HashSet<decimal>();
                for (var r = 0; r < ys.Length; r++)
                {
                    if (ys[r] is null || a[r] is null || b[r] is null) continue;
                    tested++; da.Add(a[r]!.Value); db.Add(b[r]!.Value);
                    if (Close(a[r]!.Value * b[r]!.Value, ys[r]!.Value)) explained++; else if (residual.Count < 3) residual.Add(keys[r]);
                }
                if (da.Count < 2 || db.Count < 2 || explained == 0) continue;
                list.Add(new Candidate($"{y} = {cols[i]} × {cols[j]}", explained, tested, "product", residual, cols[i], cols[j]));
            }
        }
        return list;
    }

    private static List<Candidate> FitRate(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed, List<string> keys)
    {
        var list = new List<Candidate>();
        foreach (var c in cols)
        {
            if (c == y) continue;
            var a = parsed[c];
            // rates seen in the data: the most common ratios y / a
            var ratios = new Dictionary<decimal, int>();
            for (var i = 0; i < ys.Length; i++)
                if (ys[i] is not null && a[i] is not null && a[i] != 0)
                {
                    var r = Math.Round(ys[i]!.Value / a[i]!.Value, 4);
                    if (r != 0 && r != 1) ratios[r] = ratios.GetValueOrDefault(r) + 1;
                }
            var seen = ratios.Where(r => r.Value >= 3).OrderByDescending(r => r.Value).Take(3).Select(r => r.Key);
            var rates = seen.Concat(CommonRates).Distinct().ToList();
            foreach (var r in rates)
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
                    int explained = 0, tested = 0; var residual = new List<string>();
                    var distinct = new HashSet<decimal>();
                    for (var i = 0; i < ys.Length; i++)
                    {
                        if (ys[i] is null || a[i] is null || a[i] == 0) continue;
                        tested++; distinct.Add(a[i]!.Value);
                        var v = fn(a[i]!.Value);
                        if (v is not null && Close(v.Value, ys[i]!.Value)) explained++; else if (residual.Count < 3) residual.Add(keys[i]);
                    }
                    if (distinct.Count < 2 || explained == 0) continue;
                    list.Add(new Candidate(expr, explained, tested, "percentage", residual, c));
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
            var parentRows = t.Rows.Take(MaxRows).Where(kv => kv.Value.TryGetValue(pk, out var k) && k is not null && groups.ContainsKey(k)).ToList();
            if (parentRows.Count < 3) continue;
            {
                int explained = 0, tested = 0; var residual = new List<string>();
                foreach (var (key, pr) in parentRows)
                {
                    if (!Values.TryDecimal(pr[y], out var yv)) continue;
                    tested++;
                    if (Close(groups[pr[pk]!].Count, yv)) explained++; else if (residual.Count < 3) residual.Add(key);
                }
                if (explained > 0) list.Add(new Candidate($"{y} = count of {rel.FromTable} rows where {rel.FromColumn} = {pk}", explained, tested, "sum-of-children", residual));
            }
            foreach (var x in numeric)
            {
                int explained = 0, tested = 0; var residual = new List<string>();
                foreach (var (key, pr) in parentRows)
                {
                    if (!Values.TryDecimal(pr[y], out var yv)) continue;
                    decimal sum = 0; var any = false;
                    foreach (var cr in groups[pr[pk]!])
                        if (cr.TryGetValue(x, out var xv) && Values.TryDecimal(xv, out var d)) { sum += d; any = true; }
                    if (!any) continue;
                    tested++;
                    if (Close(sum, yv)) explained++; else if (residual.Count < 3) residual.Add(key);
                }
                if (explained > 0) list.Add(new Candidate($"{y} = sum of {rel.FromTable}.{x} where {rel.FromColumn} = {pk}", explained, tested, "sum-of-children", residual));
            }
        }
        return list;
    }

    /// <summary>Least squares y = c0 + c1·a (+ c2·b + c3·c) over subsets of up to 3 columns.</summary>
    private static List<Candidate> FitLinear(string y, decimal?[] ys, List<string> cols, Dictionary<string, decimal?[]> parsed, List<string> keys)
    {
        var list = new List<Candidate>();
        var pool = cols.Where(c => c != y).Take(MaxLinearColumns).ToList();
        foreach (var subset in Subsets(pool, 3))
        {
            var xs = subset.Select(c => parsed[c]).ToList();
            var idx = new List<int>();
            for (var i = 0; i < ys.Length; i++) if (ys[i] is not null && xs.All(x => x[i] is not null)) idx.Add(i);
            if (idx.Count < subset.Count + 3) continue;
            var n = subset.Count + 1;
            var ata = new double[n, n]; var atb = new double[n];
            foreach (var i in idx)
            {
                var row = new double[n]; row[0] = 1;
                for (var k = 0; k < subset.Count; k++) row[k + 1] = (double)xs[k][i]!.Value;
                var yv = (double)ys[i]!.Value;
                for (var p = 0; p < n; p++) { atb[p] += row[p] * yv; for (var q2 = 0; q2 < n; q2++) ata[p, q2] += row[p] * row[q2]; }
            }
            var coef = Solve(ata, atb, n);
            if (coef is null) continue;
            if (subset.Count > 1 && coef.Skip(1).Any(c => Math.Abs(c) < 1e-6)) continue; // a smaller subset covers it
            int explained = 0; var residual = new List<string>();
            foreach (var i in idx)
            {
                var pred = coef[0];
                for (var k = 0; k < subset.Count; k++) pred += coef[k + 1] * (double)xs[k][i]!.Value;
                if (Close((decimal)Math.Round(pred, 6), ys[i]!.Value)) explained++; else if (residual.Count < 3) residual.Add(keys[i]);
            }
            if (explained == 0) continue;
            var terms = new List<string>();
            for (var k = 0; k < subset.Count; k++) terms.Add($"{Coef(coef[k + 1])}{subset[k]}");
            var expr = $"{y} = {string.Join(" + ", terms)}{(Math.Abs(coef[0]) >= 0.005 ? $" + {coef[0]:0.##}" : "")}".Replace("+ -", "- ");
            list.Add(new Candidate(expr, explained, idx.Count, "linear", residual, subset.ToArray()));
        }
        return list;
    }

    private static string Coef(double c) => Math.Abs(c - 1) < 1e-6 ? "" : Math.Abs(c + 1) < 1e-6 ? "-" : $"{c:0.####} × ";

    private static IEnumerable<List<string>> Subsets(List<string> pool, int maxSize)
    {
        for (var i = 0; i < pool.Count; i++)
        {
            yield return new List<string> { pool[i] };
            if (maxSize < 2) continue;
            for (var j = i + 1; j < pool.Count; j++)
            {
                yield return new List<string> { pool[i], pool[j] };
                if (maxSize < 3) continue;
                for (var k = j + 1; k < pool.Count; k++) yield return new List<string> { pool[i], pool[j], pool[k] };
            }
        }
    }

    private static double[]? Solve(double[,] a, double[] b, int n)
    {
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9) return null;
            for (var j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                for (var j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
        return x;
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
