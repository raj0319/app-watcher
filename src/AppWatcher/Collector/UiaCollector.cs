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
    /// <summary>Dropdown suggestions or grid rows.</summary>
    public List<string>? Items;
}

/// <summary>
/// Subscribes to UI Automation events of ONE process (focus, invoke, selection, toggle, value and text changes)
/// and to WinEvents for that process (window open/close/title, menus, MSAA invoke/focus/state) plus the global
/// foreground change. Never installs a keyboard hook. Produces RawEvents into a channel; does no file work itself.
/// Presses are seen four ways, first one wins per press: UIA Invoke, MSAA EVENT_OBJECT_INVOKED or a pressed
/// state change, a menu closing right after a menu item was highlighted, and (inferred) a focus on a button or
/// menu item followed within half a second by a window opening, closing or changing.
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
    private (string name, DateTime t) _lastPress = ("", DateTime.MinValue);
    private (string name, DateTime t) _lastMenuItem = ("", DateTime.MinValue);
    private DateTime _menuOpenedAt = DateTime.MinValue;
    private int _stateEventsThisSecond; private DateTime _stateSecond = DateTime.MinValue;
    private (string name, string aid, string ct, DateTime t) _lastField = ("", "", "", DateTime.MinValue);
    private readonly Dictionary<string, string> _lastTyped = new();
    private bool _firstWindow = true;

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
        lock (_gate) { _rows.Clear(); _lastDump.Clear(); _activeTab.Clear(); _menuPopups.Clear(); }
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
        lock (_gate) return _activeTab.TryGetValue(hwnd, out var tab) && title.Length > 0 ? $"{title} › {tab}" : title;
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
            if (hwnd == _foreground && title == _currentTitle) return;
            if (title != _currentTitle) _previousTitle = _currentTitle;
            _foreground = hwnd;
            _currentTitle = title;
            if (Excluded(title)) { _status.ExcludedEvents++; return; }
            Emit(new RawEvent { Act = Acts.Foreground, Hwnd = hwnd, WindowTitle = CurrentTitle, Ct = "Window", Name = CurrentTitle });
            if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false);
            else MaybeRedump(hwnd, title);
            InferPress("window changed", from: _previousTitle);
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
                lock (_gate) { _rows.Remove(hwnd); _activeTab.Remove(hwnd); }
                if (Excluded(title)) return;
                Emit(new RawEvent { Act = Acts.Close, Hwnd = hwnd, WindowTitle = title, Ct = "Window", Name = title });
                InferPress("window closed", from: title);
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
                break;
            case Win32.EVENT_SYSTEM_MENUPOPUPEND:
            case Win32.EVENT_SYSTEM_MENUEND:
                MenuClosed();
                break;
        }
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

    /// <summary>A push button whose state gained PRESSED is a press (toolbars that raise no Invoke). Rate limited: state changes are frequent.</summary>
    private void OnMsaaState(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront || idObject != Win32.OBJID_CLIENT) return;
        var now = DateTime.Now;
        if (now.Second != _stateSecond.Second) { _stateSecond = now; _stateEventsThisSecond = 0; }
        if (++_stateEventsThisSecond > 40) return;
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        if (o.Role is Msaa.RolePushButton or Msaa.RoleSplitButton or Msaa.RoleButtonDropDown && (o.State & Msaa.StatePressed) != 0)
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = "Button", Name = o.Name, How = PressHow.Msaa, WindowTitle = CurrentTitle });
    }

    /// <summary>
    /// Focus on a button or menu item in window X, then within half a second a window opened or changed (from X) or X
    /// closed, and no press was recorded since: count it as a press of that button. The window check keeps the default
    /// button of a dialog that just opened, or a button that got focus back when a dialog closed, from counting.
    /// </summary>
    private void InferPress(string why, string from)
    {
        if (!_settings.PressInference) return;
        (string name, string aid, string ct, string win, DateTime t) a; (string name, DateTime t) p;
        lock (_gate) { a = _lastActionable; p = _lastPress; }
        if (a.name.Length == 0) return;
        var age = (DateTime.Now - a.t).TotalMilliseconds;
        if (age > 600) return;
        if (from.Length > 0 && a.win.Length > 0 && Core.Learning.Titles.Key(a.win) != Core.Learning.Titles.Key(from)) return;
        if (p.t >= a.t.AddMilliseconds(-50)) return; // a real press was already recorded for this click
        lock (_gate) _lastActionable = ("", "", "", "", DateTime.MinValue);
        _status.PressesInferred++;
        Emit(new RawEvent { Act = Acts.Invoke, Ct = a.ct, Name = a.name, Aid = a.aid, How = PressHow.Inferred, WindowTitle = a.win.Length > 0 ? a.win : CurrentTitle });
        lock (_gate) _lastPress = (a.name, DateTime.Now);
    }

    /// <summary>All press paths go through here so one click is one event whichever path saw it first.</summary>
    private void EmitPress(RawEvent r)
    {
        lock (_gate)
        {
            // one click, one event: the MSAA pressed state can flicker for a couple of seconds on the same button
            var window = r.How == PressHow.Msaa ? 1500 : 400;
            if (_lastPress.name == r.Name && (DateTime.Now - _lastPress.t).TotalMilliseconds < window) return;
            _lastPress = (r.Name, DateTime.Now);
            if (_lastActionable.name == r.Name) _lastActionable = ("", "", "", "", DateTime.MinValue);
        }
        switch (r.How)
        {
            case PressHow.Msaa: _status.PressesMsaa++; break;
            case PressHow.Menu: _status.PressesMenu++; break;
            case PressHow.Inferred: _status.PressesInferred++; break;
            default: _status.PressesUia++; break;
        }
        if (r.Ct == "MenuItem") lock (_gate) _lastMenuItem = (r.Name, DateTime.Now);
        Emit(r);
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
        if (cls is "tooltips_class32" or "IME" or "MSCTFIME UI" or "Shell_TrayWnd") return;
        if (cls == "#32768" || cls.Contains("ToolTip", StringComparison.OrdinalIgnoreCase)) { if (cls == "#32768") { lock (_gate) _menuPopups.Add(hwnd); _menuOpenedAt = DateTime.Now; } return; }
        if (Ignored(title)) return;
        var excluded = Excluded(title);
        _knownWindows[hwnd] = title;
        var handlers = new List<IDisposable>();
        _perWindow[hwnd] = handlers;
        var isDialogClass = cls == "#32770";
        var firstWindow = _firstWindow && !initial;
        var reachedFrom = _previousTitle.Length > 0 ? _previousTitle : _currentTitle;
        (string name, DateTime t) lastMenu; lock (_gate) lastMenu = _lastMenuItem;
        var menuPath = lastMenu.name.Length > 0 && (DateTime.Now - lastMenu.t).TotalSeconds <= 5 ? lastMenu.name : null;

        // UIA subscriptions scoped to this window's subtree only
        ThreadPool.QueueUserWorkItem(_ =>
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
                // a popup menu that is an ordinary window (toolkit menus, WinForms drop-downs): no screen, just a menu-open marker
                var menuish = rec.Controls.Any(c => c.Type is "Menu" or "MenuItem") && !rec.Controls.Any(c => c.Type is "Edit" or "DataGrid" or "Tab" or "Document");
                if (title.Length == 0 && rec.Controls.Count > 0 && rec.Controls.Count <= 120 && menuish)
                {
                    lock (_gate) _menuPopups.Add(hwnd);
                    _menuOpenedAt = DateTime.Now;
                    _knownWindows.Remove(hwnd);
                    return;
                }
                // a suggestion list (autocomplete, lookup) that opened while the user was typing: its items are the evidence
                if (title.Length == 0 && _inFront)
                {
                    var items = rec.Controls.Where(c => c.Type is "ListItem" or "DataItem" or "TreeItem" && c.Name.Length > 0).Select(c => c.Name).ToList();
                    (string name, string aid, string ct, DateTime t) field; string typed;
                    lock (_gate) { field = _lastField; typed = _lastTyped.GetValueOrDefault(field.aid.Length > 0 ? field.aid : field.name, ""); }
                    if (items.Count > 0 && field.name.Length + field.aid.Length > 0 && (DateTime.Now - field.t).TotalSeconds <= 8 && !rec.Controls.Any(c => c.Type is "MenuItem" or "Edit" or "Button"))
                    {
                        _status.Dropdowns++;
                        var pwd = Masking.IsSensitiveName(field.name) || Masking.IsSensitiveName(field.aid);
                        Emit(new RawEvent
                        {
                            Act = Acts.Dropdown, Ct = field.ct, Name = field.name, Aid = field.aid, Val = pwd ? Masking.Masked : typed, Hwnd = hwnd, WindowTitle = CurrentTitle,
                            Items = items.Take(30).Select(i => pwd ? Masking.Masked : (i.Length > 120 ? i[..120] : i)).ToList(),
                        });
                        _knownWindows.Remove(hwnd);
                        return;
                    }
                }
                // untitled helper windows (parking windows, drop-downs, tooltips) are not screens either
                if (title.Length == 0 && (rec.Controls.Count < 15 || !rec.Controls.Any(c => c.Type is "Edit" or "DataGrid" or "ComboBox" or "Button"))) { _knownWindows.Remove(hwnd); return; }
                _status.WindowsSeen++;
                if (rec.Source == "msaa") _status.DumpsMsaa++;
                if (excluded) { _status.ExcludedEvents++; return; }
                var (dialogLike, dialogText) = DialogDetector.Detect(cls, rec.Controls);
                if (!dialogLike && _settings.DialogWindows.Any(p => DialogDetector.Wildcard(rec.Title, p))) dialogLike = true;
                if (dialogLike && (dialogText is null || dialogText.Length < 8)) dialogText = ReadDialogBody(el, rec.Controls) ?? dialogText;
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
                if (!initial) InferPress("window opened", from: reachedFrom);
            }
            catch (Exception ex)
            {
                Log.Warn($"window registration failed for '{title}': {ex.Message}");
            }
        });
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

    // ---- UIA callbacks (thread pool) ----

    private void OnFocus(AutomationElement el)
    {
        try
        {
            if (el.Properties.ProcessId.ValueOrDefault != _pid) return;
            var r = Describe(el, Acts.Focus);
            if (r.Ct == "Window" || r.Ct == "Pane" && r.Name.Length == 0) return;
            if (r.Ct == "MenuItem") lock (_gate) _lastMenuItem = (r.Name, DateTime.Now);
            else if (r.Ct is "Button" or "SplitButton" or "Hyperlink" or "TabItem") lock (_gate) _lastActionable = (r.Name, r.Aid, r.Ct, CurrentTitle, DateTime.Now);
            else if (r.Ct is "Edit" or "ComboBox" or "Spinner" or "Document") lock (_gate) _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now);
            Emit(r);
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
        }
        catch { }
    }

    /// <summary>A tab document was selected: events now belong to "Window › Tab", and the tab's content is a screen of its own.</summary>
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
        var top = hwnd;
        if (top == IntPtr.Zero) top = _foreground;
        var windowTitle = _knownWindows.TryGetValue(top, out var t) ? t : _currentTitle;
        lock (_gate) _activeTab[top] = tabName;
        var screenTitle = $"{windowTitle} › {tabName}";
        _previousTitle = _currentTitle.Length > 0 ? ScreenTitle(IntPtr.Zero, _currentTitle) : _previousTitle;
        Emit(new RawEvent { Act = Acts.Select, Ct = "TabItem", Name = tabName, WindowTitle = screenTitle });
        bool firstTime;
        lock (_dumpedTitles) firstTime = !_dumpedTitles.Contains(Core.Learning.Titles.Key(screenTitle));
        if (!firstTime) { Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top }); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(400);
                var rec = DumpSubtree(tab, screenTitle);
                if (rec.Controls.Count < 5)
                {
                    // MDI-style tabs: the content is a sibling of the tab strip, so take the window's visible controls instead
                    var el = _automation.FromHandle(top);
                    rec = DumpTree(el, top, screenTitle, onlyOnScreen: true);
                }
                rec.Kind = ScreenKinds.Tab;
                rec.ReachedFrom = windowTitle;
                lock (_dumpedTitles) _dumpedTitles.Add(Core.Learning.Titles.Key(screenTitle));
                _status.TabScreens++;
                Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top, Screen = rec });
            }
            catch (Exception ex) { Log.Warn($"tab dump failed for '{screenTitle}': {ex.Message}"); }
        });
    }

    private void OnValueChanged(AutomationElement el, PropertyId prop, object newValue)
    {
        try
        {
            var r = Describe(el, Acts.Value);
            var v = newValue as string ?? newValue?.ToString();
            r.Val = Masking.ForEvent(v, r.Name, r.Aid, r.IsPassword);
            _status.ValuesTyped++;
            lock (_gate) { _lastTyped[r.Aid.Length > 0 ? r.Aid : r.Name] = r.Val ?? ""; if (_lastField.name.Length == 0 || _lastField.aid != r.Aid) _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now); }
            Emit(r);
        }
        catch { }
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

    /// <summary>Reads the properties of a control in one cached call; an empty Name is resolved to the label a person would use.</summary>
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
        if (r.Ct is "Edit" or "ComboBox" or "Spinner" or "CheckBox" or "RadioButton" or "Document" && r.Name.EndsWith(':')) r.Name = r.Name.TrimEnd(':', ' ');
        if (r.Name.Length == 0 || Labels.IsWrapperId(r.Name))
        {
            // 1. the control's LabeledBy, 2. the nearest Text in the last tree dump, 3. the cleaned automation id (done by FieldKey)
            string? label = null;
            try { label = el.Properties.LabeledBy.ValueOrDefault?.Properties.Name.ValueOrDefault; } catch { }
            if (string.IsNullOrWhiteSpace(label) && r.Aid.Length > 0)
            {
                List<ControlRow>? rows;
                lock (_gate) _rows.TryGetValue(_foreground, out rows);
                var row = rows?.FirstOrDefault(x => x.AutomationId == r.Aid);
                if (row is not null) label = Labels.Resolve(row, rows!);
            }
            if (!string.IsNullOrWhiteSpace(label)) r.Name = label.Trim();
        }
        if (act == Acts.Select && r.Val is null) r.Val = Masking.ForEvent(r.Name, r.Name, r.Aid, r.IsPassword);
        return r;
    }

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

    private CacheRequest Cache(bool withFilter)
    {
        var lib = _automation.PropertyLibrary;
        var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.None };
        if (withFilter) cr.TreeFilter = new PropertyCondition(lib.Element.IsControlElement, true);
        cr.Add(lib.Element.Name); cr.Add(lib.Element.ControlType); cr.Add(lib.Element.AutomationId); cr.Add(lib.Element.BoundingRectangle); cr.Add(lib.Element.IsPassword);
        cr.Add(lib.Element.IsOffscreen); cr.Add(lib.Element.AcceleratorKey); cr.Add(lib.Element.AccessKey);
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

    private static void WalkCached(AutomationElement el, int depth, int maxDepth, List<ControlRow> rows, int max, int gridRowCap, bool onlyOnScreen)
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
            rows.Add(new ControlRow { Depth = depth, Type = ct, Name = name.Length > 120 ? name[..120] : name, AutomationId = aid, Position = $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}", IsPassword = pwd, Shortcut = key.Length > 24 ? "" : key });
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
                    if (kept < gridRowCap) { kept++; WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen); }
                    else if (columns.Count == 0 && kept > 0) { try { columns = c.CachedChildren.Select(x => { try { return x.Properties.Name.ValueOrDefault ?? ""; } catch { return ""; } }).Where(n => n.Length > 0).Take(30).ToList(); } catch { } }
                    continue;
                }
                WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen);
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
            WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen);
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
        try { _automation.Dispose(); } catch { }
    }
}
