namespace AppWatcher.Core.Screens;

/// <summary>
/// Hit-testing on a recorded control tree: which button, menu item, tick box, link or tab item lies under a screen point,
/// from the rows the collector dumped (their Position column holds screen pixels). Used when a click's target has already
/// gone when the collector hears of the click (the toolkit announces nothing, and the next event is the destroy of the
/// dialog or document the button closed), so a live read under the pointer finds only the window behind. Pure.
/// </summary>
public static class TreeHitTest
{
    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal) { "Button", "SplitButton", "MenuItem", "CheckBox", "RadioButton", "Hyperlink", "TabItem" };

    /// <summary>The deepest clickable row whose rectangle holds the point; null when none (a gap, a label, an offscreen or zero-size row).</summary>
    public static ControlRow? ButtonAt(IReadOnlyList<ControlRow> rows, int x, int y) => HitAt(rows, x, y) is { } h ? rows[h] : null;

    /// <summary>Same, as the row's index (so the caller can find the Tab control a tab item belongs to).</summary>
    public static int? HitAt(IReadOnlyList<ControlRow> rows, int x, int y)
    {
        int? best = null; var bestDepth = -1; var bestArea = long.MaxValue;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!Targets.Contains(r.Type) || r.Name.Trim().Length == 0) continue;
            if (!Labels.TryRect(r.Position, out var rx, out var ry, out var rw, out var rh) || rw <= 0 || rh <= 0) continue;
            if (x < rx || x >= rx + rw || y < ry || y >= ry + rh) continue;
            var area = (long)rw * rh;
            // deeper wins (a button inside a pane over the pane's own button-like parent); same depth: the smaller rectangle
            if (r.Depth > bestDepth || (r.Depth == bestDepth && area < bestArea)) { best = i; bestDepth = r.Depth; bestArea = area; }
        }
        return best;
    }

    /// <summary>
    /// The recorded trees a click is resolved on, in order, each with the point to test: the window being destroyed (the
    /// button that closed it), the window in front, the window that was in front at the previous event when it changed
    /// since (the click went to it before the switch), and, when the pointer moved since the previous event, that window
    /// at the previous position. A document of an MDI workspace is followed by its container: the container's own buttons
    /// (close the workspace, a toolbar) are not in the document's tree. Handles are opaque here; zero means none.
    /// </summary>
    public static List<(long hwnd, int x, int y)> Surfaces(long destroyed, long front, long prevFront, int x, int y, int prevX, int prevY, bool moved, Func<long, long> containerOf)
    {
        var list = new List<(long, int, int)>();
        void Add(long h, int px, int py)
        {
            if (h == 0 || list.Contains((h, px, py))) return;
            list.Add((h, px, py));
            var top = containerOf(h);
            if (top != 0 && top != h && !list.Contains((top, px, py))) list.Add((top, px, py));
        }
        Add(destroyed, x, y);
        Add(front, x, y);
        if (prevFront != front) Add(prevFront, x, y);
        if (moved) Add(prevFront, prevX, prevY);
        return list;
    }

    /// <summary>The key of the Tab control a tab item row belongs to (its automation id, else its position), as <see cref="TabDocuments.InnerTabSelections"/> keys it.</summary>
    public static string? TabKeyOf(IReadOnlyList<ControlRow> rows, int tabItemIndex)
    {
        var depth = rows[tabItemIndex].Depth;
        for (var i = tabItemIndex - 1; i >= 0; i--)
        {
            if (rows[i].Depth >= depth) continue;
            depth = rows[i].Depth;
            if (rows[i].Type == "Tab") return rows[i].AutomationId.Length > 0 ? rows[i].AutomationId : "tab@" + rows[i].Position;
        }
        return null;
    }
}
