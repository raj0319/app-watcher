using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core.Learning;

/// <summary>One line of text on a picture, with its box.</summary>
public sealed class OcrLine
{
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>The text layer of one state picture of one screen.</summary>
public sealed class OcrState
{
    /// <summary>The screen's title key (the folder's "Pictures of:" line).</summary>
    public string Screen { get; set; } = "";
    /// <summary>Evidence pointer: "screens/shots/&lt;screen&gt;/03-after-type-qty.png.ocr.json".</summary>
    public string File { get; set; } = "";
    public DateTime T { get; set; }
    public string Label { get; set; } = "";
    public List<OcrLine> Lines { get; set; } = new();
}

/// <summary>
/// The pictures' text as evidence for the logic, for the controls the tree does not report (grid cells, bound editors,
/// custom-drawn screens). Every number on a picture is paired with its label (the text on its left on the same line,
/// or the header above it in a grid); a number that changed since the previous picture of the same screen, or that
/// appeared right after the user did something, is tested against the other numbers on that picture, closest first.
/// Findings carry the picture's .ocr.json as evidence, so a reader can tell them from tree-based ones. Additive per
/// picture: each picture is processed once (LearnState.OcrFiles), and the last values per screen are kept in the state.
/// </summary>
public static partial class OcrLogic
{
    public const double AfterUserSeconds = 5;
    public const int MaxInputs = 8;

    [GeneratedRegex(@"^[\$€£]?\(?-?\d{1,3}(,\d{3})*(\.\d+)?\)?%?$|^[\$€£]?-?\d+(\.\d+)?%?$")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"(?<label>.*?[A-Za-z][^\d]*?)[\s:]+(?<num>[\$€£]?\(?-?\d[\d,]*(\.\d+)?\)?%?)\s*$")]
    private static partial Regex LabelThenNumber();

