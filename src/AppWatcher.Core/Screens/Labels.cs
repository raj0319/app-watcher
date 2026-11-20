using System.Text;
using System.Text.RegularExpressions;

namespace AppWatcher.Core.Screens;

/// <summary>
/// Turns what UI Automation gives us (an empty Name and an AutomationId such as
/// "srcMasterSearch_EmbeddableTextBox") into the label a person would use ("Master Search").
/// Order: the control's own Name, then the nearest Text to the left or above, then the cleaned AutomationId.
/// </summary>
public static partial class Labels
{
    /// <summary>Suffixes that toolkits (Infragistics, DevExpress, Telerik) glue onto the editor inside a wrapper control.</summary>
    public static readonly string[] WrapperSuffixes =
    {
        "_EmbeddableTextBoxWithUIPermissions", "_EmbeddableTextBox", "[Editor] Edit Area", "_EditArea", "EditArea", "_Editor", "_TextBox",
        "_ValueList", "_DropDown", "_TextEdit", "_Edit", "_Inner", "_Host", "_Content",
    };

    /// <summary>Hungarian-style prefixes used by generated form code; stripped only when a capital letter or digit follows.</summary>
    private static readonly string[] Prefixes =
    {
        "tsmi", "dtp", "cbo", "cmb", "chk", "rdo", "btn", "lbl", "src", "num", "lkp", "grd", "lst", "pnl", "frm", "ucd", "opt", "spn", "mnu", "tsb", "ddl", "edt", "nud", "txt", "grid", "tab",
        "rb", "dt", "ne", "de", "te", "ce", "le", "tv", "uc", "ed", "cb", "tb", "rt", "me", "pe", "se", "ie",
    };

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"^(?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)$")]
    private static partial Regex Pos();

    public static bool IsWrapperId(string? aid) =>
        !string.IsNullOrEmpty(aid) && WrapperSuffixes.Any(s => aid.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>"srcMasterSearch_EmbeddableTextBox" → "Master Search"; "neRegisterNumber" → "Register Number"; "deEffective" → "Effective".</summary>
    public static string Clean(string? aid)
    {
        if (string.IsNullOrWhiteSpace(aid)) return "";
        var s = aid.Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suf in WrapperSuffixes)
                if (s.Length > suf.Length && s.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { s = s[..^suf.Length]; changed = true; }
        }
        // generated names end in a number: "textBox12", "ultraTextEditor3"
        s = Regex.Replace(s, @"(textBox|ultraTextEditor|ultraCombo|comboBox|checkBox|button|label|dateTimePicker|numericUpDown|richTextBox|dataGridView|listBox)\d+$", "$1", RegexOptions.IgnoreCase);
        foreach (var p in Prefixes.OrderByDescending(p => p.Length))
        {
            if (s.Length > p.Length && s.StartsWith(p, StringComparison.Ordinal) && (char.IsUpper(s[p.Length]) || char.IsDigit(s[p.Length]) || s[p.Length] == '_'))
            { s = s[p.Length..].TrimStart('_'); break; }
        }
        return Humanize(s);
    }

    /// <summary>"APInvoiceNumber" → "AP Invoice Number"; "register_number" → "Register Number".</summary>
    public static string Humanize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Replace('_', ' ').Replace('.', ' ').Trim();
        t = CamelBoundary().Replace(t, " ");
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        var sb = new StringBuilder();
        var newWord = true;
        foreach (var ch in t)
        {
            sb.Append(newWord && char.IsLower(ch) ? char.ToUpperInvariant(ch) : ch);
            newWord = ch == ' ';
        }
        return sb.ToString().Trim();
    }

    /// <summary>The label of a field row: its Name, else the nearest Text to its left or above it, else its cleaned AutomationId.</summary>
    public static string Resolve(ControlRow field, IList<ControlRow> rows)
    {
        var own = field.Name.Trim().TrimEnd(':', '*').Trim();
        // a name that is data (a phone, a city line, a record id) is never a label: the text next to it, its cleaned id or its shape stand in
        var data = own.Length > 0 && Masking.LooksLikeData(own);
        if (own.Length > 0 && !IsWrapperId(own) && !LooksLikeValue(own, field) && !data) return own;
        var near = NearestText(field, rows);
        if (near is not null) return near;
        var cleaned = Masking.LooksLikeData(field.AutomationId) ? "" : Clean(field.AutomationId);
        if (cleaned.Length > 0) return cleaned;
        if (data) return Masking.Shape(own);
        return own.Length > 0 ? own : field.Type;
    }

    /// <summary>A Name that is really the current value (dates, numbers, long text) is not a label.</summary>
    private static bool LooksLikeValue(string name, ControlRow field)
    {
        if (field.Type is not ("Edit" or "ComboBox" or "Spinner" or "Document")) return false;
        if (name.Length > 60) return true;
        return name.All(c => char.IsDigit(c) || c is '.' or ',' or '/' or '-' or ':' or ' ' or '$');
    }

    /// <summary>Nearest Text control whose box sits to the left on the same line, or just above and left-aligned.</summary>
    public static string? NearestText(ControlRow field, IList<ControlRow> rows)
    {
        if (!TryRect(field.Position, out var fx, out var fy, out var fw, out var fh) || fw <= 0 || fh <= 0) return null;
        string? best = null; var bestScore = double.MaxValue;
        foreach (var r in rows)
        {
            if (r.Type != "Text" || r.Name.Trim().Length == 0 || r.Name.Length > 60 || Masking.LooksLikeData(r.Name)) continue; // a text that is data (a city line) labels nothing
            if (!TryRect(r.Position, out var x, out var y, out var w, out var h) || w <= 0 || h <= 0) continue;
            var right = x + w; var bottom = y + h;
            double score;
            var sameLine = Math.Abs((y + h / 2.0) - (fy + fh / 2.0)) <= Math.Max(fh, h);
            if (sameLine && right <= fx + 4 && fx - right <= 260) score = fx - right;               // left of the field
            else if (bottom <= fy + 4 && fy - bottom <= 40 && Math.Abs(x - fx) <= 24) score = 100 + (fy - bottom); // just above, left aligned
            else continue;
            if (score < bestScore) { bestScore = score; best = r.Name.Trim().TrimEnd(':', '*').Trim(); }
        }
        return string.IsNullOrEmpty(best) ? null : best;
    }

    public static bool TryRect(string pos, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        var m = Pos().Match(pos ?? "");
        if (!m.Success) return false;
        x = int.Parse(m.Groups["x"].Value); y = int.Parse(m.Groups["y"].Value); w = int.Parse(m.Groups["w"].Value); h = int.Parse(m.Groups["h"].Value);
        return true;
    }
}
