namespace AppWatcher.Core.Screens;

/// <summary>
/// Tabbed workspaces (Infragistics UltraTabbedMdi, DevExpress document managers, WinForms TabControls): which tab
/// document is active, and which part of the window's tree is that document. Pure functions; the collector feeds them
/// what it reads. The tab strip of an MDI workspace raises no selection event, so the collector asks after any event
/// (at most every two seconds) which tab item says it is selected.
/// </summary>
public static class TabDocuments
{
    /// <summary>One tab item as read from the tab strip.</summary>
    public readonly record struct TabItem(string Name, bool Selected);

    /// <summary>
    /// The active tab from the tab items read now: the selected one, else none. A tab whose item is gone is no longer active,
    /// so the next events belong to the plain window again.
    /// </summary>
    public static string? Active(IEnumerable<TabItem> items) => Active(items, null);

    /// <summary>
    /// Same, knowing the tab that was active before: a strip whose items say nothing about selection (some toolkits answer
    /// false for every item) keeps the current tab as long as its item is still there; the tab is only gone when its item is.
    /// </summary>
    public static string? Active(IEnumerable<TabItem> items, string? current)
    {
        var list = items.Where(i => i.Name.Trim().Length > 0).Select(i => new TabItem(i.Name.Trim(), i.Selected)).ToList();
        var selected = list.FirstOrDefault(i => i.Selected).Name;
        if (!string.IsNullOrEmpty(selected)) return selected;
        if (current is not null && list.Any(i => i.Name == current)) return current;
        return null;
    }

    /// <summary>"Window › Tab": the screen title of a tab document.</summary>
    public static string Title(string windowTitle, string tab) => $"{windowTitle} › {tab}";

