namespace AppWatcher.Core.Screens;

/// <summary>
/// Tells a message box from a screen. Windows message boxes have class #32770; toolkit message boxes
/// (Infragistics, DevExpress) are ordinary windows with a few buttons named OK/Cancel/Yes/No and a body
/// that is a Text control or a read-only Edit named like lblBody.
/// </summary>
public static class DialogDetector
{
    private static readonly HashSet<string> DialogButtons = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "cancel", "yes", "no", "close", "retry", "ignore", "abort", "continue", "help", "try again", "yes to all", "no to all", "details", "copy" };

    private static readonly string[] BodyIds = { "body", "message", "msg", "text", "prompt", "content", "label", "static" };

    public static string ButtonKey(string name) => name.Replace("&", "").Replace("_", "").Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Returns whether the window is a message box, and its body text when it is.</summary>
    public static (bool isDialog, string? body) Detect(string windowClass, IList<ControlRow> rows)
    {
        var buttons = rows.Where(r => r.Type == "Button" && r.Name.Trim().Length > 0).ToList();
        var dialogButtons = buttons.Where(b => DialogButtons.Contains(ButtonKey(b.Name))).ToList();
        var texts = rows.Where(r => r.Depth > 0 && r.Type == "Text" && r.Name.Trim().Length > 0).ToList();
        var bodyEdits = rows.Where(r => r.Type is "Edit" or "Document" && (BodyIds.Any(b => r.AutomationId.Contains(b, StringComparison.OrdinalIgnoreCase)) || r.Name.Trim().Length > 0)).ToList();
        var bodyParts = texts.Select(t => t.Name.Trim()).Concat(bodyEdits.Select(e => e.Name.Trim())).Where(s => s.Length > 0 && !DialogButtons.Contains(ButtonKey(s))).Distinct().ToList();
        var body = bodyParts.Count == 0 ? null : string.Join(" / ", bodyParts.Take(8));

        var classic = windowClass == "#32770" && rows.Count <= 60 && buttons.Count <= 6 && buttons.Count > 0;
        if (classic) return (true, body);

        if (rows.Count > 25 || dialogButtons.Count == 0 || buttons.Count > 4) return (false, null);
        if (rows.Any(r => r.Type is "DataGrid" or "List" or "Tree" or "Table" or "ComboBox" or "CheckBox" or "RadioButton" or "Tab" or "TabItem" or "MenuBar" or "ToolBar")) return (false, null);
        var edits = rows.Where(r => r.Type is "Edit" or "Document").ToList();
        if (edits.Count > 1) return (false, null);
        if (edits.Count == 1 && !bodyEdits.Contains(edits[0])) return (false, null); // an input field, not a body
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
