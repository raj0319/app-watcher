namespace AppWatcher.Core.Screens;

/// <summary>
/// Tells a message box from a screen by its STRUCTURE, never by its title alone. A window is a message box when
/// its class is the Windows dialog class #32770 and it holds no grid, list, tree or field to type into, or when it
/// has 25 controls or fewer, every button is one of OK, Cancel, Yes, No, Close, Retry, Abort, Ignore, Help (plus the
/// window's own System, Minimize, Maximize buttons, which some toolkits list outside the title bar), it shows its
/// message in a Text or in an Edit that is read-only or named like a body (Infragistics' lblBody, whose inner
/// "[Editor] Edit Area" is part of it), and it holds no grid, list, tree, tab or editable field.
/// The title bar (Minimize, Maximize, Close, system menu) is on every window and is not counted.
/// Checked against the three message boxes recorded at the first client (Close?, No Selection Specified, an information box with a tick box).
/// </summary>
public static class DialogDetector
{
    public static readonly HashSet<string> DialogButtons = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "cancel", "yes", "no", "close", "retry", "abort", "ignore", "help", "system", "system menu", "minimize", "maximize", "restore" };

    /// <summary>Buttons every window has; never a reason to call a window a form, never reported as "not used".</summary>
    public static readonly HashSet<string> WindowButtons = new(StringComparer.OrdinalIgnoreCase)
        { "system", "system menu", "minimize", "maximize", "restore", "restore down", "close", "help" };

    /// <summary>AutomationId words that name a message body: such an Edit is the message, whatever its read-only flag says.</summary>
    private static readonly string[] BodyIds = { "body", "message", "msg", "prompt", "content", "lbl", "static" };

    /// <summary>Control types a message box never has. A single tick box ("do not show this again") is allowed.</summary>
    private static readonly HashSet<string> NeverInDialog = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataGrid", "Table", "List", "Tree", "ComboBox", "RadioButton", "Spinner", "Slider", "Calendar",
        "Tab", "TabItem", "ToolBar", "MenuBar", "Menu", "MenuItem", "SplitButton", "DataItem", "ListItem", "TreeItem", "Header", "HeaderItem",
    };

    /// <summary>Control types that make a window a screen even when a profile pattern names it a dialog.</summary>
    private static readonly HashSet<string> NeverInNamedDialog = new(StringComparer.OrdinalIgnoreCase)
        { "DataGrid", "Table", "List", "Tree", "Tab", "TabItem", "DataItem", "ListItem", "TreeItem" };

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

    /// <summary>
    /// Content with toolkit wrappers folded: an inner editor ("[Editor] Edit Area", "_EmbeddableTextBox") nested in another
    /// Edit is the same control, so a message body counts once and never as a second, editable field.
    /// </summary>
    public static List<ControlRow> Logical(IList<ControlRow> rows)
    {
        var content = Content(rows);
        var result = new List<ControlRow>();
        var ancestors = new ControlRow?[64];
        foreach (var r in content)
        {
            if (r.Depth < ancestors.Length) ancestors[r.Depth] = r;
            var inner = false;
            if (r.Type is "Edit" or "Document" && Labels.IsWrapperId(r.AutomationId))
                for (var d = r.Depth - 1; d >= Math.Max(1, r.Depth - 2); d--)
                    if (ancestors[d] is { } a && a.Type is "Edit" or "Document") { inner = true; break; }
            if (!inner) result.Add(r);
        }
        return result;
    }

    private static bool IsBodyEdit(ControlRow r) =>
        r.Type is "Edit" or "Document" &&
        (r.IsReadOnly == true || BodyIds.Any(b => r.AutomationId.Contains(b, StringComparison.OrdinalIgnoreCase)));

    private static bool IsEditable(ControlRow r) => r.Type is "Edit" or "Document" && !IsBodyEdit(r);

    /// <summary>True when nothing in the structure rules out a message box: 25 controls or fewer, no grid, list, tree or editable field.</summary>
    public static bool CouldBeDialog(IList<ControlRow> rows)
    {
        var c = Logical(rows);
        return c.Count <= 25 && !c.Any(r => NeverInDialog.Contains(r.Type) || IsEditable(r)) && c.Count(r => r.Type == "CheckBox") <= 1;
    }

    /// <summary>
    /// A window whose title a profile names as a message box (dialogWindows): believed unless the structure says screen
    /// (a grid, list, tree or tab, or more than 40 controls). An editable-looking Edit is allowed: toolkits report a label-like
    /// body as editable.
    /// </summary>
    public static bool CouldBeNamedDialog(IList<ControlRow> rows)
    {
        var c = Logical(rows);
        return c.Count <= 40 && !c.Any(r => NeverInNamedDialog.Contains(r.Type));
    }

    /// <summary>Returns whether the window is a message box, and its body text when it is.</summary>
    public static (bool isDialog, string? body) Detect(string windowClass, IList<ControlRow> rows)
    {
        var c = Logical(rows);
        var buttons = c.Where(r => r.Type == "Button" && r.Name.Trim().Length > 0).ToList();
        var texts = c.Where(r => r.Type == "Text" && r.Name.Trim().Length > 0 && !DialogButtons.Contains(ButtonKey(r.Name))).ToList();
        var bodyEdits = c.Where(IsBodyEdit).ToList();
        var bodyParts = texts.Select(t => t.Name.Trim()).Concat(bodyEdits.Select(e => e.Name.Trim())).Where(s => s.Length > 0).Distinct().ToList();
        var body = bodyParts.Count == 0 ? null : string.Join(" / ", bodyParts.Take(8));
        var hasForbidden = c.Any(r => NeverInDialog.Contains(r.Type) || IsEditable(r)) || c.Count(r => r.Type == "CheckBox") > 1;
        var answerButtons = buttons.Where(b => !WindowButtons.Contains(ButtonKey(b.Name))).ToList();

        // the Windows dialog class: a message box unless it is really a small form (grid, list, tree, a field to type into)
        if (windowClass == "#32770" && c.Count <= 60 && buttons.Count is > 0 and <= 8 && !hasForbidden)
            return (true, body);

        if (c.Count > 25 || answerButtons.Count == 0 || hasForbidden) return (false, null);
        if (!buttons.All(b => DialogButtons.Contains(ButtonKey(b.Name)))) return (false, null);
        if (texts.Count == 0 && bodyEdits.Count == 0) return (false, null);
        return (true, body);
    }

    /// <summary>
    /// Dialogs drawn inside a web page (an HTML dialog, an ARIA dialog): a Window element below a Document, with buttons, that
    /// has no window of its own. A message (the same structure test as a message box) is a dialog; a small named form inside
    /// the page ("New Customer") is a screen of its own. Returned in reading order: title, body (messages only), is a message.
    /// </summary>
    public static List<(string title, string? body, bool message)> InPage(IList<ControlRow> rows)
    {
        var found = new List<(string, string?, bool)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Type != "Window" || r.Depth == 0) continue;
            var inDocument = false; var d = r.Depth;
            for (var j = i - 1; j >= 0 && d > 0; j--)
                if (rows[j].Depth < d) { d = rows[j].Depth; if (rows[j].Type == "Document") { inDocument = true; break; } }
            if (!inDocument) continue;
            var sub = new List<ControlRow>();
            for (var k = i + 1; k < rows.Count && rows[k].Depth > r.Depth; k++) sub.Add(rows[k]);
            if (sub.Count == 0 || sub.Count > 40 || !sub.Any(x => x.Type == "Button" && x.Name.Trim().Length > 0)) continue;
            var title = r.Name.Trim();
            var (isMessage, body) = Detect("", sub);
            if (isMessage) found.Add((title.Length > 0 ? title : body ?? "Message", body, true));
            else if (title.Length > 0 && CouldBeNamedDialog(sub)) found.Add((title, null, false));
        }
        return found;
    }

    private static readonly string[] PromptWords = { "close?", "confirm", "warning", "error", "specified", "question", "alert", "attention", "notice", "are you sure", "information" };

    /// <summary>A title that reads like a prompt ("Close?", "Confirm Delete", "Warning", "No Selection Specified"): a hint for the health page, never proof.</summary>
    public static bool LooksLikePromptTitle(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        if (t.EndsWith('?')) return true;
        return PromptWords.Any(w => t.StartsWith(w) || t.EndsWith(w));
    }

    /// <summary>
    /// A popup menu that is an ordinary window (Infragistics, DevExpress menus): its controls are menu items (and the panes
    /// that hold them), nothing to type into. Recorded for its items and access keys, never as a screen.
    /// </summary>
    public static bool IsMenuWindow(IList<ControlRow> rows)
    {
        if (rows.Count == 0 || rows.Count > 120) return false;
        var items = rows.Count(c => c.Type is "MenuItem");
        if (items == 0 || rows.Any(c => c.Type is "Edit" or "DataGrid" or "Tab" or "Document" or "Table" or "List" or "Tree" or "ComboBox")) return false;
        var other = rows.Count(c => c.Type is not ("MenuItem" or "Menu" or "MenuBar" or "Pane" or "Group" or "Window" or "Custom" or "Separator" or "Image" or "Text"));
        return other == 0;
    }

    /// <summary>"&amp;Sales - 0" → "Sales": the name of a popup menu window, without the access-key marker and the toolkit's index.</summary>
    public static string MenuName(string? title)
    {
        var t = (title ?? "").Replace("&", "").Trim();
        var dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && t[(dash + 3)..].Trim().All(char.IsDigit)) t = t[..dash].Trim();
        return t;
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
