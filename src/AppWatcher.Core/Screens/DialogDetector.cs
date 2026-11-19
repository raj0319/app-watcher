namespace AppWatcher.Core.Screens;

/// <summary>
/// Tells a message box from a screen by its STRUCTURE, never by its title alone. A window is a message box when
/// its class is the Windows dialog class #32770 and it holds no grid, list, tree or field to type into, or when it
/// has 25 controls or fewer, every button is one of OK, Cancel, Yes, No, Close, Retry, Abort, Ignore, it shows its
/// message in a Text or a read-only Edit (Infragistics' lblBody), and it holds no grid, list, tree or editable field.
/// The title bar (Minimize, Maximize, Close, system menu) is on every window and is not counted.
/// </summary>
public static class DialogDetector
{
    public static readonly HashSet<string> DialogButtons = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "cancel", "yes", "no", "close", "retry", "abort", "ignore" };

    /// <summary>AutomationId words that name a message body when the read-only state could not be read.</summary>
    private static readonly string[] BodyIds = { "body", "message", "msg", "prompt", "content", "lbl", "static" };

    /// <summary>Control types a message box never has.</summary>
    private static readonly HashSet<string> NeverInDialog = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataGrid", "Table", "List", "Tree", "ComboBox", "CheckBox", "RadioButton", "Spinner", "Slider", "Calendar",
        "Tab", "TabItem", "ToolBar", "MenuBar", "Menu", "MenuItem", "SplitButton", "DataItem", "ListItem", "TreeItem", "Header", "HeaderItem",
    };

    public static string ButtonKey(string name) => name.Replace("&", "").Replace("_", "").Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>The window's controls without the window row itself and without its title bar.</summary>
    public static List<ControlRow> Content(IList<ControlRow> rows)
    {
        var list = new List<ControlRow>();
        int? skipBelow = null;
        foreach (var r in rows)
        {
            if (skipBelow is not null)
            {
                if (r.Depth > skipBelow) continue;
                skipBelow = null;
            }
            if (r.Type == "TitleBar") { skipBelow = r.Depth; continue; }
            if (r.Depth == 0) continue;
            list.Add(r);
        }
        return list;
    }

    private static bool IsBodyEdit(ControlRow r) =>
        r.Type is "Edit" or "Document" &&
        (r.IsReadOnly == true || (r.IsReadOnly is null && BodyIds.Any(b => r.AutomationId.Contains(b, StringComparison.OrdinalIgnoreCase))));

    private static bool IsEditable(ControlRow r) => r.Type is "Edit" or "Document" && !IsBodyEdit(r);

    /// <summary>True when nothing in the structure rules out a message box: 25 controls or fewer, no grid, list, tree or editable field.</summary>
    public static bool CouldBeDialog(IList<ControlRow> rows)
    {
        var c = Content(rows);
        return c.Count <= 25 && !c.Any(r => NeverInDialog.Contains(r.Type) || IsEditable(r));
    }

    /// <summary>Returns whether the window is a message box, and its body text when it is.</summary>
    public static (bool isDialog, string? body) Detect(string windowClass, IList<ControlRow> rows)
    {
        var c = Content(rows);
        var buttons = c.Where(r => r.Type == "Button" && r.Name.Trim().Length > 0).ToList();
        var texts = c.Where(r => r.Type == "Text" && r.Name.Trim().Length > 0 && !DialogButtons.Contains(ButtonKey(r.Name))).ToList();
        var bodyEdits = c.Where(IsBodyEdit).ToList();
        var bodyParts = texts.Select(t => t.Name.Trim()).Concat(bodyEdits.Select(e => e.Name.Trim())).Where(s => s.Length > 0).Distinct().ToList();
        var body = bodyParts.Count == 0 ? null : string.Join(" / ", bodyParts.Take(8));
        var hasForbidden = c.Any(r => NeverInDialog.Contains(r.Type) || IsEditable(r));

        // the Windows dialog class: a message box unless it is really a small form (grid, list, tree, a field to type into)
        if (windowClass == "#32770" && c.Count <= 60 && buttons.Count is > 0 and <= 6 && !hasForbidden)
            return (true, body);

        if (c.Count > 25 || buttons.Count == 0 || hasForbidden) return (false, null);
        if (!buttons.All(b => DialogButtons.Contains(ButtonKey(b.Name)))) return (false, null);
        if (texts.Count == 0 && bodyEdits.Count == 0) return (false, null);
        return (true, body);
    }

    private static readonly string[] SessionWords = { "login", "log in", "logon", "log on", "sign in", "splash", "register", "station", "password", "welcome", "loading", "please wait", "select company", "choose company" };

    /// <summary>Splash, login and station prompts: a password field, or a tiny first window, or a telling title.</summary>
    public static bool IsSessionWindow(string title, IList<ControlRow> rows, bool firstWindowOfProcess, IEnumerable<string>? extraPatterns = null)
    {
        if (rows.Any(r => r.IsPassword)) return true;
        var t = (title ?? "").ToLowerInvariant();
        if (SessionWords.Any(w => t.Contains(w))) return true;
        if (extraPatterns is not null && extraPatterns.Any(p => p.Length > 0 && Wildcard(t, p.ToLowerInvariant()))) return true;
        if (firstWindowOfProcess && rows.Count < 12 && !rows.Any(r => r.Type is "MenuBar" or "DataGrid" or "Tab")) return true;
        return false;
    }

    /// <summary>Simple * wildcard match, case-insensitive.</summary>
    public static bool Wildcard(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text ?? "", rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
