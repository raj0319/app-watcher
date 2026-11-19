using System.Threading.Channels;
using AppWatcher.Core;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Native;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.Core.Identifiers;
using FlaUI.UIA3;

namespace AppWatcher.Collector;

/// <summary>Raw event from a hook or a UIA callback, before it is written. Cheap to create.</summary>
internal sealed class RawEvent
{
    public DateTime T = DateTime.Now;
    public string Act = "";
    public string Ct = "";
    public string Name = "";
    public string Aid = "";
    public string? Val;
    public string? How;
    public bool IsPassword;
    public IntPtr Hwnd;
    public string? WindowTitle;
    /// <summary>Set for window-open events: the dumped control tree.</summary>
    public ScreenRecord? Screen;
    public bool IsDialog;
    /// <summary>Dropdown suggestions, grid rows, or the items of a menu that opened.</summary>
    public List<string>? Items;
    /// <summary>ValueBy.App when the program changed the value itself.</summary>
    public string? By;
    /// <summary>An inferred press that came from the control under the pointer (counted once it is emitted).</summary>
    public bool FromPointer;
}

/// <summary>
/// Subscribes to UI Automation events of ONE process (focus, invoke, selection, toggle, value and text changes)
/// and to WinEvents for that process (window open/close/title, menus, mouse capture, MSAA invoke/focus/state) plus
/// the global foreground change. Never installs a keyboard or mouse hook, never presses anything. Produces RawEvents
/// into a channel; does no file work itself.
/// Presses are seen five ways, first one wins per click: UIA Invoke, MSAA EVENT_OBJECT_INVOKED or a pressed state,
/// a menu closing right after a menu item was highlighted, and (inferred) the control under the pointer at the moment
/// the mouse was captured or a state changed, or the button that had focus, when within 1.5 s a window opens, closes
/// or changes, a tab changes, a message appears, or a value arrives on another control. Toolkits whose buttons
/// announce nothing (Infragistics, DevExpress toolbars) are covered by the pointer path.
/// Tab documents: the tab strip is read after events (at most every two seconds) because MDI tab strips raise no
/// selection event; only the selected document's subtree is a screen, and its fields are re-read once after each
/// typed value or press to find what the program filled in (grid cells and bound editors report no change).
/// </summary>
internal sealed class UiaCollector : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ChannelWriter<RawEvent> _out;
    private readonly List<WinEventHook> _hooks = new();
    private readonly Dictionary<IntPtr, List<IDisposable>> _perWindow = new();
    private readonly HashSet<string> _dumpedTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, List<ControlRow>> _rows = new();
    private readonly Dictionary<IntPtr, DateTime> _lastDump = new();
    private readonly Dictionary<IntPtr, string> _activeTab = new();
    private readonly Dictionary<IntPtr, AutomationElement> _tabStrips = new();
    private readonly Dictionary<IntPtr, DateTime> _lastTabPoll = new();
    private readonly HashSet<string> _tabsSeen = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> _menuPopups = new();
    private readonly CollectorSettings _settings;
    private readonly PrivacySettings _privacy;
    private readonly CollectorStatus _status;
    private FocusChangedEventHandlerBase? _focus;
    private readonly SynchronizationContext _ui;
    private uint _pid;
    private IntPtr _foreground;
    private volatile bool _inFront;
    private string _currentTitle = "";
    private string _previousTitle = "";
    private readonly Dictionary<IntPtr, string> _knownWindows = new();
    private readonly object _gate = new();
    // press bookkeeping (any thread)
    private (string name, string aid, string ct, string win, DateTime t) _lastActionable = ("", "", "", "", DateTime.MinValue);
    private (string name, string aid, string ct, string win, DateTime t, System.Drawing.Point at) _pointer = ("", "", "", "", DateTime.MinValue, default);
    private DateTime _lastPointerRead = DateTime.MinValue;
    private System.Drawing.Point _lastPointerPress = new(-9999, -9999);
    private (string name, DateTime t) _lastPress = ("", DateTime.MinValue);
    private (string name, DateTime t) _lastMenuItem = ("", DateTime.MinValue);
    private DateTime _menuOpenedAt = DateTime.MinValue;
    private int _stateEventsThisSecond; private DateTime _stateSecond = DateTime.MinValue;
    private (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) _lastField = ("", "", "", DateTime.MinValue, default);
    private readonly Dictionary<string, string> _lastTyped = new();
    private bool _firstWindow = true;
    // document re-reads (what the program filled in)
    private readonly Dictionary<string, List<FieldRead>> _fieldReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ControlRow>> _fieldRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _userKeysSinceRead = new(StringComparer.Ordinal);
    private System.Threading.Timer? _rereadTimer;
    private string _rereadWhy = "";
    private int _rereadBusy;

    public UiaCollector(ChannelWriter<RawEvent> output, SynchronizationContext uiContext, CollectorSettings settings, PrivacySettings privacy, CollectorStatus status)
    {
        _out = output;
        _ui = uiContext;
        _settings = settings;
        _privacy = privacy;
        _status = status;
    }

    public bool IsAttached => _pid != 0;
    public uint Pid => _pid;
    /// <summary>True while a window of the watched process is the foreground window.</summary>
    public bool InFront => _inFront;
    public string CurrentTitle => ScreenTitle(_foreground, _currentTitle);
    public IntPtr ForegroundHwnd => _foreground;
    public int MainWindowControlCount { get; private set; } = -1;
    public event Action<bool>? InFrontChanged;

    /// <summary>Call on the UI thread.</summary>
    public void Attach(uint pid)
    {
        Detach();
        _pid = pid;
        _firstWindow = true;
        Log.Info($"attaching to pid {pid}");
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, OnForeground));
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_MENUSTART, Win32.EVENT_SYSTEM_MENUPOPUPEND, pid, OnMenuEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_CAPTURESTART, Win32.EVENT_SYSTEM_CAPTUREEND, pid, OnCapture));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_SHOW, pid, OnWindowEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, pid, OnWindowEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_INVOKED, Win32.EVENT_OBJECT_INVOKED, pid, OnMsaaInvoked));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_FOCUS, Win32.EVENT_OBJECT_FOCUS, pid, OnMsaaFocus));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_STATECHANGE, Win32.EVENT_OBJECT_STATECHANGE, pid, OnMsaaState));
        try
        {
            _focus = _automation.RegisterFocusChangedEvent(OnFocus);
        }
        catch (Exception ex) { Log.Error("focus event registration failed", ex); }

        foreach (var h in Win32.TopLevelWindowsOf(pid)) RegisterWindow(h, initial: true);
        OnForeground(Win32.EVENT_SYSTEM_FOREGROUND, Win32.GetForegroundWindow(), 0, 0);
    }

    public void Detach()
    {
        if (_pid == 0) return;
        Log.Info($"detaching from pid {_pid}");
        foreach (var h in _hooks) h.Dispose();
        _hooks.Clear();
        try { _focus?.Dispose(); } catch { }
        _focus = null;
        foreach (var list in _perWindow.Values) foreach (var d in list) { try { d.Dispose(); } catch { } }
        _perWindow.Clear();
        _knownWindows.Clear();
        lock (_gate) { _rows.Clear(); _lastDump.Clear(); _activeTab.Clear(); _menuPopups.Clear(); _tabStrips.Clear(); _lastTabPoll.Clear(); _fieldReads.Clear(); _fieldRows.Clear(); }
        _pid = 0;
        SetInFront(false);
    }

    private void SetInFront(bool v)
    {
        if (_inFront == v) return;
        _inFront = v;
        InFrontChanged?.Invoke(v);
    }

    private string ScreenTitle(IntPtr hwnd, string title)
    {
        if (!_settings.TabDocuments) return title;
        lock (_gate) return _activeTab.TryGetValue(hwnd, out var tab) && title.Length > 0 ? TabDocuments.Title(title, tab) : title;
    }

    // ---- window filters (profile and privacy) ----

    private bool Ignored(string title) => _settings.IgnoreWindows.Any(p => DialogDetector.Wildcard(title, p));
    private bool Excluded(string title) =>
        _privacy.ExcludeWindows.Any(p => DialogDetector.Wildcard(title, p)) || (_privacy.AllowlistWindows.Count > 0 && !_privacy.AllowlistWindows.Any(p => DialogDetector.Wildcard(title, p)));

    // ---- WinEvents (UI thread) ----

    private void OnForeground(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (hwnd == IntPtr.Zero) { SetInFront(false); return; }
        var pid = Win32.ProcessIdOf(hwnd);
        var mine = pid == _pid && _pid != 0;
        if (mine)
        {
            // the event may name a child control (a WinForms button has its own hwnd): always work with the top-level window
            var top = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
            if (top != IntPtr.Zero) hwnd = top;
            lock (_gate) { if (_menuPopups.Contains(hwnd)) return; }
            var title = Win32.WindowText(hwnd);
            SetInFront(true);
            // untitled helper windows (parking windows, drop-downs) do not change which screen the user is on
            if (title.Length == 0) { if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false); return; }
            if (hwnd == _foreground && title == _currentTitle) { PollTabs(hwnd, force: false); return; }
            if (title != _currentTitle) _previousTitle = _currentTitle;
            _foreground = hwnd;
            _currentTitle = title;
            if (Excluded(title)) { _status.ExcludedEvents++; return; }
            Emit(new RawEvent { Act = Acts.Foreground, Hwnd = hwnd, WindowTitle = CurrentTitle, Ct = "Window", Name = CurrentTitle });
            if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false);
            else MaybeRedump(hwnd, title);
            InferPress("window changed", from: _previousTitle);
            PollTabs(hwnd, force: true);
        }
        else SetInFront(false);
    }

    private void OnWindowEvent(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        switch (ev)
        {
            case Win32.EVENT_OBJECT_SHOW:
                if (!Win32.IsTopLevel(hwnd) || _perWindow.ContainsKey(hwnd)) return;
                RegisterWindow(hwnd, initial: false);
                break;
            case Win32.EVENT_OBJECT_DESTROY:
            case Win32.EVENT_OBJECT_HIDE:
                bool wasMenu;
                lock (_gate) wasMenu = _menuPopups.Remove(hwnd);
                if (wasMenu) { MenuClosed(); return; }
                if (!_knownWindows.TryGetValue(hwnd, out var title)) return;
                if (ev == Win32.EVENT_OBJECT_HIDE && Win32.IsWindowVisible(hwnd)) return;
                _knownWindows.Remove(hwnd);
                if (_perWindow.Remove(hwnd, out var list)) foreach (var d in list) { try { d.Dispose(); } catch { } }
                lock (_gate) { _rows.Remove(hwnd); _activeTab.Remove(hwnd); _tabStrips.Remove(hwnd); _lastTabPoll.Remove(hwnd); }
                if (title.Length == 0 || Excluded(title)) return; // helper windows (lookups, tips, drop-downs) are not screens; their close is noise
                Emit(new RawEvent { Act = Acts.Close, Hwnd = hwnd, WindowTitle = title, Ct = "Window", Name = title });
                InferPress("window closed", from: title);
                if (_foreground != IntPtr.Zero && _foreground != hwnd) PollTabs(_foreground, force: true);
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                if (!_knownWindows.ContainsKey(hwnd)) return;
                var t = Win32.WindowText(hwnd);
                if (t.Length > 0 && _knownWindows[hwnd] != t)
                {
                    _knownWindows[hwnd] = t;
                    if (hwnd == _foreground && _inFront)
                    {
                        _previousTitle = _currentTitle;
                        _currentTitle = t;
                        if (!Excluded(t)) Emit(new RawEvent { Act = Acts.Foreground, Hwnd = hwnd, WindowTitle = CurrentTitle, Ct = "Window", Name = CurrentTitle });
                        MaybeRedump(hwnd, t);
                        InferPress("window changed", from: _previousTitle);
                        PollTabs(hwnd, force: true);
                    }
                }
                break;
        }
    }

    private void OnMenuEvent(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        switch (ev)
        {
            case Win32.EVENT_SYSTEM_MENUSTART:
            case Win32.EVENT_SYSTEM_MENUPOPUPSTART:
                _menuOpenedAt = DateTime.Now;
                ReadPointer("menu");
                break;
            case Win32.EVENT_SYSTEM_MENUPOPUPEND:
            case Win32.EVENT_SYSTEM_MENUEND:
                MenuClosed();
                break;
        }
    }

    /// <summary>The mouse was captured by a control of the watched program: a button was pressed down. Read what is under the pointer now, before it reacts.</summary>
    private void OnCapture(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (ev == Win32.EVENT_SYSTEM_CAPTURESTART && _inFront) ReadPointer("capture");
    }

    /// <summary>A menu closed: if an item was highlighted or focused just before, that item was chosen (unless a real press already said so).</summary>
    private void MenuClosed()
    {
        (string name, DateTime t) item;
        lock (_gate) item = _lastMenuItem;
        if (item.name.Length == 0 || (DateTime.Now - item.t).TotalMilliseconds > 1500) return;
        // the menu-bar item ("File", "View") is highlighted before its popup opens; a chosen item is highlighted after
        if (_menuOpenedAt != DateTime.MinValue && item.t < _menuOpenedAt) { lock (_gate) _lastMenuItem = ("", DateTime.MinValue); return; }
        lock (_gate) _lastMenuItem = ("", DateTime.MinValue);
        EmitPress(new RawEvent { Act = Acts.Invoke, Ct = "MenuItem", Name = item.name, How = PressHow.Menu, WindowTitle = CurrentTitle });
    }

    private void OnMsaaInvoked(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront) return;
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        var ct = Msaa.ControlType(o.Role);
        if (ct is "Window" or "Pane" or "Edit" or "Text" or "Document") return;
        EmitPress(new RawEvent { Act = Acts.Invoke, Ct = ct, Name = o.Name, How = PressHow.Msaa, WindowTitle = CurrentTitle });
    }

    private void OnMsaaFocus(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront && _menuOpenedAt == DateTime.MinValue) return;
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        if (o.Role == Msaa.RoleMenuItem) { lock (_gate) _lastMenuItem = (o.Name, DateTime.Now); return; }
        if (o.Role is Msaa.RolePushButton or Msaa.RoleSplitButton or Msaa.RoleButtonDropDown or Msaa.RoleButtonMenu or Msaa.RoleLink)
            lock (_gate) _lastActionable = (o.Name, "", Msaa.ControlType(o.Role), CurrentTitle, DateTime.Now);
    }

    /// <summary>
    /// A state change in the watched program (rate limited: they are frequent). A push button whose state gained PRESSED is a
    /// press (toolbars that raise no Invoke); any state change while the pointer rests on a button is the moment to read what
    /// is under the pointer, before the click's effect replaces it (a tab close button is gone right after).
    /// </summary>
    private void OnMsaaState(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront) return;
        var now = DateTime.Now;
        if (now.Second != _stateSecond.Second) { _stateSecond = now; _stateEventsThisSecond = 0; }
        if (++_stateEventsThisSecond > 40) return;
        ReadPointer("state");
        if (idObject != Win32.OBJID_CLIENT) return;
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        if (o.Role is Msaa.RolePushButton or Msaa.RoleSplitButton or Msaa.RoleButtonDropDown && (o.State & Msaa.StatePressed) != 0)
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = "Button", Name = o.Name, How = PressHow.Msaa, WindowTitle = CurrentTitle });
    }

    private static readonly HashSet<string> Actionable = new(StringComparer.Ordinal) { "Button", "SplitButton", "MenuItem", "Hyperlink", "CheckBox", "RadioButton" };

    /// <summary>
    /// Reads the control under the pointer (read only: ElementFromPoint, no hook) and remembers it when it is a button, menu
    /// item, tab or link of the watched program. Throttled to once per 150 ms. Called on mouse capture, state changes and
    /// menu starts, which is when a click has just begun.
    /// </summary>
    private void ReadPointer(string why)
    {
        lock (_gate)
        {
            if ((DateTime.Now - _lastPointerRead).TotalMilliseconds < 150) return;
            _lastPointerRead = DateTime.Now;
        }
        var pt = System.Windows.Forms.Cursor.Position;
        var win = CurrentTitle;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var el = _automation.FromPoint(pt);
                if (el is null || el.Properties.ProcessId.ValueOrDefault != _pid) return;
                var r = Describe(el, Acts.Invoke);
                if (!Actionable.Contains(r.Ct) || r.Name.Length == 0) return;
                lock (_gate) _pointer = (r.Name, r.Aid, r.Ct, win, DateTime.Now, pt);
            }
            catch { }
        });
    }

    /// <summary>
    /// Something changed (a window opened, closed or changed, a tab or a message appeared, a value arrived on another control,
    /// the data changed) within 1.5 s after a click began on a button, and no press was recorded for it: count the press.
    /// The control under the pointer when the click began comes first; the button that had focus second; for a window change
    /// a fresh read under the pointer last (the pointer still rests on the toolbar button that opened the document).
    /// The window check keeps the default button of a dialog that just opened, or a button that got focus back when a dialog
    /// closed, from counting.
    /// </summary>
    private void InferPress(string why, string from, DateTime? at = null, int maxAgeMs = 1500)
    {
        if (!_settings.PressInference) return;
        var now = at ?? DateTime.Now;
        (string name, string aid, string ct, string win, DateTime t, System.Drawing.Point at) p; (string name, string aid, string ct, string win, DateTime t) a; (string name, DateTime t) last;
        lock (_gate) { p = _pointer; a = _lastActionable; last = _lastPress; }
        bool Fresh(DateTime t) { var age = (now - t).TotalMilliseconds; return age >= -50 && age <= maxAgeMs; }
        bool SameWindow(string win) => from.Length == 0 || win.Length == 0 || Core.Learning.Titles.Key(win) == Core.Learning.Titles.Key(from);
        if (p.name.Length > 0 && Fresh(p.t) && SameWindow(p.win) && last.t < p.t.AddMilliseconds(-50))
        {
            lock (_gate) { _pointer = ("", "", "", "", DateTime.MinValue, default); _lastPointerPress = p.at; }
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = p.ct, Name = p.name, Aid = p.aid, How = PressHow.Inferred, WindowTitle = p.win.Length > 0 ? p.win : CurrentTitle, FromPointer = true });
            return;
        }
        if (a.name.Length > 0 && Fresh(a.t) && SameWindow(a.win) && last.t < a.t.AddMilliseconds(-50))
        {
            lock (_gate) _lastActionable = ("", "", "", "", DateTime.MinValue);
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = a.ct, Name = a.name, Aid = a.aid, How = PressHow.Inferred, WindowTitle = a.win.Length > 0 ? a.win : CurrentTitle });
            return;
        }
        // a window, tab or message change with nothing remembered: the pointer usually still rests on the button that caused it.
        // A pointer that has not moved since the last press it was credited with is parked, not clicking: nothing is credited to it.
        if (why is "window opened" or "window closed" or "window changed" or "tab changed" or "dialog")
        {
            var pt = System.Windows.Forms.Cursor.Position;
            System.Drawing.Point lastAt; lock (_gate) lastAt = _lastPointerPress;
            if (Math.Abs(pt.X - lastAt.X) <= 3 && Math.Abs(pt.Y - lastAt.Y) <= 3) return;
            var win = from.Length > 0 ? from : CurrentTitle;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var el = _automation.FromPoint(pt);
                    if (el is null || el.Properties.ProcessId.ValueOrDefault != _pid) return;
                    var r = Describe(el, Acts.Invoke);
                    if (r.Ct is not ("Button" or "SplitButton" or "MenuItem" or "Hyperlink") || r.Name.Length == 0) return;
                    lock (_gate) _lastPointerPress = pt;
                    EmitPress(new RawEvent { Act = Acts.Invoke, Ct = r.Ct, Name = r.Name, Aid = r.Aid, How = PressHow.Inferred, WindowTitle = win, FromPointer = true });
                }
                catch { }
            });
        }
    }

    /// <summary>
    /// The watched program's data changed (a data file was written) shortly after a click began, and no press was
    /// seen: a Save, Post or Delete-type click in a toolkit whose buttons announce nothing.
    /// </summary>
    public void InferPressFromData()
    {
        if (!_inFront) return;
        InferPress("data changed", from: "", maxAgeMs: 3000);
    }

    /// <summary>All press paths go through here so one click is one event whichever path saw it first.</summary>
    private void EmitPress(RawEvent r)
    {
        lock (_gate)
        {
            // one click, one event: the MSAA pressed state can flicker for a couple of seconds on the same button, and
            // an inferred press can be seen by two triggers (a window opened and a tab changed)
            var window = r.How is PressHow.Msaa or PressHow.Inferred ? 1500 : 400;
            if (_lastPress.name == r.Name && (DateTime.Now - _lastPress.t).TotalMilliseconds < window) return;
            _lastPress = (r.Name, DateTime.Now);
            if (_lastActionable.name == r.Name) _lastActionable = ("", "", "", "", DateTime.MinValue);
            if (_pointer.name == r.Name) _pointer = ("", "", "", "", DateTime.MinValue, default);
        }
        switch (r.How)
        {
            case PressHow.Msaa: _status.PressesMsaa++; break;
            case PressHow.Menu: _status.PressesMenu++; break;
            case PressHow.Inferred: _status.PressesInferred++; if (r.FromPointer) _status.PressesPointer++; break;
            default: _status.PressesUia++; break;
        }
        if (r.Ct == "MenuItem") lock (_gate) _lastMenuItem = (r.Name, DateTime.Now);
        Emit(r);
        ScheduleReread($"press {r.Name}");
    }

    private void MaybeRedump(IntPtr hwnd, string title)
    {
        int count; DateTime last;
        lock (_gate) { count = _rows.TryGetValue(hwnd, out var rows) ? rows.Count : -1; last = _lastDump.GetValueOrDefault(hwnd); }
        if (count >= 10 || count < 0) return;
        if ((DateTime.Now - last).TotalSeconds < Math.Max(10, _settings.RedumpSeconds)) return;
        lock (_gate) _lastDump[hwnd] = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var el = _automation.FromHandle(hwnd);
                var rec = DumpTree(el, hwnd, title);
                if (rec.Controls.Count <= count) return;
                lock (_gate) _rows[hwnd] = rec.Controls;
                Log.Info($"re-dump of '{title}' found {rec.Controls.Count} controls (was {count})");
                Emit(new RawEvent { Act = Acts.Open, Hwnd = hwnd, WindowTitle = rec.Title, Ct = "Window", Name = rec.Title, Screen = rec });
            }
            catch (Exception ex) { Log.Warn($"re-dump failed for '{title}': {ex.Message}"); }
        });
    }

    private void RegisterWindow(IntPtr hwnd, bool initial)
    {
        var title = Win32.WindowText(hwnd);
        if (!Win32.IsWindowVisible(hwnd)) return;
        var cls = Win32.ClassName(hwnd);
        if (cls is "tooltips_class32" || cls.Contains("tooltips_class32", StringComparison.OrdinalIgnoreCase) || cls.Contains("ToolTip", StringComparison.OrdinalIgnoreCase)) { OnTooltip(hwnd, null); return; }
        if (cls is "IME" or "MSCTFIME UI" or "Shell_TrayWnd") return;
        if (cls == "#32768") { lock (_gate) _menuPopups.Add(hwnd); _menuOpenedAt = DateTime.Now; return; }
        if (Ignored(title)) return;
        var excluded = Excluded(title);
        var appeared = DateTime.Now;
        var pointerAtShow = System.Windows.Forms.Cursor.Position;
        _knownWindows[hwnd] = title;
        var handlers = new List<IDisposable>();
        _perWindow[hwnd] = handlers;
        var isDialogClass = cls == "#32770";
        var firstWindow = _firstWindow && !initial;
        var reachedFrom = _previousTitle.Length > 0 ? _previousTitle : _currentTitle;
        (string name, DateTime t) lastMenu; lock (_gate) lastMenu = _lastMenuItem;
        var menuPath = lastMenu.name.Length > 0 && (DateTime.Now - lastMenu.t).TotalSeconds <= 5 ? lastMenu.name : null;

        // an untitled window that is small: a custom-drawn hover tip (Infragistics draws its tips in a plain window). Read it as a hint, never as a screen.
        if (title.Length == 0 && !initial && IsTipSized(hwnd)) { _knownWindows.Remove(hwnd); _perWindow.Remove(hwnd); OnTooltip(hwnd, pointerAtShow); return; }

        // UIA subscriptions scoped to this window's subtree only
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var el = _automation.FromHandle(hwnd);
                    var lib = _automation.EventLibrary;
                    if (!excluded)
                    {
                        var invoke = el.RegisterAutomationEvent(lib.Invoke.InvokedEvent, TreeScope.Subtree, (e, _) => OnElementEvent(e, Acts.Invoke, hwnd));
                        var select = el.RegisterAutomationEvent(lib.SelectionItem.ElementSelectedEvent, TreeScope.Subtree, (e, _) => OnElementEvent(e, Acts.Select, hwnd));
                        var value = el.RegisterPropertyChangedEvent(TreeScope.Subtree, OnValueChanged, _automation.PropertyLibrary.Value.Value);
                        lock (handlers) { handlers.Add(invoke); handlers.Add(select); handlers.Add(value); }
                        try
                        {
                            var toggle = el.RegisterPropertyChangedEvent(TreeScope.Subtree, OnToggleChanged, _automation.PropertyLibrary.Toggle.ToggleState);
                            lock (handlers) handlers.Add(toggle);
                        }
                        catch { }
                        try
                        {
                            var menuOpened = el.RegisterAutomationEvent(lib.Element.MenuOpenedEvent, TreeScope.Subtree, (e, _) => _menuOpenedAt = DateTime.Now);
                            var menuClosed = el.RegisterAutomationEvent(lib.Element.MenuClosedEvent, TreeScope.Subtree, (e, _) => MenuClosed());
                            lock (handlers) { handlers.Add(menuOpened); handlers.Add(menuClosed); }
                        }
                        catch { }
                        // Document controls (rich text, editors) report edits through the Text pattern instead of the Value property
                        try
                        {
                            var text = el.RegisterAutomationEvent(lib.Text.TextChangedEvent, TreeScope.Subtree, (e, _) => OnTextChanged(e));
                            lock (handlers) handlers.Add(text);
                        }
                        catch { /* not every provider supports it */ }
                    }

                    // dump the control tree once per window title (after a short delay so the window is populated)
                    Thread.Sleep(initial ? 50 : 400);
                    var rec = DumpTree(el, hwnd, title.Length > 0 ? title : Win32.WindowText(hwnd));
                    lock (_gate) { _rows[hwnd] = rec.Controls; _lastDump[hwnd] = DateTime.Now; }
                    // a popup menu that is an ordinary window (toolkit menus, WinForms drop-downs): its items and access keys are kept, it is never a screen
                    if (DialogDetector.IsMenuWindow(rec.Controls) && rec.Controls.Count <= 120)
                    {
                        lock (_gate) _menuPopups.Add(hwnd);
                        _menuOpenedAt = DateTime.Now;
                        var menuName = DialogDetector.MenuName(title);
                        if (menuName.Length == 0) menuName = menuPath ?? "";
                        // what the menu offers, even if nothing is chosen: shows the screens nobody opened
                        var menuItems = rec.Controls.Where(c => c.Type == "MenuItem" && c.Name.Trim().Length > 0).Select(c => c.Name.Trim()).Distinct().Take(60).ToList();
                        bool firstMenu;
                        lock (_dumpedTitles) firstMenu = menuName.Length > 0 && _dumpedTitles.Add("menu:" + menuName);
                        if (menuItems.Count > 0 && _inFront)
                        {
                            rec.Kind = ScreenKinds.Menu; rec.Title = menuName.Length > 0 ? menuName : rec.Title;
                            Emit(new RawEvent { Act = Acts.Menu, Ct = "Menu", Name = menuName, Items = menuItems, Hwnd = hwnd, WindowTitle = CurrentTitle, Screen = firstMenu ? rec : null });
                        }
                        _knownWindows.Remove(hwnd);
                        return;
                    }
                    if (title.Length == 0)
                    {
                        // a suggestion list or lookup (autocomplete, a search's result list) that opened while the user was typing: its rows are the evidence
                        if (_inFront && LookupFromWindow(rec, hwnd)) { _knownWindows.Remove(hwnd); return; }
                        // untitled helper windows (parking windows, drop-downs, tips) are not screens either
                        _knownWindows.Remove(hwnd);
                        return;
                    }
                    _status.WindowsSeen++;
                    if (rec.Source == "msaa") _status.DumpsMsaa++;
                    if (excluded) { _status.ExcludedEvents++; return; }
                    var (dialogLike, dialogText) = DialogDetector.Detect(cls, rec.Controls);
                    if (!dialogLike && _settings.DialogWindows.Any(p => DialogDetector.Wildcard(rec.Title, p)) && DialogDetector.CouldBeNamedDialog(rec.Controls)) dialogLike = true;
                    if (dialogLike && (dialogText is null || dialogText.Length < 8 || dialogText == rec.Title)) dialogText = ReadDialogBody(el, rec.Controls) ?? dialogText;
                    if (dialogLike && initial) dialogLike = false; // a window that was already there is a screen, not a reaction to anything
                    var session = !dialogLike && DialogDetector.IsSessionWindow(rec.Title, rec.Controls, firstWindow, _settings.SessionWindows);
                    _firstWindow = false;
                    rec.Kind = dialogLike ? ScreenKinds.Dialog : session ? ScreenKinds.Session : ScreenKinds.Screen;
                    rec.ReachedFrom = reachedFrom.Length > 0 && reachedFrom != rec.Title ? reachedFrom : null;
                    rec.MenuPath = menuPath;
                    if (dialogLike) _status.Dialogs++;
                    bool firstTime;
                    lock (_dumpedTitles) firstTime = _dumpedTitles.Add(Core.Learning.Titles.Key(rec.Title));
                    if (!initial || _knownWindows.Count == 1) { if (MainWindowControlCount < 0 || rec.Controls.Count > MainWindowControlCount) MainWindowControlCount = Math.Max(MainWindowControlCount, rec.Controls.Count); }
                    Emit(new RawEvent
                    {
                        Act = dialogLike ? Acts.Dialog : Acts.Open, Hwnd = hwnd, WindowTitle = rec.Title, Ct = "Window", Name = rec.Title,
                        Val = dialogLike ? dialogText : null, Screen = firstTime && !dialogLike ? rec : null, IsDialog = dialogLike,
                    });
                    if (!initial) InferPress(dialogLike ? "dialog" : "window opened", from: reachedFrom, at: appeared);
                    if (!initial && !dialogLike) PollTabs(hwnd, force: true);
                    return;
                }
                catch (Exception ex)
                {
                    // a slow or busy program can miss the UI Automation timeout once; without a retry the window would stay
                    // unread (no screen, no picture, no events) for as long as it is open
                    lock (handlers) { foreach (var d in handlers) { try { d.Dispose(); } catch { } } handlers.Clear(); }
                    if (attempt >= 3 || !Win32.IsWindowVisible(hwnd))
                    {
                        Log.Warn($"window registration failed for '{title}': {ex.Message} (attempt {attempt}, giving up)");
                        return;
                    }
                    Log.Info($"window registration of '{title}' failed: {ex.Message}; trying again (attempt {attempt + 1} of 3)");
                    Thread.Sleep(attempt == 1 ? 2000 : 6000);
                }
            }
        });
    }

    /// <summary>A window the size of a hover tip: under 40 px high and under 400 px wide.</summary>
    private static bool IsTipSized(IntPtr hwnd)
    {
        try
        {
            if (!Win32.GetWindowRect(hwnd, out var r)) return false;
            var w = r.Right - r.Left; var h = r.Bottom - r.Top;
            return w > 0 && h > 0 && h < 40 && w < 400;
        }
        catch { return false; }
    }

    /// <summary>
    /// An untitled window holding a grid, list or tree that opened while a field had focus: a lookup or suggestion list.
    /// Its first rows become a dropdown event on that field. Returns true when it was taken as one.
    /// </summary>
    private bool LookupFromWindow(ScreenRecord rec, IntPtr hwnd)
    {
        (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) field; string typed;
        lock (_gate) { field = _lastField; typed = _lastTyped.GetValueOrDefault(field.aid.Length > 0 ? field.aid : field.name, ""); }
        if (field.name.Length + field.aid.Length == 0 || (DateTime.Now - field.t).TotalSeconds > 8) return false;
        var gridIndex = rec.Controls.FindIndex(c => c.Type is "DataGrid" or "Table" or "List" or "Tree" || c.Type == "Custom" && c.AutomationId.StartsWith("grid", StringComparison.OrdinalIgnoreCase));
        List<string> items;
        if (gridIndex >= 0) items = FieldReads.GridLines(rec.Controls, gridIndex, 30);
        else items = rec.Controls.Where(c => c.Type is "ListItem" or "DataItem" or "TreeItem" && c.Name.Length > 0).Select(c => c.Name).Take(30).ToList();
        if (items.Count == 0 || (gridIndex >= 0 && items.Count <= 1)) return false;
        if (gridIndex < 0 && rec.Controls.Any(c => c.Type is "MenuItem" or "Edit" or "Button")) return false;
        _status.Dropdowns++;
        var pwd = Masking.IsSensitiveName(field.name) || Masking.IsSensitiveName(field.aid);
        Emit(new RawEvent
        {
            Act = Acts.Dropdown, Ct = field.ct, Name = field.name, Aid = field.aid, Val = pwd ? Masking.Masked : typed, Hwnd = hwnd, WindowTitle = CurrentTitle,
            Items = items.Select(i => pwd ? Masking.Masked : (i.Length > 400 ? i[..400] : i)).ToList(),
        });
        return true;
    }

    /// <summary>The body text of a message box whose body is a read-only Edit (Infragistics lblBody): one live read.</summary>
    private string? ReadDialogBody(AutomationElement window, List<ControlRow> rows)
    {
        try
        {
            var body = rows.FirstOrDefault(r => r.Type is "Edit" or "Document" && r.AutomationId.Contains("body", StringComparison.OrdinalIgnoreCase))
                ?? rows.FirstOrDefault(r => r.Type is "Edit" or "Document");
            if (body is null) return null;
            var cf = _automation.ConditionFactory;
            var el = body.AutomationId.Length > 0 ? window.FindFirstDescendant(cf.ByAutomationId(body.AutomationId)) : window.FindFirstDescendant(cf.ByControlType(ControlType.Edit));
            if (el is null) return null;
            string? v = null;
            try { v = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { }
            if (string.IsNullOrWhiteSpace(v)) { try { v = el.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(500); } catch { } }
            if (string.IsNullOrWhiteSpace(v)) { try { v = el.Name; } catch { } }
            return string.IsNullOrWhiteSpace(v) ? null : v.Replace("\r", " ").Replace("\n", " ").Trim();
        }
        catch { return null; }
    }

    // ---- tab documents (read after events, at most every two seconds) ----

    /// <summary>
    /// Reads which tab item of the window's tab strip says it is selected (a true condition and a filter in code: property
    /// conditions on a WinForms tab strip can flip its tabs). On a change the new document becomes the screen; when no tab
    /// is selected or the item is gone, events belong to the plain window again.
    /// </summary>
    private void PollTabs(IntPtr hwnd, bool force)
    {
        if (!_settings.TabDocuments || hwnd == IntPtr.Zero || !_inFront) return;
        lock (_gate)
        {
            if (!force && _lastTabPoll.TryGetValue(hwnd, out var last) && (DateTime.Now - last).TotalSeconds < 2) return;
            _lastTabPoll[hwnd] = DateTime.Now;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                AutomationElement? strip;
                lock (_gate) _tabStrips.TryGetValue(hwnd, out strip);
                List<TabDocuments.TabItem>? items = null;
                AutomationElement? selectedEl = null;
                if (strip is not null) (items, selectedEl) = ReadTabItems(strip);
                if (items is null)
                {
                    var win = _automation.FromHandle(hwnd);
                    strip = win.FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Tab));
                    if (strip is null) return;
                    lock (_gate) _tabStrips[hwnd] = strip;
                    (items, selectedEl) = ReadTabItems(strip);
                    if (items is null) { lock (_gate) _tabStrips.Remove(hwnd); return; }
                }
                string? current; lock (_gate) current = _activeTab.GetValueOrDefault(hwnd);
                var active = TabDocuments.Active(items, current);
                if (active == current) return;
                if (active is null) { TabGone(hwnd, current!); return; }
                ApplyTab(hwnd, active, selectedEl, viaEvent: false);
            }
            catch (Exception ex) { Log.Info("tab strip not read: " + ex.Message); lock (_gate) _tabStrips.Remove(hwnd); }
        });
    }

    /// <summary>
    /// The tab items of a strip and which one is active, read without touching the selection: asking a WinForms tab item
    /// whether it is selected selects it (seen live), so the active tab is the one whose document is on screen instead: the
    /// MDI child window or pane named like the tab that is not offscreen (tabbed MDI), else the tab item with on-screen
    /// content (a WinForms TabControl keeps the page under its tab item).
    /// </summary>
    private (List<TabDocuments.TabItem>? items, AutomationElement? selected) ReadTabItems(AutomationElement strip)
    {
        try
        {
            var lib = _automation.PropertyLibrary;
            var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.Full };
            cr.TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true);
            cr.Add(lib.Element.Name); cr.Add(lib.Element.ControlType); cr.Add(lib.Element.IsOffscreen); cr.Add(lib.Element.BoundingRectangle);
            AutomationElement[] tabs;
            using (cr.Activate()) tabs = strip.FindAllChildren();
            var items = new List<(string name, AutomationElement el, bool contentOnScreen)>();
            foreach (var c in tabs)
            {
                string ct; try { ct = c.Properties.ControlType.ValueOrDefault.ToString(); } catch { continue; }
                if (ct != "TabItem") continue;
                var name = ""; try { name = (c.Properties.Name.ValueOrDefault ?? "").Trim(); } catch { }
                if (name.Length == 0) continue;
                var onScreen = false;
                try
                {
                    foreach (var child in c.CachedChildren)
                    {
                        string cct; try { cct = child.Properties.ControlType.ValueOrDefault.ToString(); } catch { continue; }
                        if (cct == "Button") continue; // a tab's own close button is always on screen
                        bool off = true; try { off = child.Properties.IsOffscreen.ValueOrDefault; } catch { }
                        var r = child.Properties.BoundingRectangle.ValueOrDefault;
                        if (!off && r.Width > 0 && r.Height > 0) { onScreen = true; break; }
                    }
                }
                catch { }
                items.Add((name, c, onScreen));
            }
            if (items.Count == 0) return (new List<TabDocuments.TabItem>(), null);
            // tabbed MDI: the documents are siblings of the strip, named like their tabs; the one on screen is the active one
            string? byDocument = null;
            try
            {
                var parent = _automation.TreeWalkerFactory.GetControlViewWalker().GetParent(strip);
                if (parent is not null)
                {
                    var cr2 = new CacheRequest { TreeScope = TreeScope.Children, AutomationElementMode = AutomationElementMode.None };
                    cr2.Add(lib.Element.Name); cr2.Add(lib.Element.ControlType); cr2.Add(lib.Element.IsOffscreen); cr2.Add(lib.Element.BoundingRectangle);
                    AutomationElement[] siblings;
                    using (cr2.Activate()) siblings = parent.FindAllChildren();
                    foreach (var s in siblings)
                    {
                        string sct; try { sct = s.Properties.ControlType.ValueOrDefault.ToString(); } catch { continue; }
                        if (sct is not ("Window" or "Pane" or "Custom")) continue;
                        var sname = ""; try { sname = (s.Properties.Name.ValueOrDefault ?? "").Trim(); } catch { }
                        if (sname.Length == 0) continue;
                        bool off = true; try { off = s.Properties.IsOffscreen.ValueOrDefault; } catch { }
                        var r = s.Properties.BoundingRectangle.ValueOrDefault;
                        var match = items.FirstOrDefault(i => Core.Learning.Titles.Key(i.name) == Core.Learning.Titles.Key(sname));
                        if (match.name is not null && !off && r.Width > 0 && r.Height > 0) { byDocument = match.name; break; }
                    }
                }
            }
            catch { }
            var active = byDocument ?? items.FirstOrDefault(i => i.contentOnScreen).name;
            var list = items.Select(i => new TabDocuments.TabItem(i.name, i.name == active)).ToList();
            var selected = active is null ? null : items.First(i => i.name == active).el;
            return (list, selected);
        }
        catch { return (null, null); }
    }

    /// <summary>The active document closed (its tab item is gone or nothing is selected): the window is plain again.</summary>
    private void TabGone(IntPtr hwnd, string tab)
    {
        string windowTitle;
        lock (_gate) { _activeTab.Remove(hwnd); windowTitle = _knownWindows.TryGetValue(hwnd, out var t) ? t : _currentTitle; }
        var screenTitle = TabDocuments.Title(windowTitle, tab);
        lock (_gate) { _fieldReads.Remove(screenTitle); _fieldRows.Remove(screenTitle); }
        Emit(new RawEvent { Act = Acts.Close, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = hwnd });
        InferPress("tab changed", from: screenTitle);
    }

    /// <summary>A tab document was selected: events now belong to "Window › Tab", and the tab's content is a screen of its own.</summary>
    private void ApplyTab(IntPtr top, string tabName, AutomationElement? tabElement, bool viaEvent)
    {
        if (top == IntPtr.Zero) top = _foreground;
        string windowTitle;
        lock (_gate)
        {
            windowTitle = _knownWindows.TryGetValue(top, out var t) ? t : _currentTitle;
            if (_activeTab.TryGetValue(top, out var cur) && cur == tabName) return;
            _activeTab[top] = tabName;
        }
        var screenTitle = TabDocuments.Title(windowTitle, tabName);
        _previousTitle = _currentTitle.Length > 0 ? ScreenTitle(IntPtr.Zero, _currentTitle) : _previousTitle;
        _status.TabSelections++;
        Emit(new RawEvent { Act = Acts.Select, Ct = "TabItem", Name = tabName, WindowTitle = screenTitle, How = viaEvent ? null : "read" });
        InferPress("tab changed", from: windowTitle);
        bool firstTime;
        lock (_dumpedTitles) firstTime = !_dumpedTitles.Contains(Core.Learning.Titles.Key(screenTitle));
        if (!firstTime) { Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top }); ScheduleReread("tab " + tabName); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(400);
                var rec = DumpDocument(top, tabName, tabElement, screenTitle);
                rec.Kind = ScreenKinds.Tab;
                rec.ReachedFrom = windowTitle;
                lock (_dumpedTitles) _dumpedTitles.Add(Core.Learning.Titles.Key(screenTitle));
                _status.TabScreens++;
                Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top, Screen = rec });
                ScheduleReread("tab " + tabName);
            }
            catch (Exception ex) { Log.Warn($"tab dump failed for '{screenTitle}': {ex.Message}"); }
        });
    }

    /// <summary>
    /// The selected document's own subtree: the MDI child named like the tab, or the on-screen sibling of the tab strip,
    /// never the window's toolbars, menu bar, status bar or the other documents. Falls back to the tab item's subtree
    /// (WinForms puts the page under the tab item).
    /// </summary>
    private ScreenRecord DumpDocument(IntPtr top, string tabName, AutomationElement? tabElement, string screenTitle)
    {
        var el = _automation.FromHandle(top);
        var whole = DumpTree(el, top, screenTitle, onlyOnScreen: true);
        var doc = TabDocuments.Document(whole.Controls, tabName);
        if (doc.Count < 5 && tabElement is not null)
        {
            var sub = DumpSubtree(tabElement, screenTitle);
            if (sub.Controls.Count >= 5) doc = sub.Controls;
        }
        if (doc.Count < 5) { Log.Info($"document of tab '{tabName}' not told apart; the window's visible controls stand in"); return whole; }
        return new ScreenRecord { Title = screenTitle, Captured = DateTime.Now, Source = whole.Source, Controls = doc };
    }

    // ---- document re-reads: what the program filled in ----

    /// <summary>Once, 300 ms after the last user value or press, the on-screen fields of the focused document are read and compared with the previous read.</summary>
    private void ScheduleReread(string why)
    {
        if (!_inFront || _foreground == IntPtr.Zero) return;
        lock (_gate) _rereadWhy = why;
        _rereadTimer ??= new System.Threading.Timer(_ => Reread(), null, Timeout.Infinite, Timeout.Infinite);
        _rereadTimer.Change(300, Timeout.Infinite);
    }

    private void Reread()
    {
        if (Interlocked.Exchange(ref _rereadBusy, 1) == 1) { _rereadTimer?.Change(300, Timeout.Infinite); return; }
        try
        {
            var hwnd = _foreground;
            if (!_inFront || hwnd == IntPtr.Zero) return;
            var screen = CurrentTitle;
            if (screen.Length == 0) return;
            string? tab; lock (_gate) tab = _activeTab.GetValueOrDefault(hwnd);
            var rows = new List<ControlRow>();
            using (Cache(withFilter: true, withValues: true).Activate())
            {
                var cached = _automation.FromHandle(hwnd);
                WalkCached(cached, 0, _settings.DumpDepth, rows, 1500, 12, onlyOnScreen: true, withValues: true);
            }
            if (tab is not null)
            {
                var doc = TabDocuments.Document(rows, tab);
                if (doc.Count >= 5) rows = doc;
            }
            var reads = FieldReads.FromRows(rows);
            List<FieldRead>? before; List<ControlRow>? beforeRows; HashSet<string> userKeys; (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) field; string typed; string why;
            lock (_gate)
            {
                _fieldReads.TryGetValue(screen, out before); _fieldRows.TryGetValue(screen, out beforeRows);
                foreach (var stale in _userKeysSinceRead.Where(k => (DateTime.Now - k.Value).TotalSeconds > 5).Select(k => k.Key).ToList()) _userKeysSinceRead.Remove(stale);
                userKeys = new HashSet<string>(_userKeysSinceRead.Keys, StringComparer.Ordinal);
                field = _lastField; typed = _lastTyped.GetValueOrDefault(field.aid.Length > 0 ? field.aid : field.name, ""); why = _rereadWhy;
                _fieldReads[screen] = reads; _fieldRows[screen] = rows;
            }
            if (field.name.Length > 0) { userKeys.Add(field.name); if (!Labels.IsWrapperId(field.aid)) { userKeys.Add(field.aid); userKeys.Add(Labels.Clean(field.aid)); } }
            if (before is null) return; // the first read of a document is the baseline
            foreach (var f in FieldReads.Filled(before, reads, userKeys))
            {
                if (f.IsPassword || Masking.IsSensitiveName(f.Label) || Masking.IsSensitiveName(f.AutomationId)) continue;
                _status.ValuesFilled++; _status.ValuesReread++;
                Emit(new RawEvent { Act = Acts.Value, Ct = f.IsCell ? "DataItem" : f.Type, Name = f.Label, Aid = f.IsCell ? "" : f.AutomationId, Val = Masking.ForEvent(f.Value, f.Label, f.AutomationId, false), By = ValueBy.App, WindowTitle = screen, Hwnd = hwnd });
            }
            // a lookup list that opened inside the window under the field being typed in
            if (beforeRows is not null && field.name.Length + field.aid.Length > 0 && (DateTime.Now - field.t).TotalSeconds <= 8 && field.rect.Width > 0)
            {
                var pos = $"{field.rect.Left},{field.rect.Top} {field.rect.Width}x{field.rect.Height}";
                var lookup = FieldReads.NewLookup(beforeRows, rows, pos);
                if (lookup is { } lk)
                {
                    _status.Dropdowns++;
                    var pwd = Masking.IsSensitiveName(field.name) || Masking.IsSensitiveName(field.aid);
                    Emit(new RawEvent { Act = Acts.Dropdown, Ct = field.ct, Name = field.name, Aid = field.aid, Val = pwd ? Masking.Masked : typed, Hwnd = hwnd, WindowTitle = screen, Items = lk.lines.Select(i => pwd ? Masking.Masked : i).ToList() });
                }
            }
        }
        catch (Exception ex) { Log.Info("document re-read skipped: " + ex.Message); }
        finally { Interlocked.Exchange(ref _rereadBusy, 0); }
    }

    // ---- UIA callbacks (thread pool) ----

    private void OnFocus(AutomationElement el)
    {
        try
        {
            if (el.Properties.ProcessId.ValueOrDefault != _pid) return;
            var r = Describe(el, Acts.Focus);
            if (r.Ct == "Window" || r.Ct == "Pane" && r.Name.Length == 0) return;
            if (r.Ct == "MenuItem") lock (_gate) _lastMenuItem = (r.Name, DateTime.Now);
            else if (r.Ct is "Button" or "SplitButton" or "Hyperlink") lock (_gate) _lastActionable = (r.Name, r.Aid, r.Ct, CurrentTitle, DateTime.Now);
            else if (r.Ct == "TabItem") PollTabs(_foreground, force: true); // a tab item took focus: the selection may have changed
            else if (r.Ct is "Edit" or "ComboBox" or "Spinner" or "Document" or "DataItem")
            {
                System.Drawing.Rectangle rect = default;
                try { rect = el.BoundingRectangle; } catch { }
                lock (_gate) { _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now, rect); _userKeysSinceRead[r.Name] = DateTime.Now; if (r.Aid.Length > 0 && !Labels.IsWrapperId(r.Aid)) _userKeysSinceRead[r.Aid] = DateTime.Now; }
                ScheduleReread("focus " + r.Name); // a grid cell typed into reports no value change: the read after the focus moved on catches it
            }
            Emit(r);
            PollTabs(_foreground, force: false);
        }
        catch { }
    }

    private void OnElementEvent(AutomationElement el, string act, IntPtr hwnd)
    {
        try
        {
            var r = Describe(el, act);
            if (act == Acts.Invoke && r.Ct == "Window") return;
            if (act == Acts.Invoke) { r.How = PressHow.Uia; EmitPress(r); return; }
            if (act == Acts.Select && r.Ct == "MenuItem") { lock (_gate) _lastMenuItem = (r.Name, DateTime.Now); }
            if (act == Acts.Select && r.Ct == "TabItem" && _settings.TabDocuments && r.Name.Length > 0) { TabSelected(el, hwnd, r.Name); return; }
            Emit(r);
            if (act == Acts.Select) ScheduleReread("pick " + r.Name);
        }
        catch { }
    }

    private void TabSelected(AutomationElement tab, IntPtr hwnd, string tabName)
    {
        // WinForms raises ElementSelected for tab items that are not selected (once, when the window is built):
        // believe the event only when the item says it is selected, or cannot say
        try
        {
            var sel = tab.Patterns.SelectionItem.PatternOrDefault;
            if (sel is not null && !sel.IsSelected.ValueOrDefault) { Log.Info($"tab '{tabName}' reported selected but is not; ignored"); return; }
        }
        catch { }
        ApplyTab(hwnd, tabName, tab, viaEvent: true);
    }

    private void OnValueChanged(AutomationElement el, PropertyId prop, object newValue)
    {
        try
        {
            var r = Describe(el, Acts.Value);
            var v = newValue as string ?? newValue?.ToString();
            r.Val = Masking.ForEvent(v, r.Name, r.Aid, r.IsPassword);
            if (!ChangedByUser(el, r))
            {
                // a total recalculated, a name looked up from a code: the program filled it in
                r.By = ValueBy.App;
                _status.ValuesFilled++;
                Emit(r);
                return;
            }
            _status.ValuesTyped++;
            lock (_gate)
            {
                _lastTyped[r.Aid.Length > 0 ? r.Aid : r.Name] = r.Val ?? "";
                if (_lastField.name.Length == 0 || _lastField.aid != r.Aid) { System.Drawing.Rectangle rect = default; try { rect = el.BoundingRectangle; } catch { } _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now, rect); }
                else _lastField.t = DateTime.Now;
                _userKeysSinceRead[r.Name] = DateTime.Now; if (r.Aid.Length > 0 && !Labels.IsWrapperId(r.Aid)) _userKeysSinceRead[r.Aid] = DateTime.Now;
            }
            Emit(r);
            InferPress("value changed", from: CurrentTitle);
            ScheduleReread("type " + r.Name);
        }
        catch { }
    }

    /// <summary>
    /// True when the value changed in the field the user is in: the last focused field, the element with keyboard
    /// focus, or a field whose focused inner editor sits inside it (Infragistics, DevExpress editors).
    /// </summary>
    private bool ChangedByUser(AutomationElement el, RawEvent r)
    {
        (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) lf;
        lock (_gate) lf = _lastField;
        if (lf.name.Length + lf.aid.Length > 0 && (r.Aid.Length > 0 ? lf.aid == r.Aid : lf.name == r.Name)) return true;
        try { if (el.Properties.HasKeyboardFocus.ValueOrDefault) return true; } catch { return true; }
        try
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return true;
            var a = el.BoundingRectangle; var b = focused.BoundingRectangle;
            if (a.IsEmpty || b.IsEmpty) return false;
            return a.Contains(b) || b.Contains(a);
        }
        catch { return true; }
    }

    private (string key, DateTime t) _lastHint;

    /// <summary>
    /// A hover tip of the watched program: its text and the control under the pointer (read once, when the tip shows;
    /// no mouse hook). Windows tooltips (tooltips_class32) and the plain small windows toolkits draw their tips in are
    /// both read: the window text, else its accessible name, else its UIA name or text child, else the text on it (OCR).
    /// The pointer has to be still for 100 ms (a tip shows when it rests). Only for controls whose tips are help texts
    /// (buttons, menu items, fields), never grid cells or list rows, whose tips show data. Never a screen, never a picture.
    /// </summary>
    private void OnTooltip(IntPtr hwnd, System.Drawing.Point? pointerAtShow)
    {
        if (!_inFront) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var p0 = System.Windows.Forms.Cursor.Position;
                Thread.Sleep(150); // the tip's text is set as it shows
                var p1 = System.Windows.Forms.Cursor.Position;
                if (Math.Abs(p0.X - p1.X) > 3 || Math.Abs(p0.Y - p1.Y) > 3) return; // the pointer is moving: not a resting tip
                if (!Win32.IsWindowVisible(hwnd)) return;
                var text = Win32.WindowText(hwnd).Trim();
                if (text.Length == 0) text = Msaa.FromEvent(hwnd, Win32.OBJID_CLIENT, 0)?.Name?.Trim() ?? "";
                if (text.Length == 0)
                {
                    try
                    {
                        var tip = _automation.FromHandle(hwnd);
                        text = (tip.Name ?? "").Trim();
                        if (text.Length == 0) { var child = tip.FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Text)); text = (child?.Name ?? "").Trim(); }
                        if (text.Length == 0) { try { text = tip.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault?.Trim() ?? ""; } catch { } }
                    }
                    catch { }
                }
                if (text.Length == 0 && IsTipSized(hwnd) && Native.Ocr.Available)
                {
                    var png = Native.WindowCapture.Capture(hwnd, minWidth: 20, minHeight: 10);
                    if (png is not null) { var r0 = Native.Ocr.Read(png); text = (r0?.Text ?? "").Replace("\n", " ").Trim(); }
                }
                if (text.Length == 0 || text.Length > 300) return;
                var el = _automation.FromPoint(p1);
                if (el is null || el.Properties.ProcessId.ValueOrDefault != _pid) return;
                var r = Describe(el, Acts.Hint);
                if (r.Ct is not ("Button" or "SplitButton" or "MenuItem" or "CheckBox" or "RadioButton" or "Edit" or "ComboBox" or "Spinner" or "TabItem" or "Hyperlink" or "Image" or "Text")) return;
                if (r.Name.Length == 0) return;
                var key = r.Ct + "\u0001" + r.Name + "\u0001" + text;
                lock (_gate)
                {
                    if (_lastHint.key == key && (DateTime.Now - _lastHint.t).TotalSeconds < 30) return;
                    _lastHint = (key, DateTime.Now);
                }
                _status.Hints++;
                r.Val = Masking.ForEvent(text, r.Name, r.Aid, r.IsPassword);
                r.WindowTitle = CurrentTitle;
                Emit(r);
            }
            catch { }
        });
    }

    private void OnToggleChanged(AutomationElement el, PropertyId prop, object newValue)
    {
        try
        {
            if (!_inFront) return;
            var r = Describe(el, Acts.Invoke);
            r.How = PressHow.Toggle;
            r.Val = newValue?.ToString() switch { "On" or "1" => "on", "Off" or "0" => "off", var s => s };
            _status.Toggles++;
            Emit(r);
            ScheduleReread("tick " + r.Name);
        }
        catch { }
    }

    private void OnTextChanged(AutomationElement el)
    {
        if (!_inFront) return; // reading the text is a cross-process call; skip it when nobody is recording anyway
        try
        {
            var r = Describe(el, Acts.Value);
            string? v = null;
            try { v = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { }
            if (v is null)
            {
                try { v = el.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(500); } catch { }
            }
            if (v is null) return;
            r.Val = Masking.ForEvent(v, r.Name, r.Aid, r.IsPassword);
            Emit(r);
        }
        catch { }
    }

    /// <summary>
    /// Reads the properties of a control in one cached call; an empty Name is resolved to the label a person would use:
    /// the outer control of a toolkit wrapper (the grid cell or editor whose inner "_EmbeddableTextBox" raised the event),
    /// then LabeledBy, then the nearest Text in the last tree dump, then the cleaned automation id. A tab's close button is
    /// named after its tab.
    /// </summary>
    private RawEvent Describe(AutomationElement el, string act)
    {
        var r = new RawEvent { Act = act };
        AutomationElement e = el;
        try
        {
            var cr = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
            var lib = _automation.PropertyLibrary;
            cr.Add(lib.Element.Name); cr.Add(lib.Element.ControlType); cr.Add(lib.Element.AutomationId); cr.Add(lib.Element.IsPassword);
            using (cr.Activate()) e = el.FindFirst(TreeScope.Element, _automation.ConditionFactory.ByControlType(ControlType.Unknown).Not()) ?? el;
        }
        catch { e = el; }
        try { r.Ct = e.Properties.ControlType.ValueOrDefault.ToString(); } catch { r.Ct = "Unknown"; }
        try { r.Name = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
        try { r.Aid = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
        try { r.IsPassword = e.Properties.IsPassword.ValueOrDefault; } catch { }
        if (r.Name.Length > 200) r.Name = r.Name[..200];
        r.Name = r.Name.Replace("\r", " ").Replace("\n", " ").Trim();
        if (r.Ct is "Edit" or "ComboBox" or "Spinner" or "CheckBox" or "RadioButton" or "Document" && r.Name.EndsWith(':')) r.Name = r.Name.TrimEnd(':', ' ');
        if (r.Name.Length == 0 || Labels.IsWrapperId(r.Name) || (r.Ct == "Button" && r.Name == "TabCloseButton"))
        {
            // 0. the outer control of a wrapper (two levels up at most): the cell "Quant Ordered", the editor "srcCustomer", the tab of a close button
            try
            {
                var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
                var parent = walker.GetParent(el);
                for (var up = 0; up < 2 && parent is not null; up++)
                {
                    string pn = "", pa = "", pct = "";
                    try { pn = (parent.Properties.Name.ValueOrDefault ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); } catch { }
                    try { pa = parent.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
                    try { pct = parent.Properties.ControlType.ValueOrDefault.ToString(); } catch { }
                    if (r.Name == "TabCloseButton" && pct == "TabItem" && pn.Length > 0) { r.Name = "Close tab " + pn; break; }
                    if (pct is "Window" or "Pane" && pn.Length == 0 && !(pa.Length > 0 && !Labels.IsWrapperId(pa))) { parent = walker.GetParent(parent); continue; }
                    if (pn.Length > 0 && !Labels.IsWrapperId(pn) && !LooksLikeValue(pn)) { r.Name = pn; if (r.Aid.Length == 0 || Labels.IsWrapperId(r.Aid)) r.Aid = pa.Length > 0 && !Labels.IsWrapperId(pa) ? pa : r.Aid; break; }
                    if (pa.Length > 0 && !Labels.IsWrapperId(pa)) { if (r.Aid.Length == 0 || Labels.IsWrapperId(r.Aid)) r.Aid = pa; break; }
                    parent = walker.GetParent(parent);
                }
            }
            catch { }
        }
        if (r.Name.Length == 0 || Labels.IsWrapperId(r.Name))
        {
            // 1. the control's LabeledBy, 2. the nearest Text in the last tree dump, 3. the cleaned automation id (done by FieldKey)
            string? label = null;
            try { label = el.Properties.LabeledBy.ValueOrDefault?.Properties.Name.ValueOrDefault; } catch { }
            if (string.IsNullOrWhiteSpace(label) && r.Aid.Length > 0 && !Labels.IsWrapperId(r.Aid))
            {
                List<ControlRow>? rows;
                lock (_gate) _rows.TryGetValue(_foreground, out rows);
                var row = rows?.FirstOrDefault(x => x.AutomationId == r.Aid);
                if (row is not null) label = Labels.Resolve(row, rows!);
            }
            if (!string.IsNullOrWhiteSpace(label)) r.Name = label.Trim();
            else if (Labels.IsWrapperId(r.Name)) r.Name = "";
        }
        if (act == Acts.Select && r.Val is null) r.Val = Masking.ForEvent(r.Name, r.Name, r.Aid, r.IsPassword);
        return r;
    }

    private static bool LooksLikeValue(string s) => s.Length > 60 || s.All(c => char.IsDigit(c) || c is '.' or ',' or '/' or '-' or ':' or ' ' or '$');

    private void Emit(RawEvent r)
    {
        r.WindowTitle ??= CurrentTitle;
        _out.TryWrite(r);
    }

    /// <summary>
    /// The grids of a window as text: for each DataGrid/Table/List with rows, the header line and the first rows
    /// (cells joined by " | "). One cached read; used right after an action to record what a search or filter returned.
    /// </summary>
    public List<(string grid, List<string> lines)> DumpGrids(IntPtr hwnd, int maxRows = 10, int maxGrids = 4)
    {
        var result = new List<(string, List<string>)>();
        try
        {
            var rows = new List<ControlRow>();
            using (Cache(true).Activate())
            {
                var cached = _automation.FromHandle(hwnd);
                WalkCached(cached, 0, _settings.DumpDepth, rows, _settings.DumpRowCap, maxRows, onlyOnScreen: true);
            }
            for (var i = 0; i < rows.Count && result.Count < maxGrids; i++)
            {
                var g = rows[i];
                if (g.Type is not ("DataGrid" or "Table" or "List")) continue;
                var depth = g.Depth;
                var headers = new List<string>(); var lines = new List<string>();
                var j = i + 1;
                for (; j < rows.Count && rows[j].Depth > depth; j++)
                {
                    var r = rows[j];
                    if (r.Type is "HeaderItem" or "Header" && r.Name.Length > 0 && r.Depth <= depth + 2) { if (r.Type == "HeaderItem" || !rows.Skip(j + 1).TakeWhile(x => x.Depth > r.Depth).Any()) headers.Add(r.Name); continue; }
                    if (r.Type is "DataItem" or "ListItem" or "TreeItem")
                    {
                        var cells = new List<string>();
                        var k = j + 1;
                        for (; k < rows.Count && rows[k].Depth > r.Depth; k++) if (rows[k].Type is not ("Note") && rows[k].Name.Length > 0 && rows[k].Depth == r.Depth + 1) cells.Add(rows[k].Name);
                        lines.Add(cells.Count > 0 ? string.Join(" | ", cells) : r.Name);
                        j = k - 1;
                        if (lines.Count >= maxRows) break;
                    }
                }
                if (lines.Count == 0) continue;
                var name = g.Name.Length > 0 ? g.Name : Labels.Clean(g.AutomationId);
                if (name.Length == 0) name = "grid";
                var all = new List<string> { headers.Count > 0 ? string.Join(" | ", headers) : string.Join(" | ", Enumerable.Range(1, Math.Max(1, lines[0].Split(" | ").Length)).Select(n => "column " + n)) };
                all.AddRange(lines.Select(l => l.Length > 400 ? l[..400] : l));
                result.Add((name, all));
            }
        }
        catch (Exception ex) { Log.Info("grid read skipped: " + ex.Message); }
        return result;
    }

    // ---- control tree dump (read only) ----

    private ScreenRecord DumpSubtree(AutomationElement root, string title)
    {
        var rec = new ScreenRecord { Title = title, Captured = DateTime.Now, Source = "uia-cached" };
        try
        {
            var cr = Cache(withFilter: true);
            using (cr.Activate())
            {
                var cached = root.FindFirst(TreeScope.Element, _automation.ConditionFactory.ByControlType(ControlType.Unknown).Not()) ?? root;
                WalkCached(cached, 0, _settings.DumpDepth, rec.Controls, _settings.DumpRowCap, _settings.GridRowCap, false);
            }
        }
        catch (Exception ex) { Log.Info("tab subtree dump not possible, using the window: " + ex.Message); }
        return rec;
    }

    private CacheRequest Cache(bool withFilter, bool withValues = false)
    {
        var lib = _automation.PropertyLibrary;
        var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.None };
        if (withFilter) cr.TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true);
        cr.Add(lib.Element.Name); cr.Add(lib.Element.ControlType); cr.Add(lib.Element.AutomationId); cr.Add(lib.Element.BoundingRectangle); cr.Add(lib.Element.IsPassword);
        cr.Add(lib.Element.IsOffscreen); cr.Add(lib.Element.AcceleratorKey); cr.Add(lib.Element.AccessKey);
        try { cr.Add(_automation.PatternLibrary.ValuePattern); cr.Add(lib.Value.IsReadOnly); } catch { }
        if (withValues)
        {
            try { cr.Add(lib.Value.Value); } catch { }
            try { cr.Add(_automation.PatternLibrary.TogglePattern); cr.Add(lib.Toggle.ToggleState); } catch { }
        }
        return cr;
    }

    /// <summary>
    /// Tries, in order: one cached request for the whole subtree (with and without the control-view filter), a live
    /// walk that survives bad nodes, a shallower live walk, and finally the MSAA tree. The first that yields ten
    /// controls wins; otherwise the richest. Every failure is counted for the health page.
    /// </summary>
    private ScreenRecord DumpTree(AutomationElement root, IntPtr hwnd, string title, bool onlyOnScreen = false)
    {
        var rec = new ScreenRecord { Title = title, Captured = DateTime.Now };
        var best = new List<ControlRow>(); var bestSource = "";
        void Consider(List<ControlRow> rows, string source) { if (rows.Count > best.Count) { best = rows; bestSource = source; } }
        foreach (var withFilter in new[] { true, false })
        {
            try
            {
                var rows = new List<ControlRow>();
                using (Cache(withFilter).Activate())
                {
                    var cached = _automation.FromHandle(hwnd);
                    WalkCached(cached, 0, _settings.DumpDepth, rows, _settings.DumpRowCap, _settings.GridRowCap, onlyOnScreen);
                }
                Consider(rows, withFilter ? "uia-cached" : "uia-cached-raw");
                if (rows.Count >= 10) break;
            }
            catch (Exception ex) { Log.Warn($"cached tree dump failed ('{title}', filter {withFilter}): {ex.Message}"); _status.DumpsFailed++; }
        }
        if (best.Count < 10)
        {
            try
            {
                var rows = new List<ControlRow>();
                Walk(_automation.TreeWalkerFactory.GetControlViewWalker(), root, 0, _settings.DumpDepth, rows, _settings.DumpRowCap);
                Consider(rows, "uia-live");
            }
            catch (Exception ex) { Log.Warn($"live tree walk failed ('{title}'): {ex.Message}"); _status.DumpsFailed++; }
        }
        if (best.Count < 10)
        {
            try
            {
                var rows = new List<ControlRow>();
                Walk(_automation.TreeWalkerFactory.GetRawViewWalker(), root, 0, Math.Min(8, _settings.DumpDepth), rows, 800);
                Consider(rows, "uia-raw");
            }
            catch (Exception ex) { Log.Warn($"raw tree walk failed ('{title}'): {ex.Message}"); _status.DumpsFailed++; }
        }
        if (best.Count < 10 && _settings.MsaaFallback)
        {
            try
            {
                var rows = Msaa.Dump(hwnd, _settings.DumpRowCap, _settings.DumpDepth);
                if (rows.Count > best.Count) { best = rows; bestSource = "msaa"; }
            }
            catch (Exception ex) { Log.Warn($"MSAA walk failed ('{title}'): {ex.Message}"); }
        }
        rec.Controls = best; rec.Source = bestSource;
        if (best.Count == 0) { _status.DumpsEmpty++; Log.Warn($"no control tree could be read for '{title}'"); }
        else _status.DumpsOk++;
        return rec;
    }

    private static readonly HashSet<string> GridTypes = new() { "DataGrid", "Table", "List", "Tree", "DataItem" };
    private static readonly HashSet<string> RowTypes = new() { "DataItem", "ListItem", "TreeItem", "Custom" };

    private static void WalkCached(AutomationElement el, int depth, int maxDepth, List<ControlRow> rows, int max, int gridRowCap, bool onlyOnScreen, bool withValues = false)
    {
        if (depth > maxDepth || rows.Count >= max) return;
        string ct = "Unknown";
        try
        {
            if (onlyOnScreen && depth > 0) { try { if (el.Properties.IsOffscreen.ValueOrDefault) return; } catch { } }
            var rect = el.Properties.BoundingRectangle.ValueOrDefault;
            ct = el.Properties.ControlType.ValueOrDefault.ToString();
            var name = (el.Properties.Name.ValueOrDefault ?? "").Replace("|", "/").Trim();
            var aid = el.Properties.AutomationId.ValueOrDefault ?? "";
            var pwd = false; var key = "";
            try { pwd = el.Properties.IsPassword.ValueOrDefault; } catch { }
            try { key = el.Properties.AcceleratorKey.ValueOrDefault ?? ""; if (key.Length == 0) key = el.Properties.AccessKey.ValueOrDefault ?? ""; } catch { }
            bool? ro = null; string? value = null;
            if (ct is "Edit" or "Document") { try { var vp = el.Patterns.Value.PatternOrDefault; if (vp is not null) ro = vp.IsReadOnly.ValueOrDefault; } catch { } }
            if (withValues)
            {
                if (ct is "Edit" or "ComboBox" or "Spinner" or "Document" or "DataItem" or "Custom") { try { value = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { } }
                else if (ct == "CheckBox") { try { var tp = el.Patterns.Toggle.PatternOrDefault; if (tp is not null) value = tp.ToggleState.ValueOrDefault.ToString(); } catch { } }
                if (value is not null) value = value.Replace("\r", " ").Replace("\n", " ").Trim();
                if (value is { Length: > 500 }) value = value[..500];
            }
            rows.Add(new ControlRow { Depth = depth, Type = ct, Name = name.Length > 120 ? name[..120] : name, AutomationId = aid, Position = $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}", IsPassword = pwd, Shortcut = key.Length > 24 ? "" : key, IsReadOnly = ro, Value = value });
        }
        catch { rows.Add(new ControlRow { Depth = depth, Type = ct, Name = "", AutomationId = "", Position = "" }); }
        AutomationElement[] children;
        try { children = el.CachedChildren; } catch { return; }
        if (GridTypes.Contains(ct) && ct != "DataItem")
        {
            // headers in full, the first few rows, then one note with the count and the column names
            var dataRows = 0; var kept = 0; var columns = new List<string>();
            foreach (var c in children)
            {
                if (rows.Count >= max) break;
                string cct; try { cct = c.Properties.ControlType.ValueOrDefault.ToString(); } catch { cct = "Unknown"; }
                if (RowTypes.Contains(cct))
                {
                    dataRows++;
                    if (kept < gridRowCap) { kept++; WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues); }
                    else if (columns.Count == 0 && kept > 0) { try { columns = c.CachedChildren.Select(x => { try { return x.Properties.Name.ValueOrDefault ?? ""; } catch { return ""; } }).Where(n => n.Length > 0).Take(30).ToList(); } catch { } }
                    continue;
                }
                WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues);
            }
            if (dataRows > kept)
            {
                if (columns.Count == 0) columns = rows.Where(r => r.Depth == depth + 2 && r.Type is "HeaderItem" or "Header").Select(r => r.Name).Where(n => n.Length > 0).Take(30).ToList();
                rows.Add(new ControlRow { Depth = depth + 1, Type = "Note", Name = $"... {dataRows - kept} more rows ({dataRows} in all){(columns.Count > 0 ? "; columns: " + string.Join(", ", columns) : "")}" });
            }
            return;
        }
        foreach (var c in children)
        {
            if (rows.Count >= max) break;
            WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues);
        }
    }

    /// <summary>Live walk that records what it can of a node and still descends when a property read fails.</summary>
    private static void Walk(ITreeWalker walker, AutomationElement el, int depth, int maxDepth, List<ControlRow> rows, int max)
    {
        if (depth > maxDepth || rows.Count >= max) return;
        var row = new ControlRow { Depth = depth, Type = "Unknown" };
        try { var rect = el.Properties.BoundingRectangle.ValueOrDefault; row.Position = $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}"; } catch { }
        try { row.Type = el.Properties.ControlType.ValueOrDefault.ToString(); } catch { }
        try { var name = (el.Properties.Name.ValueOrDefault ?? "").Replace("|", "/").Trim(); row.Name = name.Length > 120 ? name[..120] : name; } catch { }
        try { row.AutomationId = el.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
        try { row.IsPassword = el.Properties.IsPassword.ValueOrDefault; } catch { }
        if (row.Type is "Edit" or "Document") { try { var vp = el.Patterns.Value.PatternOrDefault; if (vp is not null) row.IsReadOnly = vp.IsReadOnly.ValueOrDefault; } catch { } }
        rows.Add(row);
        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { }
        var guard = 0;
        while (child is not null && rows.Count < max && guard++ < 2000)
        {
            Walk(walker, child, depth + 1, maxDepth, rows, max);
            try { child = walker.GetNextSibling(child); } catch { break; }
        }
    }

    public void Dispose()
    {
        Detach();
        _rereadTimer?.Dispose();
        try { _automation.Dispose(); } catch { }
    }
}
