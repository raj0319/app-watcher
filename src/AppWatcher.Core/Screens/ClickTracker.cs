namespace AppWatcher.Core.Screens;

/// <summary>
/// What the collector can read about the watched program's windows at the moment a click is heard of: Win32 reads and the
/// trees it keeps in memory, never a hook and never a write. The collector implements it over real handles; the tests
/// implement it over a recorded run, so the live order of events (hover, click, foreground switch, destroy within 200 ms)
/// runs through the same decisions as on the client's PC.
/// </summary>
public interface IWindowWorld
{
    /// <summary>The last tree read from this window handle (never a record found by title), or null.</summary>
    IReadOnlyList<ControlRow>? RowsOf(long hwnd);
    /// <summary>True while the window (and its parents) is shown.</summary>
    bool IsVisible(long hwnd);
    /// <summary>The deepest window of the watched program at the point now (WindowFromPoint), 0 when the point is not on it.</summary>
    long WindowAt(int x, int y);
    /// <summary>The top-level window of a handle.</summary>
    long RootOf(long hwnd);
    /// <summary>The MDI document window holding the handle (itself when it is one), 0 when none.</summary>
    long DocumentOf(long hwnd);
    /// <summary>The screen title a hit in this window's tree belongs to: "Main › Document" for a document, the plain title for a top window.</summary>
    string ScreenOf(long hwnd);
    /// <summary>True when the point lies on a menu popup of the program that is open, or that is <paramref name="goingAway"/>.</summary>
    bool MenuPopupAt(int x, int y, long goingAway);
}

/// <summary>
/// One sample of the input state, taken at an event the collector already receives: where the pointer is now, whether the
/// left button is down now (bit 15 of GetAsyncKeyState) and went down since the last sample (bit 0), whether Enter or Tab
/// went down, the window in front now, and the window being destroyed or hidden by this event (0 when none).
/// <c>PressKeyWentDown</c>: Enter or Space went down (the keys that press the focused button; Tab only moves the focus).
/// </summary>
public sealed record InputSample(DateTime T, int X, int Y, bool ButtonDown, bool WentDown, bool KeyWentDown, long Front, long GoingAway = 0, bool PressKeyWentDown = false);

/// <summary>A click resolved on a recorded tree: the window, the row, and whether that window is still on screen (then a live read checks it).</summary>
public sealed record ClickHit(long Hwnd, int Index, ControlRow Row, bool Live, string Screen);

public enum ClickKind { None, Menu, Tab, Target, NoTarget }

/// <summary>What one sample meant: no click, a click on a menu, on a tab item, on a button (a target), or on nothing known.</summary>
public sealed record ClickOutcome(ClickKind Kind, DateTime ClickedAt, ClickHit? Hit, bool NeedsLiveRead, string Trace, int X = 0, int Y = 0)
{
    public static readonly ClickOutcome None = new(ClickKind.None, DateTime.MinValue, null, false, "");
}

/// <summary>The control a live read (ElementFromPoint) found under the click point, and whether it is in the window the click was resolved on.</summary>
public sealed record LiveElement(string Name, string Aid, string Ct, string Position, string Screen, bool SameSurface);

/// <summary>What a live read changed: a tab page clicked, a press now confirmed by a change that waited for the read, or nothing.</summary>
public sealed record LiveResult(string? TabPage, PressCandidate? Confirmed, bool Cancelled);

/// <summary>
/// The left button's clicks, from samples of the key state at events: a click is a down seen since the last sample (bit 0)
/// followed by the button being up at a sample. A button still down is a click in progress; a sample with no down seen is
/// never a click, whatever the pointer rests on. Pure.
/// </summary>
public sealed class ClickSampler
{
    private bool _down;

    /// <summary>A down was seen and the button has not been seen up since: a click in progress.</summary>
    public bool DownSeen => _down;

    /// <summary>True when a click completed at this sample.</summary>
    public bool Sample(bool buttonDownNow, bool wentDownSinceLast)
    {
        if (wentDownSinceLast) _down = true;
        if (_down && !buttonDownNow) { _down = false; return true; }
        return false;
    }
}

