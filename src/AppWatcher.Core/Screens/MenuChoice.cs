namespace AppWatcher.Core.Screens;

/// <summary>
/// Which menu item was chosen when a menu closed. Pure: the collector hands it what it saw.
/// <list type="bullet">
/// <item>The item read under the pointer when the button went down inside the open menu wins: it is the click itself (Qt items
/// raise no focus event at all; a WPF item's focus event can come after the menu closed).</item>
/// <item>Else the item last highlighted or focused, when that happened after the menu opened (the menu-bar item, "File", is
/// highlighted before its popup opens) and within 1.5 s before the close, or up to 0.4 s after it (a late focus event).</item>
/// <item>Escape closed it: nothing was chosen.</item>
/// </list>
/// </summary>
public static class MenuChoice
{
    public const int LateFocusMs = 400;

    public static string? Pick((string name, DateTime t) clicked, (string name, DateTime t) focused, DateTime openedAt, DateTime closedAt, bool escaped)
    {
        if (escaped) return null;
        if (clicked.name.Length > 0 && clicked.t != DateTime.MinValue && (openedAt == DateTime.MinValue || clicked.t >= openedAt.AddMilliseconds(-50))
            && (closedAt - clicked.t).TotalMilliseconds is >= -LateFocusMs and <= 3000)
            return Labels.WithoutShortcut(clicked.name);
        if (focused.name.Length == 0 || focused.t == DateTime.MinValue) return null;
        var age = (closedAt - focused.t).TotalMilliseconds;
        if (age > 1500 || age < -LateFocusMs) return null;
        if (openedAt != DateTime.MinValue && focused.t < openedAt) return null;
        return Labels.WithoutShortcut(focused.name);
    }
}
