namespace AppWatcher.Core.Learning;

/// <summary>
/// Titles that share one screen structure but differ in a record id or a name
/// ("Customer - 0002776 "Nate Test"", "A/P Invoice - INV410987") are folded into one pattern with the
/// varying words replaced by #. Built from screens/index.json, applied to every event and screen title
/// before learning, so flows line up and record names stay out of the memory files.
/// </summary>
public sealed class TitleNormalizer
{
    private readonly Dictionary<string, string> _map = new();

    public int Count => _map.Count;

    public static TitleNormalizer FromGroups(IEnumerable<IEnumerable<string>> groups)
    {
        var n = new TitleNormalizer();
        foreach (var g in groups)
        {
            var keys = g.Select(Titles.Key).Where(k => k.Length > 0).Distinct().ToList();
            if (keys.Count < 2) continue;
            var pattern = Pattern(keys);
            if (pattern.Length == 0) continue;
            foreach (var k in keys) if (k != pattern) n._map[k] = pattern;
        }
        return n;
    }

    /// <summary>Word-wise: words equal in every title stay, the rest become one #; unequal lengths fall back to common prefix + # + common suffix.</summary>
    public static string Pattern(List<string> keys)
    {
        var tokens = keys.Select(k => k.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (tokens.Any(t => t.Length == 0)) return "";
        var len = tokens[0].Length;
        if (tokens.All(t => t.Length == len))
        {
            var outTokens = new List<string>();
            for (var i = 0; i < len; i++)
            {
                var w = tokens[0][i];
                outTokens.Add(tokens.All(t => t[i] == w) ? w : "#");
            }
            return Collapse(outTokens);
        }
        var min = tokens.Min(t => t.Length);
        var prefix = 0;
        while (prefix < min && tokens.All(t => t[prefix] == tokens[0][prefix])) prefix++;
        var suffix = 0;
        while (suffix < min - prefix && tokens.All(t => t[^(suffix + 1)] == tokens[0][^(suffix + 1)])) suffix++;
        var result = tokens[0].Take(prefix).ToList();
        result.Add("#");
        result.AddRange(tokens[0].Skip(tokens[0].Length - suffix));
        return Collapse(result);
    }

    private static string Collapse(List<string> words)
    {
        var sb = new List<string>();
        foreach (var w in words) if (!(w == "#" && sb.Count > 0 && sb[^1] == "#")) sb.Add(w);
        return string.Join(" ", sb);
    }

    public string Apply(string? title)
    {
        var key = Titles.Key(title);
        return _map.TryGetValue(key, out var p) ? p : key;
    }
}