/// <summary>
/// Which recorded tree a click is resolved on. First a window that is going away (destroyed or hidden by this very event,
/// or the window that was in front at the previous sample and is hidden now: a message box loses the foreground before it
/// closes), with its top window after it when it is a document. Else the window really under the point now (WindowFromPoint):
/// the document there when the point is inside one, else its top window. Never a tree found by title, never a point kept
/// from an earlier event, never the top window's tree for a point inside a document that has no tree of its own. Pure.
/// </summary>
public static class ClickSurfaces
{
    public static ClickHit? Resolve(IWindowWorld w, int x, int y, long goingAway, long front, long prevFront, List<string>? trace = null)
    {
        // 1. a window that is going away: its tree is all there is (a live read would find the window behind)
        var leaving = new List<long>();
        if (goingAway != 0) leaving.Add(goingAway);
        if (prevFront != 0 && prevFront != front && prevFront != goingAway && !w.IsVisible(prevFront)) leaving.Add(prevFront);
        foreach (var h in leaving)
        {
            foreach (var s in new[] { h, w.RootOf(h) }.Where(s => s != 0).Distinct())
            {
                var gone = HitIn(w, s, x, y, live: false, trace);
                if (gone is not null) return gone;
            }
        }
        // 2. the window under the point now
        var at = w.WindowAt(x, y);
        if (at == 0) { trace?.Add("no window of the program under the point"); return null; }
        var doc = w.DocumentOf(at);
        var surface = doc != 0 ? doc : w.RootOf(at);
        if (surface == 0) surface = at;
        if (doc != 0 && w.RowsOf(doc) is null) { trace?.Add($"{doc}: a document with no tree of its own (the top window's tree is not used inside it)"); return null; }
        var hit = HitIn(w, surface, x, y, live: true, trace);
        // nothing there in the top window's tree: the document in front of it (whose own window the point may not report,
        // a toolkit drawing the page in a pane) is tried next, as v0.10 did
        if (hit is null && doc == 0 && front != 0 && front != surface && w.RootOf(front) == surface && w.DocumentOf(front) == front)
            hit = HitIn(w, front, x, y, live: true, trace);
        return hit;
    }

    private static ClickHit? HitIn(IWindowWorld w, long hwnd, int x, int y, bool live, List<string>? trace)
    {
        var rows = w.RowsOf(hwnd);
        if (rows is null || rows.Count == 0) { trace?.Add($"{hwnd}: no tree"); return null; }
        var i = TreeHitTest.HitAt(rows, x, y);
        trace?.Add($"{hwnd}{(live ? "" : " (going away)")}: {(i is { } k ? rows[k].Type + " " + rows[k].Name : "nothing at the point")}");
        return i is { } j ? new ClickHit(hwnd, j, rows[j], live, w.ScreenOf(hwnd)) : null;
    }

    /// <summary>
    /// A live element agrees with a recorded row when one rectangle holds the other (the button itself, a part of it, or a
    /// toolbar strip whose tools are not separate elements). A field where the tree has a button means the tree is stale.
    /// </summary>
    public static bool RectsAgree(string rowPosition, string livePosition, int slack = 3)
    {
        if (!Labels.TryRect(rowPosition, out var rx, out var ry, out var rw, out var rh) || !Labels.TryRect(livePosition, out var lx, out var ly, out var lw, out var lh)) return true;
        if (rw <= 0 || lw <= 0) return true;
        static bool Inside(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh, int s) => ax >= bx - s && ay >= by - s && ax + aw <= bx + bw + s && ay + ah <= by + bh + s;
        return Inside(lx, ly, lw, lh, rx, ry, rw, rh, slack) || Inside(rx, ry, rw, rh, lx, ly, lw, lh, slack);
    }
}

/// <summary>
/// The click and press decisions of the collector in one place, so the fixture replay runs the same code as the live
/// collector: samples become clicks (<see cref="ClickSampler"/>), clicks are resolved on a tree (<see cref="ClickSurfaces"/>),
/// a live read refines or cancels the answer, and a change in the program (a window closed, a value filled) confirms the
/// press within <see cref="PressDecider.ClickAgeMs"/>. A press needs a click (or Enter for the focused button): a hovered
/// control or the control under a resting pointer is never pressed. Pure; not thread safe (the collector locks around it).
/// </summary>
public sealed class ClickTracker
{
    public PressDecider Decider { get; } = new();
    private readonly ClickSampler _sampler = new();
    private DateTime _clickAt = DateTime.MinValue, _readDone = DateTime.MinValue, _keyAt = DateTime.MinValue;
    private int _pressesWithoutClick;
    private ClickHit? _liveHit;
    private int _clickX, _clickY, _downX, _downY;
    private DateTime _downAt = DateTime.MinValue;
    private (string why, string from, DateTime at)? _pendingConfirm;
    private long _prevFront;
    private bool _menuOpen;
    private DateTime _menuClosedAt = DateTime.MinValue;
    private (string name, string how, DateTime t) _lastPress = ("", "", DateTime.MinValue);

    /// <summary>When Enter or Space last went down (MinValue when never).</summary>
    public DateTime PressKeyAt => _keyAt;

    /// <summary>Where and when the last click was heard (its point), MinValue when none.</summary>
    public (int x, int y, DateTime t) LastClick => (_clickX, _clickY, _clickAt);

