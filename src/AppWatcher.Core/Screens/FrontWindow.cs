namespace AppWatcher.Core.Screens;

/// <summary>What the collector knows of one window when it decides which window the user works in.</summary>
public readonly record struct WindowFacts(long Hwnd, string Title, bool Visible, bool Program, bool MenuPopup = false, bool Excluded = false);

/// <summary>
/// Which window the user works in after the one in front went away (hidden or destroyed with no foreground event yet, or a
/// late event that names an untitled drop-down). Pure: the collector hands it the window in front and its owners in order.
/// Only a visible, titled, non-excluded window of the program qualifies; an untitled helper (a lookup list, a drop-down, a
/// tip) or a menu popup never does, it hands over to its owner. Inform r39: letting the read follow the untitled lookup list
/// filed the lookup under the bare main title instead of its document.
/// </summary>
public static class FrontWindow
{
    /// <summary>The first qualifying window of the chain (the window in front, then its owners), or null when none does.</summary>
    public static WindowFacts? Pick(IReadOnlyList<WindowFacts> chain, long current)
    {
        foreach (var w in chain)
        {
            if (w.Hwnd == 0) continue;
            if (!w.Program) return null;            // another program is in front: the program is not
            if (w.MenuPopup || !w.Visible || w.Title.Trim().Length == 0) continue;
            if (w.Excluded) return null;
            return w.Hwnd == current ? null : w;    // already the window in front: nothing to change
        }
        return null;
    }
}