    /// <summary>
    /// A Tab control that lies inside a document (below a named Window or Pane that is not the top) is the document's own
    /// inner tab control (Main | General | Credit), never the workspace's document strip.
    /// </summary>
    public static bool IsInnerTab(IList<ControlRow> rows, int tabIndex)
    {
        var depth = rows[tabIndex].Depth;
        for (var i = tabIndex - 1; i >= 1 && depth > 1; i--)
        {
            if (rows[i].Depth >= depth) continue;
            depth = rows[i].Depth;
            if (rows[i].Type is "Window" && rows[i].Name.Trim().Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// The inner tab controls of a document and the tab item each one shows as selected (the collector marks the selected
    /// item's Value "selected": from a cached read of the selection, or from the page that is on screen). Keyed by the tab
    /// control's automation id, else its position.
    /// </summary>
    public static Dictionary<string, string> InnerTabSelections(IList<ControlRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type != "Tab") continue;
            var key = rows[i].AutomationId.Length > 0 ? rows[i].AutomationId : "tab@" + rows[i].Position;
            var depth = rows[i].Depth;
            string? selected = null;
            for (var j = i + 1; j < rows.Count && rows[j].Depth > depth; j++)
                if (rows[j].Type == "TabItem" && rows[j].Depth == depth + 1 && rows[j].Name.Trim().Length > 0 && rows[j].Value == "selected") { selected = rows[j].Name.Trim(); break; }
            if (selected is not null) result[key] = selected;
        }
        return result;
    }

    /// <summary>The tab items newly selected since the previous read: a user clicked them (or the program switched the page).</summary>
    public static List<string> InnerTabChanges(IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> now)
    {
        var list = new List<string>();
        foreach (var (key, name) in now)
            if (previous is null || !previous.TryGetValue(key, out var old) || old != name) list.Add(name);
        return list;
    }

    /// <summary>Names of the tab items in a control tree (the workspace tab strip's direct TabItem children), in order; inner tab controls of documents are skipped.</summary>
    public static List<string> TabNames(IList<ControlRow> rows)
    {
        var names = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type != "Tab" || IsInnerTab(rows, i)) continue;
            var depth = rows[i].Depth;
            for (var j = i + 1; j < rows.Count && rows[j].Depth > depth; j++)
                if (rows[j].Type == "TabItem" && rows[j].Depth == depth + 1 && rows[j].Name.Trim().Length > 0) names.Add(rows[j].Name.Trim());
        }
        return names;
    }

    private static readonly HashSet<string> Chrome = new(StringComparer.OrdinalIgnoreCase)
        { "ToolBar", "MenuBar", "StatusBar", "TitleBar", "Tab", "ScrollBar", "ToolTip" };

    /// <summary>
    /// The subtree of one tab document out of a window's tree: the Window or Pane named like the tab (an MDI child form), else
    /// the largest on-screen sibling of the tab strip that is not the window's toolbar, menu bar, status bar or another document.
    /// Returns the rows re-based at depth 0, or an empty list when no document can be told apart.
    /// </summary>
    public static List<ControlRow> Document(IList<ControlRow> rows, string tabName)
    {
        var key = Learning.Titles.Key(tabName);
        // 1. a Window or Pane named like the tab anywhere below the top: the MDI child itself
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Type is "Window" or "Pane" or "Custom" && r.Name.Length > 0 && Learning.Titles.Key(r.Name) == key && !Offscreen(r))
                return Subtree(rows, i);
        }
        // 2. the siblings of the tab strip: the document is the on-screen one with the most controls that is not window chrome
        var tabIndex = -1;
        for (var i = 1; i < rows.Count; i++) if (rows[i].Type == "Tab" && !IsInnerTab(rows, i)) { tabIndex = i; break; }
        if (tabIndex < 0) return new List<ControlRow>();
        var parentDepth = rows[tabIndex].Depth - 1;
        var parentIndex = -1;
        for (var i = tabIndex - 1; i >= 0; i--) if (rows[i].Depth == parentDepth) { parentIndex = i; break; }
        if (parentIndex < 0) return new List<ControlRow>();
        var best = -1; var bestCount = 0;
        for (var i = parentIndex + 1; i < rows.Count && rows[i].Depth > parentDepth; i++)
        {
            if (rows[i].Depth != parentDepth + 1 || i == tabIndex) continue;
            var r = rows[i];
            if (Chrome.Contains(r.Type) || Offscreen(r)) continue;
            var count = Subtree(rows, i).Count;
            if (count > bestCount) { best = i; bestCount = count; }
        }
        return best < 0 ? new List<ControlRow>() : Subtree(rows, best);
    }

    private static bool Offscreen(ControlRow r) => Labels.TryRect(r.Position, out _, out _, out var w, out var h) && (w <= 0 || h <= 0);

    private static List<ControlRow> Subtree(IList<ControlRow> rows, int index)
    {
        var depth = rows[index].Depth;
        var list = new List<ControlRow>();
        for (var i = index; i < rows.Count && (i == index || rows[i].Depth > depth); i++)
        {
            var r = rows[i];
            list.Add(new ControlRow { Depth = r.Depth - depth, Type = r.Type, Name = r.Name, AutomationId = r.AutomationId, Position = r.Position, IsPassword = r.IsPassword, IsReadOnly = r.IsReadOnly, Shortcut = r.Shortcut, LabeledBy = r.LabeledBy, Value = r.Value });
        }
        return list;
    }

    /// <summary>
    /// Rows of a tab record that are really the parent window's own controls (its toolbars, menu bar, status bar, other documents):
    /// the same type, name and automation id at the window level. Used by the learner on records made before the collector
    /// dumped documents on their own, so "not used" lists hold only the document's controls.
    /// </summary>
    public static List<ControlRow> WithoutParentControls(IList<ControlRow> tabRows, IList<ControlRow> parentRows)
    {
        var parent = new HashSet<string>(parentRows.Where(r => r.Depth > 0).Select(Sig), StringComparer.Ordinal);
        return tabRows.Where(r => r.Depth == 0 || !parent.Contains(Sig(r))).ToList();
    }

    private static string Sig(ControlRow r) => r.Type + "\u0001" + r.Name + "\u0001" + r.AutomationId;
}