    /// <summary>The time of the last click, MinValue when none.</summary>
    public DateTime LastClickAt => _clickAt;

    /// <summary>The left button went down and has not been seen up yet (a pressed state now belongs to a real click).</summary>
    public bool ButtonDownSeen => _sampler.DownSeen;

    /// <summary>One sample at an event. Returns what it meant.</summary>
    public ClickOutcome Observe(InputSample s, IWindowWorld w)
    {
        var prevFront = _prevFront;
        _prevFront = s.Front;
        if (s.PressKeyWentDown) _keyAt = s.T;
        // the point of a click is where the pointer was when the button was first seen down (a sample taken later, when the
        // button is up again, may come after the pointer moved on)
        var wasDown = _sampler.DownSeen;
        if (s.ButtonDown && !wasDown) { _downX = s.X; _downY = s.Y; _downAt = s.T; }
        if (!_sampler.Sample(s.ButtonDown, s.WentDown)) return ClickOutcome.None;
        var useDown = wasDown && _downAt != DateTime.MinValue && (s.T - _downAt).TotalMilliseconds <= 1500;
        var x = useDown ? _downX : s.X; var y = useDown ? _downY : s.Y;
        _downAt = DateTime.MinValue;
        s = s with { X = x, Y = y };
        _clickAt = s.T; _clickX = s.X; _clickY = s.Y; _pendingConfirm = null; _liveHit = null;
        // a click while a menu is open, a moment after it closed, or right after a menu item was counted belongs to the menu
        var menuClick = _menuOpen || (s.T - _menuClosedAt).TotalMilliseconds is >= 0 and < 300
                        || (_lastPress.how == "menu" && (s.T - _lastPress.t).TotalMilliseconds is >= 0 and < 1000)
                        || w.MenuPopupAt(s.X, s.Y, s.GoingAway);
        if (menuClick)
        {
            Decider.Clicked(null, s.X, s.Y, s.T, overPopup: true);
            _readDone = s.T;
            return new ClickOutcome(ClickKind.Menu, s.T, null, false, "a menu click: no target beneath the popup", s.X, s.Y);
        }
        var trace = new List<string>();
        var hit = ClickSurfaces.Resolve(w, s.X, s.Y, s.GoingAway, s.Front, prevFront, trace);
        var text = string.Join("; ", trace);
        if (hit is { } t && t.Row.Type == "TabItem")
        {
            Decider.Clicked(null, s.X, s.Y, s.T);
            _readDone = s.T;
            return new ClickOutcome(ClickKind.Tab, s.T, t, false, text, s.X, s.Y);
        }
        var c = hit is null ? null : new PressCandidate(hit.Row.Name.Trim(), hit.Row.AutomationId, hit.Row.Type, hit.Screen);
        Decider.Clicked(c, s.X, s.Y, s.T);
        // a window going away cannot be read live; a window on screen is checked against the live element under the point
        var needsRead = hit is null ? w.WindowAt(s.X, s.Y) != 0 : hit.Live;
        _readDone = needsRead ? DateTime.MinValue : s.T;
        _liveHit = hit is { Live: true } ? hit : null;
        return new ClickOutcome(hit is null ? ClickKind.NoTarget : ClickKind.Target, s.T, hit, needsRead, text, s.X, s.Y);
    }

    /// <summary>
    /// The live read of a click came back: an actionable control refines the target; a different control where the tree had a
    /// button cancels it (the tree was stale); a read from a window that is no longer the one the click went to is ignored.
    /// A change that came while the read was in flight is confirmed now.
    /// </summary>
    public LiveResult LiveRead(DateTime clickedAt, LiveElement? el)
    {
        if (clickedAt != _clickAt) return new LiveResult(null, null, false);
        _readDone = clickedAt;
        string? tab = null; var cancelled = false; var agreed = false;
        if (el is not null && el.SameSurface)
        {
            if (el.Ct == "TabItem" && el.Name.Length > 0 && _liveHit is null && Decider.Pending is null) { tab = el.Name; Decider.Clicked(null, _clickX, _clickY, clickedAt); }
            else if (PressDecider.IsActionable(el.Ct) && el.Name.Length > 0)
            {
                var c = new PressCandidate(el.Name, el.Aid, el.Ct, el.Screen);
                if (Decider.Pending is null) Decider.Clicked(c, _clickX, _clickY, clickedAt); else Decider.Refine(c, clickedAt);
                // the recorded tree and the live element agree on a button at the click point: the click itself is the evidence
                // (Add Line Item changes nothing the re-read could compare: Inform already added the empty line after Enter)
                if (el.Ct is "Button" or "SplitButton" or "Hyperlink" && _liveHit is { } lh && string.Equals(lh.Row.Name.Trim(), el.Name.Trim(), StringComparison.Ordinal))
                    agreed = true;
            }
            else if (_liveHit is { } h && !ClickSurfaces.RectsAgree(h.Row.Position, el.Position))
            {
                Decider.Cancel(clickedAt);
                cancelled = true;
            }
        }
        PressCandidate? confirmed = null;
        if (_pendingConfirm is { } p) { _pendingConfirm = null; confirmed = Decider.Confirm(p.at, win => ScreensRelated(win, p.from), MaxAgeFor(p.why, Decider.Pending)); }
        if (confirmed is null && agreed) confirmed = Decider.Confirm(clickedAt);
        return new LiveResult(tab, confirmed, cancelled);
    }

