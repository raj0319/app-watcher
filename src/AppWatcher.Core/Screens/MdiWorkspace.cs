namespace AppWatcher.Core.Screens;

/// <summary>
/// The documents of an MDI workspace (Infragistics UltraTabbedMdi, plain WinForms MDI): each document is a child window
/// whose parent's class name contains MDICLIENT. The collector feeds this model the window events it already receives
/// (create, show, focus, destroy) and the z-order it reads; the model says which document is active and which documents
/// were ever shown. Pure, so it is tested with synthetic windows. Keys are window handles (as numbers).
/// </summary>
public sealed class MdiWorkspace
{
    private readonly List<long> _order = new();            // front first
    private readonly Dictionary<long, string> _titles = new();
    private readonly List<string> _seen = new();

    public static bool IsMdiClientClass(string? className) => className is not null && className.Contains("MDICLIENT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The title of the document in front, or null when no document is open.</summary>
    public string? Active => _order.Count == 0 ? null : _titles[_order[0]];

    public int Count => _order.Count;

    /// <summary>Every document title shown so far, in order of first appearance.</summary>
    public IReadOnlyList<string> TitlesSeen => _seen;

    public bool Contains(long key) => _titles.ContainsKey(key);

    public string? TitleOf(long key) => _titles.GetValueOrDefault(key);

    /// <summary>A document window was created or shown: it comes to the front. Returns the active title.</summary>
    public string? Shown(long key, string title)
    {
        title = title.Trim();
        _titles[key] = title;
        _order.Remove(key);
        _order.Insert(0, title.Length > 0 ? key : key);
        if (title.Length > 0 && !_seen.Contains(title)) _seen.Add(title);
        return Active;
    }

    /// <summary>A document window got the focus or was read in front of the others.</summary>
    public string? Activated(long key)
    {
        if (!_titles.ContainsKey(key)) return Active;
        _order.Remove(key);
        _order.Insert(0, key);
        return Active;
    }

    /// <summary>The window's title changed (a document renamed after a record loaded).</summary>
    public void Retitle(long key, string title)
    {
        if (!_titles.ContainsKey(key)) return;
        title = title.Trim();
        if (title.Length == 0) return;
        _titles[key] = title;
        if (!_seen.Contains(title)) _seen.Add(title);
    }

    /// <summary>A document window was destroyed. Returns the title now active, or null when none is left.</summary>
    public string? Destroyed(long key)
    {
        _order.Remove(key);
        _titles.Remove(key);
        return Active;
    }

    /// <summary>The z-order as read from the windows (front first): windows the model never saw are added, gone ones dropped.</summary>
    public string? Reorder(IEnumerable<(long key, string title)> frontFirst)
    {
        var list = frontFirst.ToList();
        var keep = new HashSet<long>(list.Select(x => x.key));
        foreach (var k in _order.Where(k => !keep.Contains(k)).ToList()) { _order.Remove(k); _titles.Remove(k); }
        _order.Clear();
        foreach (var (key, title) in list)
        {
            var t = title.Trim();
            if (!_titles.TryGetValue(key, out var old) || (t.Length > 0 && old != t)) _titles[key] = t.Length > 0 ? t : _titles.GetValueOrDefault(key, "");
            if (t.Length > 0 && !_seen.Contains(t)) _seen.Add(t);
            _order.Add(key);
        }
        return Active;
    }

    public void Clear() { _order.Clear(); _titles.Clear(); }
}
