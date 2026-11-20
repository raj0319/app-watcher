namespace AppWatcher.Core.Screens;

/// <summary>A button-like control as the collector read it (never invoked, only read).</summary>
public sealed record PressCandidate(string Name, string Aid, string Ct, string Win);

/// <summary>
/// Decides which button a click went to, for toolkits whose buttons announce nothing (Infragistics toolbars raise no Invoke,
/// no pressed state, and a state change on hover). No hook: the collector samples whether the left button was pressed
/// since the last event it received (GetAsyncKeyState, a read) and, when it was, reads the control under the pointer at
/// once. That fresh read is the click's target; a control remembered from a pressed state change only wins when the
/// pointer has since moved more than <see cref="MovedPx"/> pixels (it left the button it pressed). The press is counted
/// when something confirms it within <see cref="ClickAgeMs"/>: a window opens or closes, a document is created or
/// destroyed, a message appears, an inner tab changes, or the program fills a value. One name within
/// <see cref="DedupeSeconds"/> with nothing in between is one press. Pure, so the rules are tested with synthetic clicks.
/// </summary>
public sealed class PressDecider
{
    public const int ClickAgeMs = 1500;
    public const int DedupeSeconds = 10;
    public const int MovedPx = 20;
    private static readonly HashSet<string> Actionable = new(StringComparer.Ordinal) { "Button", "SplitButton", "MenuItem", "Hyperlink", "CheckBox", "RadioButton" };

    private (PressCandidate? c, int x, int y, DateTime t) _pressed = (null, 0, 0, DateTime.MinValue);
    private (PressCandidate? c, DateTime t) _click = (null, DateTime.MinValue);
    private (string name, DateTime t) _last = ("", DateTime.MinValue);
    private int _eventsSinceLastPress;

    public static bool IsActionable(string? ct) => ct is not null && Actionable.Contains(ct);

    /// <summary>A control whose state gained PRESSED (or that took the mouse capture) under the pointer: remembered with where the pointer was.</summary>
    public void RememberPressed(PressCandidate c, int x, int y, DateTime now)
    {
        if (!IsActionable(c.Ct) || c.Name.Length == 0) return;
        _pressed = (c, x, y, now);
    }

    /// <summary>
    /// The left button went down since the previous event: <paramref name="underPointerNow"/> is the control read under the
    /// pointer right now (null when none or not a button). Decides the click's target.
    /// </summary>
    public void Clicked(PressCandidate? underPointerNow, int x, int y, DateTime now) => Clicked(underPointerNow, x, y, now, overPopup: false);

    /// <summary>
    /// Same, saying whether the pointer was over an open menu popup: then the click belongs to the menu (its item is counted
    /// when the menu closes), and nothing read or recorded from the window beneath the popup is a target.
    /// </summary>
    public void Clicked(PressCandidate? underPointerNow, int x, int y, DateTime now, bool overPopup)
    {
        if (overPopup) { _pressed = (null, 0, 0, DateTime.MinValue); _click = (null, now); return; }
        var fresh = underPointerNow is not null && IsActionable(underPointerNow.Ct) && underPointerNow.Name.Length > 0 ? underPointerNow : null;
        PressCandidate? remembered = null;
        if (_pressed.c is not null && (now - _pressed.t).TotalMilliseconds <= ClickAgeMs)
        {
            var moved = Math.Abs(x - _pressed.x) > MovedPx || Math.Abs(y - _pressed.y) > MovedPx;
            if (moved || fresh is null) remembered = _pressed.c;
        }
        var target = remembered ?? fresh;
        _pressed = (null, 0, 0, DateTime.MinValue);
        _click = (target, now);
    }

    /// <summary>True when the last press was counted within <paramref name="ms"/> before <paramref name="now"/>: a window that opens right after it was opened by it.</summary>
    public bool PressedWithin(int ms, DateTime now) => _last.t != DateTime.MinValue && (now - _last.t).TotalMilliseconds >= -50 && (now - _last.t).TotalMilliseconds < ms;

    /// <summary>True when a click began within the last <see cref="ClickAgeMs"/> (a toggle needs one, or focus on the tick box).</summary>
    public bool ClickedRecently(DateTime now) => _click.t != DateTime.MinValue && (now - _click.t).TotalMilliseconds <= ClickAgeMs;

    /// <summary>The click's target still waiting for something to confirm it, or null.</summary>
    public PressCandidate? Pending => _click.c;

    /// <summary>A better read of the same click arrived (the live element under the pointer): replaces the target unless the click was confirmed meanwhile.</summary>
    public void Refine(PressCandidate c, DateTime clickedAt)
    {
        if (_click.c is null || _click.t != clickedAt) return;
        if (!IsActionable(c.Ct) || c.Name.Length == 0) return;
        _click = (c, clickedAt);
    }

    /// <summary>
    /// Something that a click explains just happened: the control the click went to, if one is pending and fresh, else null.
    /// The candidate is cleared so a second confirmation (a window opened and a tab changed) counts it once.
    /// </summary>
    public PressCandidate? Confirm(DateTime now, Func<string, bool>? sameWindow = null)
    {
        var (c, t) = _click;
        if (c is null) return null;
        var age = (now - t).TotalMilliseconds;
        if (age < -50 || age > ClickAgeMs) return null;
        if (sameWindow is not null && c.Win.Length > 0 && !sameWindow(c.Win)) return null;
        _click = (null, DateTime.MinValue);
        return c;
    }

    /// <summary>Call for every event the collector emits, so "nothing between" can be told.</summary>
    public void EventEmitted() => _eventsSinceLastPress++;

    /// <summary>True when this press is a repeat of the last one (same name within <see cref="DedupeSeconds"/> and no event in between, or within 1.5 s regardless).</summary>
    public bool IsRepeat(string name, DateTime now)
    {
        if (_last.name != name) return false;
        var ms = (now - _last.t).TotalMilliseconds;
        if (ms < ClickAgeMs) return true;
        return ms < DedupeSeconds * 1000 && _eventsSinceLastPress == 0;
    }

    public void Pressed(string name, DateTime now)
    {
        _last = (name, now);
        _eventsSinceLastPress = 0;
        if (_click.c?.Name == name) _click = (null, DateTime.MinValue);
        if (_pressed.c?.Name == name) _pressed = (null, 0, 0, DateTime.MinValue);
    }

    /// <summary>A tick box change is the user's when a click just began or the box has the focus; otherwise the program changed it.</summary>
    public static bool ToggleIsUserAction(bool clickedRecently, bool boxHasFocus) => clickedRecently || boxHasFocus;
}