    /// <summary>Reads the text layers next to the pictures: screens/shots/&lt;screen&gt;/index.json gives the time and label of each picture.</summary>
    public static List<OcrState> Load(string screensDir)
    {
        var list = new List<OcrState>();
        var shots = Path.Combine(screensDir, "shots");
        if (!Directory.Exists(shots)) return list;
        foreach (var folder in Directory.GetDirectories(shots).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var screen = "";
                var indexMd = Path.Combine(folder, "index.md");
                if (File.Exists(indexMd))
                {
                    var first = File.ReadLines(indexMd).FirstOrDefault() ?? "";
                    if (first.StartsWith("# Pictures of: ", StringComparison.Ordinal)) screen = first[15..].Trim();
                }
                if (screen.Length == 0) screen = Path.GetFileName(folder);
                var indexJson = Path.Combine(folder, "index.json");
                if (!File.Exists(indexJson)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(indexJson));
                if (!doc.RootElement.TryGetProperty("Entries", out var entries)) continue;
                foreach (var e in entries.EnumerateArray())
                {
                    var file = e.GetProperty("File").GetString() ?? "";
                    var sidecar = Path.Combine(folder, file + ".ocr.json");
                    if (file.Length == 0 || !File.Exists(sidecar)) continue;
                    var state = new OcrState
                    {
                        Screen = screen, T = e.TryGetProperty("T", out var t) && t.TryGetDateTime(out var dt) ? dt : default,
                        Label = e.TryGetProperty("Label", out var l) ? l.GetString() ?? "" : "",
                        File = "screens/shots/" + Path.GetFileName(folder) + "/" + file + ".ocr.json",
                        Lines = ParseLines(File.ReadAllText(sidecar)),
                    };
                    if (state.Lines.Count > 0) list.Add(state);
                }
            }
            catch (Exception ex) { Log.Warn($"OCR layer of {Path.GetFileName(folder)} not read: {ex.Message}"); }
        }
        return list.OrderBy(s => s.T).ThenBy(s => s.File, StringComparer.Ordinal).ToList();
    }

    /// <summary>The lines of a .ocr.json sidecar (Lines: Text, X, Y, W, H).</summary>
    public static List<OcrLine> ParseLines(string json)
    {
        var list = new List<OcrLine>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Lines", out var lines)) return list;
        foreach (var l in lines.EnumerateArray())
        {
            var o = new OcrLine { Text = l.TryGetProperty("Text", out var tx) ? tx.GetString() ?? "" : "" };
            if (l.TryGetProperty("X", out var x)) o.X = x.GetInt32();
            if (l.TryGetProperty("Y", out var y)) o.Y = y.GetInt32();
            if (l.TryGetProperty("W", out var w)) o.W = w.GetInt32();
            if (l.TryGetProperty("H", out var h)) o.H = h.GetInt32();
            if (o.Text.Trim().Length > 0) list.Add(o);
        }
        return list;
    }

    /// <summary>
    /// The field and column names each screen record knows (its fields' labels, its grid headers, its plain labels), by screen
    /// key: a label on a picture must be one of them before it names a formula's field (the picture of an order shows the
    /// customer's address too, and "<City>, NY" is not a field).
    /// </summary>
    public static Dictionary<string, HashSet<string>> KnownFields(IEnumerable<ScreenRecord> screens)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rec in screens)
        {
            var key = Titles.Key(rec.Title);
            if (key.Length == 0) continue;
            if (!result.TryGetValue(key, out var set)) result[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (row, label) in rec.LogicalFields()) { if (label.Length > 0) set.Add(Norm(label)); if (row.Name.Length > 0) set.Add(Norm(row.Name)); }
            foreach (var r in rec.Controls)
            {
                if (r.Type is "HeaderItem" or "Header" or "Text" or "CheckBox" or "RadioButton" or "ComboBox" or "Edit" or "Spinner" or "DataItem" && LooksLikeFieldName(r.Name)) set.Add(Norm(r.Name));
                if (r.LabeledBy.Length > 0) set.Add(Norm(r.LabeledBy));
            }
            // the parent window's labels count for its documents too (older records hold the whole window)
        }
        foreach (var key in result.Keys.Where(k => k.Contains(" › ", StringComparison.Ordinal)).ToList())
        {
            var parent = key[..key.IndexOf(" › ", StringComparison.Ordinal)];
            if (result.TryGetValue(parent, out var pset)) foreach (var f in pset) result[key].Add(f);
        }
        return result;
    }

    private static string Norm(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim().TrimEnd(':', ' ').Trim();

    /// <summary>A label a field could carry: letters, short, no comma after letters (an address line), no digits run (a value).</summary>
    public static bool LooksLikeFieldName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = Norm(s);
        if (t.Length < 2 || t.Length > 40) return false;
        if (!t.Any(char.IsLetter)) return false;
        if (t.Contains(',')) return false; // "<City>, NY"
        if (char.IsDigit(t[0])) return false; // "12 Some Street": a street line
        if (t.Count(char.IsDigit) > 2) return false; // "###-###-####"
        if (t.Contains('@') || t.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>A number as shown on screen: money, percentages and thousands separators allowed; dates, times, codes with leading zeros, long ids and more than four decimals are not numbers.</summary>
    public static bool TryNumber(string token, out decimal value)
    {
        value = 0;
        var t = token.Trim();
        if (t.Length == 0 || t.Length > 16 || !NumberToken().IsMatch(t)) return false;
        var digits = t.Count(char.IsDigit);
        if (digits == 0 || digits > 9) return false;
        var dot = t.IndexOf('.');
        if (dot >= 0 && t[(dot + 1)..].TakeWhile(char.IsDigit).Count() > 4) return false;
        var neg = t.Contains('(') || t.Contains('-');
        var clean = t.Replace("$", "").Replace("€", "").Replace("£", "").Replace("(", "").Replace(")", "").Replace("%", "").Replace(",", "").Replace("-", "");
        if (clean.Length > 1 && clean[0] == '0' && clean[1] != '.') return false; // a code (0002776), not an amount
        if (!decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (neg) value = -value;
        return true;
    }

    /// <summary>Label → number pairs on one picture: "Net Price 45.34" on one line, or a bare number with the label on its left or the header above it.</summary>
    public static List<(string label, decimal value, string raw, int x, int y)> Pairs(List<OcrLine> lines)
    {
        var result = new List<(string, decimal, string, int, int)>();
        var textLines = lines.Where(l => !TryNumber(l.Text, out _)).ToList();
        foreach (var l in lines)
        {
            var text = l.Text.Trim();
            if (TryNumber(text, out var v))
            {
                var label = LabelFor(l, textLines);
                if (label.Length > 0) result.Add((label, v, text, l.X, l.Y));
                continue;
            }
            var m = LabelThenNumber().Match(text);
            if (!m.Success) continue;
            var raw = m.Groups["num"].Value;
            if (!TryNumber(raw, out var v2)) continue;
            var lab = CleanLabel(m.Groups["label"].Value);
            if (lab.Length >= 2 && !lab.Any(char.IsDigit)) result.Add((lab, v2, raw, l.X + l.W, l.Y)); // "Page 1 of 1" is not a labelled number
        }
        return result;
    }

    private static string CleanLabel(string s)
    {
        var t = s.Trim().TrimEnd(':', '=', '-', '.', ' ').Trim();
        return t.Length > 60 ? "" : t;
    }

    /// <summary>
    /// The label of a bare number: the nearest non-number text on its left in the same row (a form's label, within 320 px) or
    /// the nearest one above it with overlapping columns (a grid's column header, within 500 px), whichever is closer in pixels.
    /// In a grid the cell on the left is another value's text, farther away than the header above; on a form the label sits
    /// right next to the value.
    /// </summary>
    private static string LabelFor(OcrLine n, List<OcrLine> textLines)
    {
        OcrLine? best = null; var bestScore = double.MaxValue;
        var ny = n.Y + n.H / 2.0;
        foreach (var t in textLines)
        {
            if (t.Text.Length > 60) continue;
            var ty = t.Y + t.H / 2.0;
            var right = t.X + t.W;
            var sameRow = Math.Abs(ty - ny) <= Math.Max(n.H, t.H);
            if (sameRow && right <= n.X + 4 && n.X - right <= 320) { var s = n.X - right; if (s < bestScore) { bestScore = s; best = t; } }
            var bottom = t.Y + t.H;
            var overlap = t.X < n.X + n.W && t.X + t.W > n.X;
            // among headers above, the one whose centre is nearest the number's centre (neighbouring headers can overlap a little)
            if (overlap && bottom <= n.Y + 2 && n.Y - bottom <= 500) { var s = n.Y - bottom + Math.Abs((t.X + t.W / 2.0) - (n.X + n.W / 2.0)) * 0.05; if (s < bestScore) { bestScore = s; best = t; } }
        }
        return best is null ? "" : CleanLabel(best.Text);
    }

    /// <summary>
    /// Feeds the pictures of one session to the calculation learner: a number that changed since the previous picture of the
    /// screen, or that appeared within <see cref="AfterUserSeconds"/> after the user typed, chose or pressed something on
    /// that screen, is tested against the other numbers on the picture (same row first, then nearest). Every picture is
    /// processed once; the state keeps the last values per screen.
    /// </summary>
    public static void Accumulate(LearnState s, List<UiEvent> session, List<OcrState> states) => Accumulate(s, session, states, null);

    /// <summary>
    /// Same, with the fields each screen record knows: on a screen that has a record, only labels that match one of its field
    /// or column names take part (as the number tested or as an input); on a screen without a record, labels must at least look
    /// like field names. Text from a picture never reaches the memory files except as such a field name.
    /// </summary>
    public static void Accumulate(LearnState s, List<UiEvent> session, List<OcrState> states, Dictionary<string, HashSet<string>>? knownFields)
    {
        var userTimes = session.Where(e => (e.Act == Acts.Value && e.By != ValueBy.App) || e.Act is Acts.Invoke or Acts.Select)
            .Select(e => (win: Titles.Key(e.Win), t: e.T)).ToList();
        // what the user typed (the picture shows it too, but it is not a fill) and what the tree already reported as filled (tested once, from the tree)
        var typed = session.Where(e => e.Act == Acts.Value && e.By != ValueBy.App && e.FieldKey.Length > 0).Select(e => (win: Titles.Key(e.Win), field: e.FieldKey, t: e.T)).ToList();
        var treeFills = session.Where(e => e.Act == Acts.Value && e.By == ValueBy.App && e.FieldKey.Length > 0).Select(e => (win: Titles.Key(e.Win), field: e.FieldKey, t: e.T)).ToList();
        static bool SameField(string a, string b) => string.Equals(a.TrimEnd(':', ' '), b.TrimEnd(':', ' '), StringComparison.OrdinalIgnoreCase);
        foreach (var st in states.OrderBy(x => x.T).ThenBy(x => x.File, StringComparer.Ordinal))
        {
            if (!s.OcrFiles.Add(st.File)) continue;
            var screen = Titles.Key(st.Screen);
            if (screen.Length == 0) continue;
            var pairs = Pairs(st.Lines);
            HashSet<string>? known = null;
            if (knownFields is not null) { knownFields.TryGetValue(screen, out known); known ??= knownFields.GetValueOrDefault(st.Screen); }
            pairs = pairs.Where(p => LooksLikeFieldName(p.label) && (known is null || known.Contains(Norm(p.label)))).ToList();
            if (pairs.Count == 0) continue;
            s.OcrLast.TryGetValue(screen, out var prev);
            var afterUser = userTimes.Any(u => u.win == screen && st.T >= u.t && (st.T - u.t).TotalSeconds <= AfterUserSeconds);
            // the same label can appear in several grid rows: number them in reading order
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var keyed = new List<(string key, decimal value, string raw, int x, int y)>();
            foreach (var p in pairs.OrderBy(p => p.y).ThenBy(p => p.x))
            {
                var n = seen.GetValueOrDefault(p.label);
                seen[p.label] = n + 1;
                keyed.Add((n == 0 ? p.label : $"{p.label} ({n + 1})", p.value, p.raw, p.x, p.y));
            }
            var cur = keyed.GroupBy(k => k.key).ToDictionary(g => g.Key, g => g.First().raw, StringComparer.Ordinal);
            foreach (var k in keyed)
            {
                var had = prev is not null && prev.TryGetValue(k.key, out var old);
                var changed = had && prev![k.key] != k.raw;
                var isNew = !had && (prev is not null || afterUser);
                if (!changed && !(isNew && afterUser)) continue;
                var field = Plain(k.key);
                if (typed.Any(u => u.win == screen && SameField(u.field, field) && st.T >= u.t && (st.T - u.t).TotalSeconds <= 10)) continue; // the user typed it
                if (treeFills.Any(u => u.win == screen && SameField(u.field, field) && Math.Abs((st.T - u.t).TotalSeconds) <= 5)) continue; // the tree reported it: tested there
                var inputs = keyed.Where(o => o.key != k.key && o.value != 0)
                    .OrderBy(o => Math.Abs(o.y - k.y) <= 12 ? 0 : 1).ThenBy(o => Math.Abs(o.y - k.y) + Math.Abs(o.x - k.x) / 4)
                    .Select(o => (n: Plain(o.key), v: o.value)).Where(o => o.n != Plain(k.key))
                    .GroupBy(o => o.n).Select(g => g.First()).Take(MaxInputs).ToList(); // one value per name: the nearest row
                if (inputs.Count == 0) continue;
                LogicLearner.LearnCalc(s, screen, field, k.value, inputs, st.File);
            }
            s.OcrLast[screen] = cur;
        }
    }

    /// <summary>"Price Extension (2)" → "Price Extension": the field name without the row number.</summary>
    private static string Plain(string key)
    {
        var i = key.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 && key.EndsWith(')') && key[(i + 2)..^1].All(char.IsDigit) ? key[..i] : key;
    }
}