    /// <summary>True while the live read of the last click has not come back (at most <see cref="PressDecider.ClickAgeMs"/>).</summary>
    public bool ReadInFlight(DateTime now) => _clickAt != DateTime.MinValue && _readDone < _clickAt && (now - _clickAt).TotalMilliseconds <= PressDecider.ClickAgeMs;

    /// <summary>
    /// Something a click explains happened at <paramref name="at"/> (a window closed or changed, a value filled, a tab
    /// changed) on the screen <paramref name="from"/>: the click's target when it is fresh and on that screen or a related one
    /// (a window and its documents). While the live read is in flight the confirmation waits for it (<paramref name="deferred"/>).
    /// </summary>
    public PressCandidate? Confirm(DateTime at, string why, string from, out bool deferred)
    {
        deferred = false;
        // while the live read is in flight the target may still be refined or found stale: the confirmation waits for it
        if (ReadInFlight(at)) { _pendingConfirm ??= (why, from, at); deferred = true; return null; }
        return Decider.Confirm(at, win => ScreensRelated(win, from), MaxAgeFor(why, Decider.Pending));
    }

    /// <summary>
    /// How old a click may be for this change to confirm it: a button of a main window (not inside a document) may take up to
    /// <see cref="PressDecider.DocumentOpenMs"/> to open its document; every other change must come within
    /// <see cref="PressDecider.ClickAgeMs"/>. Any later click replaces the target, so the long wait never spans two clicks.
    /// </summary>
    public static int MaxAgeFor(string why, PressCandidate? pending) =>
        why is "tab changed" or "window opened" or "window changed" or "document opened"
        && pending is { Ct: "Button" or "SplitButton" or "MenuItem" } p && !p.Win.Contains(" › ", StringComparison.Ordinal)
            ? PressDecider.DocumentOpenMs : PressDecider.ClickAgeMs;

    /// <summary>A press was written (by any path): remembered for the menu rule and for "a window that opens right after a press is its doing".</summary>
    public void Pressed(string name, string how, DateTime now)
    {
        _lastPress = (name, how, now);
        Decider.Pressed(name, now);
    }

    /// <summary>True when a press was written within <paramref name="ms"/> before <paramref name="now"/>.</summary>
    public bool PressedWithin(int ms, DateTime now) => _lastPress.t != DateTime.MinValue && (now - _lastPress.t).TotalMilliseconds is >= -50 && (now - _lastPress.t).TotalMilliseconds < ms;

    /// <summary>A click completed within the last <paramref name="ms"/>.</summary>
    public bool ClickedWithin(DateTime now, int ms = PressDecider.ClickAgeMs) => _clickAt != DateTime.MinValue && (now - _clickAt).TotalMilliseconds is >= -50 && (now - _clickAt).TotalMilliseconds <= ms;

    /// <summary>
    /// The focused button may be credited only after Enter or Space (the keys that press it). A click is never enough: the click
    /// has its own point, and a button that merely holds the focus (Edit after a record loads, the default button of a
    /// window the program opened) was not where the click went. All six wrong names of the v0.11 run came from that.
    /// </summary>
    public bool MayCreditFocused(DateTime now) => _keyAt != DateTime.MinValue && (now - _keyAt).TotalMilliseconds is >= -50 and <= PressDecider.ClickAgeMs;

    /// <summary>The control under the pointer may be credited for a window change only when a click came just before and found no target.</summary>
    public bool MayCreditPointer(DateTime now) => ClickedWithin(now) && Decider.Pending is null && !PressedWithin(1000, now);

    public void MenuOpened() => _menuOpen = true;
    public void MenuClosed(DateTime now) { _menuOpen = false; _menuClosedAt = now; }

    /// <summary>The same screen, or a window and one of its documents ("Main" and "Main › Order"), compared by title key.</summary>
    public static bool ScreensRelated(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return true;
        var x = Learning.Titles.Key(a); var y = Learning.Titles.Key(b);
        return x == y || x.StartsWith(y + " › ", StringComparison.Ordinal) || y.StartsWith(x + " › ", StringComparison.Ordinal);
    }
}
