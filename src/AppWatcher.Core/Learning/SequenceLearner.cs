using System.Text.RegularExpressions;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Columns that go up by one per new row are numbering sequences (invoice numbers, ids).</summary>
public static partial class SequenceLearner
{
    [GeneratedRegex(@"^(?<p>\D*?)(?<n>\d+)$")]
    private static partial Regex PrefixedNumber();

    public static List<Sequence> Learn(DataSnapshot? latest, List<EffectRecord> effects)
    {
        var result = new Dictionary<(string, string), Sequence>();

        // evidence from effects: new rows continue the previous maximum by one
        var lastMax = new Dictionary<(string, string), long>();
        foreach (var fx in effects.OrderBy(f => f.T))
            foreach (var td in fx.Diff.Tables)
            {
                if (td.Added.Count == 0) continue;
                var cols = td.Added.Values.First().Keys;
                foreach (var col in cols)
                {
                    var nums = new List<long>();
                    string? prefix = null; var ok = true;
                    foreach (var row in td.Added.Values)
                    {
                        if (!row.TryGetValue(col, out var v) || !TrySplit(v, out var p, out var n)) { ok = false; break; }
                        if (prefix is null) prefix = p; else if (prefix != p) { ok = false; break; }
                        nums.Add(n);
                    }
                    if (!ok || nums.Count == 0) continue;
                    nums.Sort();
                    var consecutive = nums.Count == 1 || nums.Zip(nums.Skip(1), (a, b) => b - a).All(d => d == 1);
                    if (!consecutive) continue;
                    var key = (td.QualifiedName, col);
                    if (lastMax.TryGetValue(key, out var prev) && nums[0] == prev + 1)
                    {
                        if (!result.TryGetValue(key, out var s)) result[key] = s = new Sequence { Table = td.QualifiedName, Column = col, Prefix = prefix };
                        s.Evidence.Add(fx.Source);
                    }
                    else if (nums.Count >= 2)
                    {
                        if (!result.TryGetValue(key, out var s)) result[key] = s = new Sequence { Table = td.QualifiedName, Column = col, Prefix = prefix };
                        s.Evidence.Add(fx.Source);
                    }
                    lastMax[key] = Math.Max(nums[^1], lastMax.GetValueOrDefault(key));
                }
            }

        // density check on the latest snapshot
        if (latest is not null)
            foreach (var (db, ts) in latest.Databases)
                foreach (var (name, t) in ts)
                {
                    if (t.Skipped || t.Rows.Count < 5) continue;
                    var q = RelationFinder.Qualified(db, name);
                    foreach (var col in t.Columns)
                    {
                        var nums = new HashSet<long>();
                        string? prefix = null; var ok = true; var n = 0;
                        foreach (var row in t.Rows.Values)
                        {
                            if (!row.TryGetValue(col, out var v) || string.IsNullOrEmpty(v)) continue;
                            n++;
                            if (!TrySplit(v, out var p, out var num)) { ok = false; break; }
                            if (prefix is null) prefix = p; else if (prefix != p) { ok = false; break; }
                            if (!nums.Add(num)) { ok = false; break; }
                        }
                        if (!ok || nums.Count < 5) continue;
                        var span = nums.Max() - nums.Min() + 1;
                        var dense = (double)nums.Count / span >= 0.9;
                        if (!dense) continue;
                        if (!result.TryGetValue((q, col), out var s))
                        {
                            s = new Sequence { Table = q, Column = col, Prefix = prefix };
                            s.Evidence.Inferred = true;
                            result[(q, col)] = s;
                        }
                        s.Dense = true;
                        if (!s.Evidence.Files.Contains("snapshots/before.json")) s.Evidence.Files.Add("snapshots/before.json");
                    }
                }

        // one "previous max + 1" pair is not enough on its own: ask for two confirmations, or one plus a dense range
        return result.Values
            .Where(s => s.Evidence.Seen >= 2 || (s.Dense && (s.Evidence.Seen >= 1 || s.Evidence.Inferred)))
            .OrderBy(s => s.Table).ThenBy(s => s.Column).ToList();
    }

    public static bool TrySplit(string? v, out string prefix, out long number)
    {
        prefix = ""; number = 0;
        if (string.IsNullOrEmpty(v) || v.Length > 20) return false;
        var m = PrefixedNumber().Match(v.Trim());
        if (!m.Success) return false;
        prefix = m.Groups["p"].Value;
        return long.TryParse(m.Groups["n"].Value, out number);
    }
}
