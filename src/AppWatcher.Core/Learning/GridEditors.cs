using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

/// <summary>
/// The editor a grid lays over a cell while the user types in it (the order lines of a sales order): Windows reports the
/// typed value on the editor, then the cell shows it. Decided per session from the events alone (pure, additive):
/// an editor without a name of its own is the column of the cell that took its value; the cell showing that same value
/// right after is the same typing, not a second one; and once a field's editor is known to hand its values to the grid,
/// a value typed in it that the grid never took (a partial code typed to search a lookup, a typing abandoned with Esc)
/// is no typing of the field. A value followed by a lookup list on the same field is a partial code too.
/// </summary>
public static class GridEditors
{
    /// <summary>How soon after the editor's value the cell shows it.</summary>
    public const double TakeSeconds = 3;

    /// <summary>How soon after a typed value a lookup list on the same field makes the value a partial code for the lookup.</summary>
    public const double LookupSeconds = 3;

    /// <summary>A value the user typed in an editor that is not a grid cell itself (no row).</summary>
    public static bool IsEditorValue(UiEvent e) =>
        e.Act == Acts.Value && e.By != ValueBy.App && e.Ct == "Edit" && string.IsNullOrEmpty(e.Row) && e.Val is { } v && v != Masking.Masked && v.Trim().Length > 0;

    /// <summary>A value that names nothing: the editor's FieldKey fell back to its control type (no name, an id that is a number).</summary>
    private static bool Unnamed(UiEvent e) => e.FieldKey == e.Ct;

    /// <summary>The columns of the cells of the same window that showed the editor's value (two characters at least) within <see cref="TakeSeconds"/>.</summary>
    private static List<UiEvent> Takers(List<UiEvent> sorted, int i)
    {
        var e = sorted[i];
        var v = (e.Val ?? "").Trim();
        var list = new List<UiEvent>();
        if (v.Length < 2) return list;
        var win = Titles.Key(e.Win);
        var named = !Unnamed(e);
        bool Takes(UiEvent c) => c.Act == Acts.Value && !string.IsNullOrEmpty(c.Row) && c.Val is { } cv && string.Equals(cv.Trim(), v, StringComparison.OrdinalIgnoreCase)
                                 && c.FieldKey != c.Ct && (!named || c.FieldKey == e.FieldKey) && Titles.Key(c.Win) == win;
        for (var j = i - 1; j >= 0 && (e.T - sorted[j].T).TotalSeconds <= 0.5; j--) if (Takes(sorted[j])) list.Add(sorted[j]);
        for (var j = i + 1; j < sorted.Count && (sorted[j].T - e.T).TotalSeconds <= TakeSeconds; j++) if (Takes(sorted[j])) list.Add(sorted[j]);
        return list;
    }

    /// <summary>An editor with no name whose value one column's cell took is that column ("typed:Edit" becomes "typed:Product Number").</summary>
    public static void NameEditors(List<UiEvent> session)
    {
        var sorted = session.OrderBy(e => e.T).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (!IsEditorValue(e) || !Unnamed(e)) continue;
            var columns = Takers(sorted, i).Select(c => c.FieldKey).Distinct().ToList();
            if (columns.Count == 1) e.Name = columns[0];
        }
    }

    /// <summary>The cells that showed the value just typed in their editor: the same typing, never a second one.</summary>
    public static HashSet<UiEvent> CellEchoes(List<UiEvent> session)
    {
        var set = new HashSet<UiEvent>(ReferenceEqualityComparer.Instance);
        var sorted = session.OrderBy(e => e.T).ToList();
        for (var i = 0; i < sorted.Count; i++)
            if (IsEditorValue(sorted[i]))
                foreach (var c in Takers(sorted, i)) if (c.By != ValueBy.App && c.FieldKey == sorted[i].FieldKey) set.Add(c);
        return set;
    }

    /// <summary>
    /// Typed values that are no typing of their field as a cause: followed by a lookup list on the same field within
    /// <see cref="LookupSeconds"/> (a partial code), or typed in an editor that hands its values to the grid (seen in this
    /// session) when the grid never took this one.
    /// </summary>
    public static HashSet<UiEvent> PartialCodes(List<UiEvent> session)
    {
        var set = new HashSet<UiEvent>(ReferenceEqualityComparer.Instance);
        var sorted = session.OrderBy(e => e.T).ToList();
        var taken = new HashSet<UiEvent>(ReferenceEqualityComparer.Instance);
        var gridEditors = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (!IsEditorValue(e) || Unnamed(e) || Takers(sorted, i).Count == 0) continue;
            taken.Add(e);
            gridEditors.Add(LearnState.Key(Titles.Key(e.Win), e.FieldKey));
        }
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (e.Act != Acts.Value || e.By == ValueBy.App) continue;
            var win = Titles.Key(e.Win); var f = e.FieldKey;
            var lookup = false;
            for (var j = i + 1; j < sorted.Count && (sorted[j].T - e.T).TotalSeconds <= LookupSeconds; j++)
                if (sorted[j].Act == Acts.Dropdown && sorted[j].FieldKey == f && Titles.Key(sorted[j].Win) == win) { lookup = true; break; }
            if (lookup || IsEditorValue(e) && !taken.Contains(e) && gridEditors.Contains(LearnState.Key(win, f))) set.Add(e);
        }
        return set;
    }
}
