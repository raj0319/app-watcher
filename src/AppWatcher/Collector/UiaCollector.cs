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
    /// <summary>For grid cell values: the row ("line 1"). Written with the event.</summary>
    public string? Row;
    /// <summary>A screen record to keep, with no event of its own (a second layout under a title already recorded).</summary>
    public bool ScreenOnly;
    /// <summary>A typed value from an editor lying over a grid cell: it waits for the cell read that names its column.</summary>
    public bool OverGridCell;
    /// <summary>Character keys seen since the focus came to the field (holds a typed value whose keystrokes arrive late).</summary>
    public int KeysSeen;
}

/// <summary>
/// Subscribes to UI Automation events of ONE process (focus, invoke, selection, toggle, value and text changes)
/// and to WinEvents for that process (window open/close/title, menus, mouse capture, MSAA invoke/focus/state) plus
/// the global foreground change. Never installs a keyboard or mouse hook, never presses anything. Produces RawEvents
/// into a channel; does no file work itself.
/// Presses are seen five ways, first one wins per click: UIA Invoke, MSAA EVENT_OBJECT_INVOKED or a pressed state,
/// a menu closing right after a menu item was highlighted, and (inferred) the control under the pointer when the left
/// button went down (sampled with GetAsyncKeyState at every event the collector receives: a read, not a hook), or the
/// button that had focus, when within 1.5 s a window or document opens or closes, a tab changes, a message appears, or the
/// program fills a value. Toolkits whose buttons announce nothing (Infragistics, DevExpress toolbars) are covered by that path.
/// Documents of an MDI workspace are the child windows of the MDICLIENT pane (created, shown, focused and destroyed events;
/// the one in front by z-order, read at most every two seconds); only the document's own subtree is a screen, its inner tab
/// pages are read after clicks and focus moves, and its fields are re-read once after each typed value, press, Enter or Tab
/// and focus move, so what the user typed into a grid cell and what the program filled in are both found (grid cells and
/// bound editors report no change). A Tab control inside a document is never taken for the workspace's strip.
/// </summary>
internal sealed class UiaCollector : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ChannelWriter<RawEvent> _out;
    private readonly List<WinEventHook> _hooks = new();
    private readonly Dictionary<IntPtr, List<IDisposable>> _perWindow = new();
    private readonly HashSet<string> _dumpedTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, List<ControlRow>> _rows = new();
    // the newest on-screen read of a window, the surface clicks are resolved on (labels keep using the recorded tree in _rows)
    private readonly Dictionary<IntPtr, List<ControlRow>> _clickRows = new();
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
    // the click and press decisions live in Core (ClickTracker) so the fixture replay runs the same code
    private readonly ClickTracker _tracker = new();
    private PressDecider _decider => _tracker.Decider;
    private World? _worldCache;
    private World _world => _worldCache ??= new World(this);
    // windows that had the keyboard focus (GetGUIThreadInfo, a read), newest last: what changed in them was typed
    private readonly List<(IntPtr hwnd, DateTime t)> _focusWindows = new();
    private readonly Dictionary<IntPtr, string> _focusAids = new();
    // the grid row the user typed in (the cell that had the focus when Enter or Tab went down), read again by place
    private sealed record TypedRow(string ReadKey, string Row, string Column, string CellPosition, List<FieldRead> Snapshot, DateTime T);
    private TypedRow? _typedRow;
    private readonly Dictionary<string, DateTime> _fieldReadAt = new(StringComparer.Ordinal);
    // lookup popups shown, by window, with their header line: the same list shown again while one is open is not a new lookup
    private readonly Dictionary<IntPtr, string> _lookupPopups = new();
    private DateTime _lastPointerRead = DateTime.MinValue;
    private System.Drawing.Point _lastPointerPress = new(-9999, -9999);
    private (string name, DateTime t) _lastPress = ("", DateTime.MinValue);
    private string? _lastPressHow;
    private (string name, DateTime t) _lastToggleFocus = ("", DateTime.MinValue);
    private readonly Dictionary<string, DateTime> _recentPageSelects = new(StringComparer.Ordinal);
    private System.Threading.Timer? _rereadAgainTimer;
    // MDI workspace: documents are child windows of a pane whose class contains MDICLIENT
    private readonly MdiWorkspace _mdi = new();
    private readonly Dictionary<IntPtr, IntPtr> _mdiChildren = new();   // child -> top-level window
    private readonly Dictionary<IntPtr, IntPtr> _mdiClients = new();    // top-level window -> MDI client pane
    private readonly Dictionary<IntPtr, IntPtr> _mdiActiveChild = new(); // top-level window -> the document in front
    private readonly HashSet<string> _mdiTitlesDumped = new(StringComparer.Ordinal);
    private DateTime _lastMdiPoll = DateTime.MinValue;
    // inner tab controls of the document in front: tab control -> selected page, as last read
    private readonly Dictionary<string, Dictionary<string, string>> _innerTabs = new(StringComparer.Ordinal);
    private (string name, DateTime t) _lastMenuItem = ("", DateTime.MinValue);
    private DateTime _menuOpenedAt = DateTime.MinValue;
    private int _stateEventsThisSecond; private DateTime _stateSecond = DateTime.MinValue;
    private (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) _lastField = ("", "", "", DateTime.MinValue, default);
    private (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) _previousField = ("", "", "", DateTime.MinValue, default);
    private readonly Dictionary<string, string> _lastTyped = new();
    // what the user's own keys last produced in a field (never the program's replacement)
    private readonly Dictionary<string, string> _lastKeyed = new();
    // when a key that types a character last went down (a read of the key state at events; which key is never kept)
    private DateTime _typingAt = DateTime.MinValue;
    // when the keyboard focus arrived in a field (by automation id and by label)
    private readonly Dictionary<string, DateTime> _focusArrivedAt = new(StringComparer.Ordinal);
    // when the keyboard focus last came to a window handle (GetGUIThreadInfo, sampled)
    private readonly Dictionary<IntPtr, DateTime> _focusSince = new();
    // character keys seen going down since the keyboard focus last moved (a lower bound: one per key per read)
    private int _keysSinceFocus;
    // the text box keys last went down in, and its text read then (WM_GETTEXT): the typed value as it was before Enter
    private (IntPtr hwnd, string text, DateTime t) _boxText = (IntPtr.Zero, "", DateTime.MinValue);
    // the box-read worker: one pending request (the newest wins), read with WM_GETTEXT off the button-read thread
    private readonly object _boxReqGate = new();
    private (IntPtr hwnd, System.Drawing.Rectangle place, DateTime t, bool enter)? _boxRequest;
    private readonly AutoResetEvent _boxSignal = new(false);
    private Thread? _boxWorker;
    private void StartBoxWorker()
    {
        if (_boxWorker is not null) return;
        _boxWorker = new Thread(() =>
        {
            while (true)
            {
                _boxSignal.WaitOne();
                (IntPtr hwnd, System.Drawing.Rectangle place, DateTime t, bool enter)? req;
                lock (_boxReqGate) { req = _boxRequest; _boxRequest = null; }
                if (req is not { } r || _pid == 0) continue;
                try { BoxRead(r.hwnd, r.place, r.t, r.enter); } catch (Exception ex) { Log.Error("box read failed", ex); }
                // keys are seen going down before the program has drawn them ("100EX" typed fast was read as "100"): one more read
                // a moment later, unless a newer request came, keeps the box's text up with the keys
                if (r.enter) continue;
                if (_boxSignal.WaitOne(150)) { _boxSignal.Set(); continue; }
                try { BoxFollowUp(r.hwnd, r.place); } catch (Exception ex) { Log.Error("box read failed", ex); }
            }
        }) { IsBackground = true, Name = "box read" };
        _boxWorker.Start();
    }

    /// <summary>
    /// The second read of a box, 150 ms after the keys: taken only when it extends what the first read showed by a few characters
    /// (the keys the program had not drawn yet), never the program's own answer (a name is not a few more characters of a code).
    /// </summary>
    private void BoxFollowUp(IntPtr typedBox, System.Drawing.Rectangle place)
    {
        var text = Win32.EditText(typedBox, 150);
        if (text is null) return;
        var now = DateTime.Now;
        lock (_gate)
        {
            var prev = _boxText.text;
            if (_boxText.hwnd != typedBox || _boxPlace != place || !FieldReads.ExtendsByKeys(prev, text)) return;
            _boxText = (typedBox, text, now);
            var same = _boxHistory.FindLastIndex(b => b.place == place && b.text == prev);
            if (same >= 0) _boxHistory[same] = (typedBox, text, place, now);
        }
    }

    /// <summary>The text of the box the keys went into, kept with where it was and when (WM_GETTEXT, up to 150 ms).</summary>
    private void BoxRead(IntPtr typedBox, System.Drawing.Rectangle place, DateTime now, bool enterNow)
    {
        var text = Win32.EditText(typedBox, 150);
        if (text is null && Win32.IsPlainEdit(typedBox))
        {
            // the program is too busy to answer (it builds a lookup list): the box's own picture shows what the keys wrote
            int keys; lock (_gate) keys = _keysSinceFocus;
            text = FieldReads.PixelTextFits(Native.BoxPicture.Text(typedBox), keys);
            if (text is not null)
            {
                lock (_gate) { _fromPicture[(place, text)] = now; if (_fromPicture.Count > 16) _fromPicture.Remove(_fromPicture.MinBy(kv => kv.Value).Key); }
                _status.BoxReadsFromPicture++;
                Log.Info($"box read from its picture ({text.Length} characters, {keys} keys): the program did not answer");
            }
        }
        if (text is null) return;
        lock (_gate)
        {
            if (!enterNow)
            {
                if (_boxPlace != place) _keysSinceFocus = 0; // the one editor moved to another cell: its keys start again
                _boxText = (typedBox, text, now); _boxPlace = place;
                var same = _boxHistory.FindLastIndex(b => b.place == place && (now - b.t).TotalSeconds <= 10);
                if (same >= 0) _boxHistory[same] = (typedBox, text, place, now);
                else { _boxHistory.Add((typedBox, text, place, now)); if (_boxHistory.Count > 8) _boxHistory.RemoveAt(0); }
                return;
            }
            // the last keys and Enter in the same read: taken when it extends what was read before, or when the editor moved to a
            // new cell (its text is that cell's keys); never the program's answer (a name is not a few more characters of a code)
            if (_boxText.hwnd == typedBox && text.StartsWith(_boxText.text, StringComparison.Ordinal) && text.Length <= _boxText.text.Length + 4) _boxText = (typedBox, text, now);
            else if (text.Length > 0 && (_boxHistory.Count == 0 || _boxHistory[^1].place != place)) { _boxText = (typedBox, text, now); _boxPlace = place; }
            if (text.Length > 0)
            {
                var same = _boxHistory.FindLastIndex(b => b.place == place && (now - b.t).TotalSeconds <= 10);
                if (same >= 0) { if (text.StartsWith(_boxHistory[same].text, StringComparison.Ordinal)) _boxHistory[same] = (typedBox, text, place, now); }
                else { _boxHistory.Add((typedBox, text, place, now)); if (_boxHistory.Count > 8) _boxHistory.RemoveAt(0); }
            }
        }
    }

    // where that box was when the keys went down (a toolkit grid moves its one editor to the next cell on Enter)
    private System.Drawing.Rectangle _boxPlace;
    // the last few boxes typed in: text, place, time (the next cell's typing must not hide what the last one held)
    private readonly List<(IntPtr hwnd, string text, System.Drawing.Rectangle place, DateTime t)> _boxHistory = new();
    // box texts that came from the box's picture (look-alike characters are settled against the value the program shows later)
    private readonly Dictionary<(System.Drawing.Rectangle place, string text), DateTime> _fromPicture = new();
    // when the field's own value event last reported what the user typed (a later re-read that differs is the program's replacement)
    private readonly Dictionary<string, DateTime> _ownTypedAt = new();
    private bool _firstWindow = true;
    // document re-reads (what the program filled in)
    private readonly Dictionary<string, List<FieldRead>> _fieldReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ControlRow>> _fieldRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _userKeysSinceRead = new(StringComparer.Ordinal);
    private System.Threading.Timer? _rereadTimer;
    private string _rereadWhy = "";
    private IntPtr _rereadSurface;
    private readonly Dictionary<string, DateTime> _slowReads = new(StringComparer.Ordinal);
    // a click or key asked for a read since the last one (a later request for another reason must not cancel it)
    private bool _rereadAsked;
    private readonly Dictionary<string, DateTime> _closedTabs = new(StringComparer.Ordinal);
    private (string key, DateTime t) _lastLookup = ("", DateTime.MinValue);
    // one re-poll timer per top window for a document with no title yet: at most two tries per set of documents, only in front
    private readonly Dictionary<IntPtr, (System.Threading.Timer timer, int tries, string children)> _repoll = new();
    // (called under the gate: it never asks the program anything, so the 50 ms button read never waits on a busy program)
    private void RepollMdiLater(IntPtr top, string children)
    {
        if (!_inFront) return;
        lock (_repoll)
        {
            if (!_repoll.TryGetValue(top, out var r)) r = (new System.Threading.Timer(_ => { if (_inFront) PollMdi(top, force: true); }), 0, "");
            if (r.children != children) r = (r.timer, 0, children);
            if (r.tries >= 2) { _repoll[top] = r; return; }
            r.timer.Change(r.tries == 0 ? 1000 : 3000, Timeout.Infinite);
            _repoll[top] = (r.timer, r.tries + 1, children);
        }
    }
    // record names quoted in window titles seen this session (never written anywhere: only to tell a label that is data)
    private readonly HashSet<string> _quotedNames = new(StringComparer.OrdinalIgnoreCase);

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

    // processes whose windows live inside the program's own windows (a WebView2 or Chromium page, an embedded viewer): their
    // windows and controls are the program's; learned when one is seen inside, or owned by, a window of the program
    private readonly HashSet<uint> _helperPids = new();
    private bool IsProgramPid(int pid) => pid != 0 && ((uint)pid == _pid || HelperPid((uint)pid));
    private bool HelperPid(uint pid) { lock (_helperPids) return _helperPids.Contains(pid); }

    /// <summary>A window of the program, or a window of another process embedded in (or owned by) a window of the program.</summary>
    private bool IsProgramWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || _pid == 0) return false;
        var pid = Win32.ProcessIdOf(h);
        if (pid == _pid) return true;
        if (HelperPid(pid)) return true;
        var root = Win32.GetAncestor(h, Win32.GA_ROOT);
        var owner = Win32.GetAncestor(h, Win32.GA_ROOTOWNER);
        if ((root != IntPtr.Zero && Win32.ProcessIdOf(root) == _pid) || (owner != IntPtr.Zero && Win32.ProcessIdOf(owner) == _pid))
        {
            bool added; lock (_helperPids) added = _helperPids.Add(pid);
            if (added) Log.Info($"process {pid} ({Win32.ClassName(h)}) draws inside the program's window: its windows count as the program's");
            return true;
        }
        return false;
    }
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
        StartBoxWorker();
        Log.Info($"attaching to pid {pid}");
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, OnForeground));
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_MENUSTART, Win32.EVENT_SYSTEM_MENUPOPUPEND, pid, OnMenuEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_CAPTURESTART, Win32.EVENT_SYSTEM_CAPTUREEND, pid, OnCapture));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_HIDE, pid, OnWindowEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, pid, OnWindowEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_INVOKED, Win32.EVENT_OBJECT_INVOKED, pid, OnMsaaInvoked));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_FOCUS, Win32.EVENT_OBJECT_FOCUS, pid, OnMsaaFocus));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_STATECHANGE, Win32.EVENT_OBJECT_STATECHANGE, pid, OnMsaaState));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_SELECTION, Win32.EVENT_OBJECT_SELECTIONWITHIN, pid, (_, _, _, _) => { if (_inFront) NoteInput(); }));
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
        lock (_gate) { _rows.Clear(); _clickRows.Clear(); _lastDump.Clear(); _activeTab.Clear(); _menuPopups.Clear(); _tabStrips.Clear(); _lastTabPoll.Clear(); _fieldReads.Clear(); _fieldRows.Clear(); _fieldReadAt.Clear(); _focusWindows.Clear(); _focusAids.Clear(); _lookupPopups.Clear(); _typedRow = null; _mdi.Clear(); _mdiChildren.Clear(); _mdiClients.Clear(); _mdiActiveChild.Clear(); _innerTabs.Clear(); }
        lock (_repoll) { foreach (var r in _repoll.Values) r.timer.Dispose(); _repoll.Clear(); }
        _pid = 0;
        SetInFront(false);
    }

    private void SetInFront(bool v)
    {
        if (_inFront == v) return;
        _inFront = v;
        try { var fg = Win32.GetForegroundWindow(); Log.Info($"program {(v ? "in front" : "not in front")}: foreground pid {Win32.ProcessIdOf(fg)} class {Win32.ClassName(fg)}"); } catch { }
        // the pointer's moves are watched only while the program is in front (nothing at all is seen otherwise)
        // a click with no event and no pointer move after it (a tab clicked, then the page read without moving) is heard by a
        // read of the button's state every 50 ms, only while the program is in front: one key-state read, no UI Automation work
        // (its own thread, never the thread pool: a slow read of a large grid can hold every pool thread for many seconds)
        if (v && _sampler is null && _pid != 0 && _settings.PressInference)
        {
            var stop = new CancellationTokenSource(); _samplerStop = stop;
            _sampler = new Thread(() =>
            {
                var last = DateTime.Now;
                while (!stop.IsCancellationRequested)
                {
                    Thread.Sleep(ButtonTickMs);
                    var t0 = DateTime.Now;
                    if ((t0 - last).TotalSeconds > 1) Log.Info($"button read: {(t0 - last).TotalSeconds:0.0} s since the last read");
                    try { OnButtonTick(); } catch (Exception ex) { Log.Error("button read failed", ex); }
                    last = DateTime.Now;
                    if ((last - t0).TotalSeconds > 1) Log.Info($"button read: one read took {(last - t0).TotalSeconds:0.0} s");
                }
            }) { IsBackground = true, Name = "button read", Priority = ThreadPriority.AboveNormal };
            _sampler.Start();
        }
        else if (!v && _sampler is not null) { _samplerStop?.Cancel(); _sampler = null; _samplerStop = null; }
        // the program left the front: memory is tidied once (at most once a minute), so the idle watcher stays small
        if (!v && (DateTime.Now - _lastTidy).TotalSeconds > 60)
        {
            _lastTidy = DateTime.Now;
            ThreadPool.QueueUserWorkItem(_ => { try { Thread.Sleep(3000); if (_inFront) return; System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; GC.Collect(2, GCCollectionMode.Forced, true, true); } catch { } });
        }
        InFrontChanged?.Invoke(v);
    }

    private Thread? _sampler;
    private DateTime _lastTidy = DateTime.MinValue;
    private CancellationTokenSource? _samplerStop;
    private const int ButtonTickMs = 50;

    /// <summary>The 50 ms read of the left button while the program is in front: a click that nothing else reported is heard here, at the pointer (it has not moved, or a move would have been heard first).</summary>
    private void OnButtonTick()
    {
        if (!_inFront || _pid == 0) return;
        // every 50 ms the left button (a press lasts about 100 ms, and some screens of the program read the "went down" bit
        // themselves, so the button seen down is the reliable sign); every 200 ms the same sample as at an event: Enter, Tab and
        // whether a character key went down (silent editors raise nothing while the user types)
        if (++_ticks % 4 == 0) { NoteInput(IntPtr.Zero); return; }
        var (wentDown, down) = Win32.KeyState(Win32.VK_LBUTTON);
        bool inProgress; lock (_gate) inProgress = _tracker.ButtonDownSeen;
        if (!wentDown && !down && !inProgress) return;
        NoteInput(IntPtr.Zero, at: System.Windows.Forms.Cursor.Position, buttonState: (wentDown, down));
    }
    private int _ticks;

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

    /// <summary>
    /// At every event the collector receives, and every 50 ms while the program is in front: a sample of the key state
    /// (GetAsyncKeyState, a read, never a hook) and of the pointer. A click is the button seen down (or a down since the last
    /// sample) and then up (<see cref="ClickSampler"/>); its point is where the pointer was when the button was first seen
    /// down. Enter or Tab while a document is in front re-reads it, and the grid row typed in is read again by place.
    /// </summary>
    private void NoteInput() => NoteInput(IntPtr.Zero);

    /// <summary>Same, from the destroy or hide of <paramref name="goingAway"/>: its tree is the first surface a click is resolved on.</summary>
    private void NoteInput(IntPtr goingAway, System.Drawing.Point? at = null, (bool wentDown, bool down)? buttonState = null)
    {
        if (!_inFront || _pid == 0) return;
        var pt = at ?? System.Windows.Forms.Cursor.Position;
        // a pointer that cannot be read comes back as 0,0 (another program's popup took the desktop's input): no sample
        if (pt.X == 0 && pt.Y == 0 && !Win32.GetCursorPosOk()) return;
        var front = FrontHwnd();
        var now = DateTime.Now;
        ClickOutcome o; bool key, enterNow = false, typedNow = false, menuDown, buttonWentDown; IntPtr typedBox = IntPtr.Zero;
        lock (_gate)
        {
            var (wentDown, down) = buttonState ?? Win32.KeyState(Win32.VK_LBUTTON); // one read: bit 0 and bit 15 together
            // the button just went down while a menu is open: the item under the pointer is the choice (read once, now, before
            // the popup closes on the button's release)
            var newDown = (wentDown || down) && !_buttonWasDown;
            _buttonWasDown = down;
            menuDown = newDown && _tracker.MenuOpen;
            buttonWentDown = newDown;
            // a pointer move reads only the button: the keys are left for the program's own events
            bool enter = false, tab = false, space = false;
            if (buttonState is null)
            {
                enter = Win32.WentDown(Win32.VK_RETURN); tab = Win32.WentDown(Win32.VK_TAB); space = Win32.WentDown(Win32.VK_SPACE);
                if (Win32.WentDown(Win32.VK_ESCAPE)) _escapeAt = now;
                if (tab) _tabAt = now;
                var keys = Win32.TypingKeysWentDown() + (space ? 1 : 0);
                // the system's last-input time moved with the pointer still and the button up: a key went down, even when another
                // program took the key's "went down" bit first (which key is never known)
                var lastInput = Win32.LastInputTick();
                // (it may be Tab or Enter whose own bit was taken: evidence that a key went down for a grid cell's commit, never a
                // typing key for the typed-or-filled test of a field)
                if (keys == 0 && !down && !wentDown && _lastInputTick != 0 && lastInput != _lastInputTick && pt == _lastKeyReadPt) _anyKeyAt = now;
                _lastInputTick = lastInput; _lastKeyReadPt = pt;
                if (keys > 0) { _typingAt = now; _keysSinceFocus += keys; typedNow = true; }
            }
            key = enter | tab; enterNow = enter;
            NoteFocusWindow(now);
            if (typedNow && _focusWindows.Count > 0) typedBox = _focusWindows[^1].hwnd;
            // Enter alone in this read (the last keys were seen in an earlier one): the box typed in is read once more before Enter's answer
            else if (enter && _boxText.hwnd != IntPtr.Zero && (now - _boxText.t).TotalSeconds is >= 0 and <= 3) typedBox = _boxText.hwnd;
            o = _settings.PressInference ? _tracker.Observe(new InputSample(now, pt.X, pt.Y, down, wentDown, key, (long)front, (long)goingAway, PressKeyWentDown: enter | space), _world) : ClickOutcome.None;
        }
        // keys went down in a text box: its text now (WM_GETTEXT, fast, no UI Automation) is what the keys wrote so far
        if (typedBox != IntPtr.Zero && (Thread.CurrentThread != _sampler || Excluded(_currentTitle))) typedBox = IntPtr.Zero;
        if (typedBox != IntPtr.Zero)
        {
            // the box's text is read by its own worker (WM_GETTEXT may wait while the program is busy; the button read never does)
            System.Drawing.Rectangle place = default; if (Win32.GetWindowRect(typedBox, out var br)) place = new System.Drawing.Rectangle(br.Left, br.Top, br.Right - br.Left, br.Bottom - br.Top);
            lock (_boxReqGate) _boxRequest = (typedBox, place, now, enterNow);
            _boxSignal.Set();
        }
        // keys went down in a field with no window of its own (Access, Delphi, some web pages): it is read once when the keys
        // pause (150 ms after the last one), before Tab or Enter moves on (a new record clears the form before any later read)
        if (typedNow && _settings.ReadAfterKeys && !Excluded(_currentTitle))
        {
            _focusedReadTimer ??= new System.Threading.Timer(_ => ReadFocusedTyped(), null, Timeout.Infinite, Timeout.Infinite);
            _focusedReadTimer.Change(150, Timeout.Infinite);
        }
        // (a WPF menu is an untitled popup window that raises no menu event: the button going down over such a window of the
        // program is a click inside a menu too, when the read finds a menu item there)
        if (!menuDown && buttonWentDown && UntitledPopupAt(pt)) menuDown = true;
        if (menuDown) ReadMenuItemAt(pt, now);
        if (key && _settings.ReadAfterKeys) { NoteTypedRow(now); ScheduleReread("key"); if (enterNow) ReadAtEnter(now); }
        if (o.Kind != ClickKind.None) OnClick(o, new System.Drawing.Point(o.X, o.Y));
    }

    // the grid cell the focus is in (its element and key), and the last value known for each cell
    private (AutomationElement? el, string key) _cellIn;
    private readonly Dictionary<string, string> _cellValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _cellValueAt = new(StringComparer.Ordinal);
    private int _cellReadBusy;

    /// <summary>
    /// Where the window is not read after keys (cells report themselves; Excel), a cell that reports nothing when its typing is
    /// committed would be lost: the cell's value is read once when the focus arrives in it and once 300 ms after the focus left
    /// it (one read of one cell each, never the window), and a difference goes through the same test as a value event.
    /// </summary>
    private void CellFocusMoved(AutomationElement el, RawEvent r)
    {
        var key = r.Aid.Length > 0 ? r.Aid : r.Name;
        if (key.Length == 0) return;
        (AutomationElement? el, string key) left;
        lock (_gate) { if (_cellIn.key == key) return; left = _cellIn; _cellIn = (el, key); }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var v0 = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                lock (_gate) { if (_cellValues.Count > 500) _cellValues.Clear(); if (v0 is not null && !_cellValues.ContainsKey(key)) _cellValues[key] = v0; }
                if (left.el is null) return;
                // the cell left is found again by its name in its grid (Excel's focused cell element follows the active cell); only
                // when it has told nothing by itself 1.2 s after the focus left it (a search of the grid is not free)
                var leftAt = DateTime.Now;
                Thread.Sleep(1200);
                lock (_gate) { if (_cellValueAt.TryGetValue(left.key, out var told) && told >= leftAt.AddMilliseconds(-200)) return; }
                // (the cell by its row and column through the grid's own Grid pattern: one cheap call, never a search of the grid,
                // which slows the program; one such read at a time)
                if (FieldReads.CellAddress(left.key) is not { } rc) return;
                if (Interlocked.Exchange(ref _cellReadBusy, 1) == 1) return;
                try
                {
                    var grid = _automation.TreeWalkerFactory.GetControlViewWalker().GetParent(el);
                    var gp = grid?.Patterns.Grid.PatternOrDefault;
                    if (gp is null) return;
                    // (the pattern counts the cells on screen, headers first: the top-left cell names where the screen starts)
                    var topLeft = gp.GetItem(1, 1);
                    if (FieldReads.CellAddress(topLeft?.Properties.Name.ValueOrDefault) is not { } tl) return;
                    var row = rc.row - tl.row + 1; var col = rc.col - tl.col + 1;
                    if (row < 1 || col < 1 || row >= gp.RowCount.ValueOrDefault || col >= gp.ColumnCount.ValueOrDefault) return;
                    var cell = gp.GetItem(row, col);
                    if (cell is null || (cell.Properties.Name.ValueOrDefault ?? "") != left.key) return;
                    var v = cell.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                    string? known; lock (_gate) _cellValues.TryGetValue(left.key, out known);
                    if (v is null || v == known) return;
                    HandleValue(cell, v);
                }
                finally { Interlocked.Exchange(ref _cellReadBusy, 0); }
            }
            catch { }
        });
    }

    /// <summary>The menu item sits on the window's menu bar (its parent is the MenuBar).</summary>
    private bool OnMenuBar(AutomationElement item)
    {
        try
        {
            if (_automation.TreeWalkerFactory.GetControlViewWalker().GetParent(item)?.Properties.ControlType.ValueOrDefault == ControlType.MenuBar) return true;
            // (an item that opens a menu of its own, "Edit" on a WPF menu bar or a submenu's parent, is never the choice)
            var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
            return ec is not null && ec.ExpandCollapseState.ValueOrDefault != FlaUI.Core.Definitions.ExpandCollapseState.LeafNode;
        }
        catch { return false; }
    }

    private System.Threading.Timer? _focusedReadTimer;

    /// <summary>
    /// The keys paused in a field with no window of its own: its value now (one UI Automation read of the focused element)
    /// goes through the same typed-or-filled test as a value event. A field with a window is read by WM_GETTEXT instead.
    /// </summary>
    private void ReadFocusedTyped()
    {
        try
        {
            if (!_inFront) return;
            var fe = _automation.FocusedElement();
            if (fe is null || !IsProgramPid(fe.Properties.ProcessId.ValueOrDefault)) return;
            if (fe.Properties.NativeWindowHandle.ValueOrDefault != IntPtr.Zero) return;
            var ct = fe.Properties.ControlType.ValueOrDefault;
            if (ct is not (ControlType.Edit or ControlType.ComboBox or ControlType.Document or ControlType.Spinner)) return;
            // (never an editor inside a grid: the row read decides cells, and a partial code typed into a busy grid is no value)
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            var up = walker.GetParent(fe);
            for (var i = 0; i < 4 && up is not null; i++, up = walker.GetParent(up))
                if (up.Properties.ControlType.ValueOrDefault is ControlType.DataGrid or ControlType.Table or ControlType.DataItem or ControlType.List or ControlType.Tree) return;
            var v = fe.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            if (v is null) return;
            HandleValue(fe, v);
        }
        catch { }
    }

    /// <summary>The window under the point is an untitled top-level window of the program other than the one in front (a popup).</summary>
    private bool UntitledPopupAt(System.Drawing.Point pt)
    {
        try
        {
            var h = Win32.WindowFromPoint(new Win32.Point { X = pt.X, Y = pt.Y });
            var root = h == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(h, Win32.GA_ROOT);
            return root != IntPtr.Zero && root != _foreground && IsProgramWindow(root) && Win32.WindowText(root).Length == 0;
        }
        catch { return false; }
    }

    // the menu item under the pointer when the button went down inside an open menu (the click's choice), and whether the
    // left button was down at the last read
    private (string name, DateTime t) _menuClick = ("", DateTime.MinValue);
    private bool _buttonWasDown;
    // the system's last-input time and the pointer at the last key read (a key went down when the first moved and the second did not)
    private uint _lastInputTick;
    // when the last-input time last said a key went down (which key unknown)
    private DateTime _anyKeyAt = DateTime.MinValue;
    private System.Drawing.Point _lastKeyReadPt;
    // the grid cells the keyboard focus went into and whose value has not been reported since (by name and id), with when
    private readonly Dictionary<string, DateTime> _visitedCells = new(StringComparer.Ordinal);
    // a typed cell's follow-up value within this time is the same typing
    private readonly Dictionary<string, DateTime> _cellGrace = new(StringComparer.Ordinal);
    // the value the program last filled into each field (window, field, row), to tell a repeat from a new fill
    private readonly Dictionary<string, (string val, DateTime t)> _lastFill = new(StringComparer.Ordinal);

    /// <summary>
    /// The button went down inside an open menu: the menu item under the pointer (ElementFromPoint, read only) is remembered as
    /// the choice; a menu-bar item or an item that opens a submenu is not one. When no menu-closed event follows (some toolkits
    /// raise none), the choice is counted a moment later.
    /// </summary>
    private void ReadMenuItemAt(System.Drawing.Point pt, DateTime t)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var el = _automation.FromPoint(pt);
                if (el is null || !IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return;
                var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
                AutomationElement? item = el;
                for (var i = 0; i < 3 && item is not null && item.Properties.ControlType.ValueOrDefault != ControlType.MenuItem; i++) item = walker.GetParent(item);
                if (item is null || item.Properties.ControlType.ValueOrDefault != ControlType.MenuItem) return;
                var parent = walker.GetParent(item);
                if (parent is not null && parent.Properties.ControlType.ValueOrDefault == ControlType.MenuBar) return;
                // a greyed-out item does nothing when clicked: never a choice
                try { if (!item.Properties.IsEnabled.ValueOrDefault) { lock (_gate) _lastMenuItem = ("", DateTime.MinValue); return; } } catch { }
                try
                {
                    var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
                    if (ec is not null && ec.ExpandCollapseState.ValueOrDefault != FlaUI.Core.Definitions.ExpandCollapseState.LeafNode) return;
                }
                catch { }
                var name = (item.Properties.Name.ValueOrDefault ?? "").Trim();
                if (name.Length == 0) return;
                var title = CurrentTitle;
                lock (_gate) _menuClick = (name, t);
                // no menu-closed event within 1.2 s: count the choice now, when the item is gone (the menu closed); an item still
                // on screen opened a submenu (Inform's "Pricing" does, and says nothing about it): not a choice
                Thread.Sleep(1200);
                bool still; lock (_gate) still = _menuClick.t == t;
                if (!still) return;
                bool shown; try { shown = !item.Properties.IsOffscreen.ValueOrDefault && !item.BoundingRectangle.IsEmpty; } catch { shown = false; }
                if (shown) return;
                lock (_gate) { _menuClick = ("", DateTime.MinValue); _lastMenuItem = ("", DateTime.MinValue); }
                EmitPress(new RawEvent { T = t, Act = Acts.Invoke, Ct = "MenuItem", Name = Labels.WithoutShortcut(name), How = PressHow.Menu, WindowTitle = title });
            }
            catch { }
        });
    }

    /// <summary>
    /// Enter went down: the field the user was in is read at once (one UI Automation read of the focused element), before the
    /// program answers. Inform puts the record's name where the code was typed half a second later, and a silent editor
    /// reports nothing while the user types: this read is the only moment the typed code is on screen. Only the field the
    /// user was in (same automation id), only a text field, only a value the last read did not already show.
    /// </summary>
    private int _enterReadBusy;
    private DateTime _enterReadAt = DateTime.MinValue;

    private void ReadAtEnter(DateTime now)
    {
        // one read at a time: Enter held down, or Enter through many cells while the program is busy, starts no pile of threads
        // (a read stuck on the busy program for over 3 s no longer blocks the next one)
        lock (_gate) { if (_enterReadBusy == 1 && (DateTime.Now - _enterReadAt).TotalSeconds < 3) return; _enterReadBusy = 1; _enterReadAt = DateTime.Now; }
        if (Excluded(_currentTitle)) { Interlocked.Exchange(ref _enterReadBusy, 0); return; }
        (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) field;
        lock (_gate) field = _lastField;

        // its own thread: the pool can be held by a slow document read, and this read must come before the program's answer
        new Thread(() =>
        {
            try
            {
                // the text box the keys went into, read just before Enter (no UI Automation: on time even while the program works)
                Thread.Sleep(200); // the box-read worker's read of this very moment lands first
                (IntPtr hwnd, string text, DateTime t) box; lock (_gate) box = _boxText;
                AutomationElement? el = null; string? v = null;
                if (box.hwnd != IntPtr.Zero && (now - box.t).TotalSeconds is >= 0 and <= 3 && box.text.Trim().Length > 0)
                {
                    try { el = _automation.FromHandle(box.hwnd); v = box.text; } catch { el = null; }
                }
                el ??= _automation.FocusedElement();
                if (el is null || !IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return;
                var r = Describe(el, Acts.Value);
                var late = (DateTime.Now - now).TotalMilliseconds;
                Log.Info($"read at Enter: {r.Ct} '{r.Name}' {r.Aid} {(v is not null ? "from the text read before Enter" : $"after {late:0} ms")}");
                if (r.Ct is not ("Edit" or "ComboBox") || r.IsPassword) return;
                if (el.Properties.NativeWindowHandle.ValueOrDefault is var eh && eh != IntPtr.Zero && Excluded(Win32.WindowText(Win32.GetAncestor(eh, Win32.GA_ROOT)))) return; // an excluded window (a login): nothing is kept
                // a UI Automation read that came more than 700 ms after Enter shows the program's answer, not the keys
                if (v is null && late > 700) return;
                // the field the user was in; or, when its focus event came late or not at all, the field whose own value
                // events reported keys in the last 10 s
                bool ownRecent; lock (_gate) ownRecent = _ownTypedAt.TryGetValue(r.Aid.Length > 0 ? r.Aid : r.Name, out var oa) && (now - oa).TotalSeconds <= 10;
                if (v is null && r.Aid != field.aid && !ownRecent) return;
                v ??= el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                if (string.IsNullOrWhiteSpace(v)) return;
                // a read-only box takes no keys; a box that shows what the last read of the window already showed was not typed in
                try { if (el.Patterns.Value.PatternOrDefault?.IsReadOnly.ValueOrDefault == true) return; } catch { }
                {
                    var (rk0, _, _, _, _) = ReadTarget();
                    List<FieldRead>? before; lock (_gate) _fieldReads.TryGetValue(rk0, out before);
                    if (before is not null && r.Aid.Length > 0 && before.Any(f => f.AutomationId == r.Aid && f.Value.Trim() == v.Trim())) return;
                }
                var key = r.Aid.Length > 0 ? r.Aid : r.Name;
                lock (_gate)
                {
                    var known = _lastKeyed.GetValueOrDefault(key) is { Length: > 0 } keyed ? keyed : _lastTyped.GetValueOrDefault(key);
                    // (what the keys produced, not what a later read showed: the program's name in the box must not make an older,
                    // shorter read of the keys look new)
                    if (known == v || known is { Length: > 0 } && known.StartsWith(v, StringComparison.Ordinal)) return; // its own event already had it (or more)
                    _lastTyped[key] = v; _ownTypedAt[key] = now; _userKeysSinceRead[r.Name] = DateTime.Now;
                }
                // the cell under an editor lying over a grid names its column itself: those values come from the row read
                System.Drawing.Rectangle rect = default; try { rect = el.BoundingRectangle; } catch { }
                var (rk, _, _, _, _) = ReadTarget();
                List<FieldRead>? last; lock (_gate) _fieldReads.TryGetValue(rk, out last);
                // an editor lying over a grid (its id is its window number, or the read finds a cell there): its value waits for the
                // row read that names the column and is dropped when that read reports it (no duplicate)
                if (last is not null && rect.Width > 0 && FieldReads.AtRect(last, $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}") is { IsCell: true }) r.OverGridCell = true;
                r.Val = Masking.ForEvent(v, r.Name, r.Aid, false); r.How = "read"; r.T = now;
                _status.ValuesTyped++;
                Emit(r);
            }
            catch { }
            finally { Interlocked.Exchange(ref _enterReadBusy, 0); }
        }) { IsBackground = true, Name = "read at Enter" }.Start();
    }

    /// <summary>The window with the keyboard focus in the program's GUI thread (GetGUIThreadInfo), remembered with the time. Call under the gate.</summary>
    private void NoteFocusWindow(DateTime now)
    {
        var f = Win32.FocusedWindowOf(_foreground);
        if (f == IntPtr.Zero) return;
        if (_focusWindows.Count > 0 && _focusWindows[^1].hwnd == f) { _focusWindows[^1] = (f, now); return; }
        _focusWindows.Add((f, now));
        // when the keyboard focus came to this window, by the program's own GUI thread (read every 200 ms at most: unlike the
        // UI Automation focus event, it is never seconds late when the watcher is busy)
        if (_focusSince.Count > 300) _focusSince.Clear();
        _focusSince[f] = now;
        _keysSinceFocus = 0;
        if (_focusWindows.Count > 4) _focusWindows.RemoveAt(0);
    }

    /// <summary>A click was heard of: log how it was resolved, select a tab page, read a document after a button in it, check the tree against the live element.</summary>
    private void OnClick(ClickOutcome o, System.Drawing.Point pt)
    {
        Log.Info($"click at {pt.X},{pt.Y}: {o.Kind}{(o.Hit is { } h ? $" {h.Row.Type} '{h.Row.Name.Trim()}' in {h.Screen}{(h.Live ? "" : " (window going away)")}" : "")}; {o.Trace}");
        if (o.Kind == ClickKind.Menu) return;
        if (o.Kind == ClickKind.Tab && o.Hit is { } t) { OnTabHit(t.Row.Name.Trim(), t.Screen, o.ClickedAt); return; }
        // a button inside a document: the document is read at 300 ms; a new row or a changed cell confirms the press
        // (Add Line Item adds a grid row and changes nothing else)
        if (o.Hit is { } b && b.Row.Type is "Button" or "SplitButton" && b.Screen.Contains(" \u203A ", StringComparison.Ordinal)) ScheduleReread("click " + b.Row.Name.Trim());
        if (!o.NeedsLiveRead) return;
        var clickedAt = o.ClickedAt;
        var atClick = _world.WindowAt(pt.X, pt.Y);
        var surfaceAtClick = atClick == 0 ? 0 : (_world.DocumentOf(atClick) is var d && d != 0 ? d : _world.RootOf(atClick));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            LiveElement? le = null;
            try
            {
                var el = _automation.FromPoint(pt);
                if (el is not null && IsProgramPid(el.Properties.ProcessId.ValueOrDefault))
                {
                    // a tab header drawn by a toolkit: the point may name a part of the item, or the strip itself
                    el = TabItemAt(el, pt) ?? el;
                    var r = Describe(el, Acts.Invoke);
                    var at = _world.WindowAt(pt.X, pt.Y);
                    var surface = at == 0 ? 0 : (_world.DocumentOf(at) is var dd && dd != 0 ? dd : _world.RootOf(at));
                    string pos = ""; try { var rc = el.BoundingRectangle; pos = $"{rc.Left},{rc.Top} {rc.Width}x{rc.Height}"; } catch { }
                    var enabled = true; try { enabled = el.Properties.IsEnabled.ValueOrDefault; } catch { }
                    le = new LiveElement(r.Name, r.Aid, r.Ct, pos, surface != 0 ? _world.ScreenOf(surface) : CurrentTitle, surface != 0 && surface == surfaceAtClick, enabled);
                }
            }
            catch { }
            // the window the click hit has closed meanwhile (an Exit on a small window): what is at the point now is the window
            // behind it and says nothing about the click; its closing confirms the press
            if (o.Hit is { } hitNow && !(Win32.IsWindow(new IntPtr(hitNow.Hwnd)) && Win32.IsWindowVisible(new IntPtr(hitNow.Hwnd)))) le = null;
            try {
            if (o.Kind == ClickKind.NoTarget) Log.Info($"click at {pt.X},{pt.Y}: live {le?.Ct} '{le?.Name}'{(le is { SameSurface: false } ? " (another window)" : "")}");
            LiveResult res;
            lock (_gate) res = _tracker.LiveRead(clickedAt, le);
            if (res.Cancelled) Log.Info($"click at {pt.X},{pt.Y}: the recorded tree was stale ({le?.Ct} '{le?.Name}' is at the point now): no press");
            if (res.TabPage is { } page) { OnTabHit(page, le!.Screen, clickedAt); return; }
            if (res.Confirmed is { } c) EmitPress(new RawEvent { Act = Acts.Invoke, Ct = c.Ct, Name = c.Name, Aid = c.Aid, How = PressHow.Inferred, WindowTitle = c.Win.Length > 0 ? c.Win : CurrentTitle, FromPointer = true });
            // any click in a window of the program changes what that window shows (a page of an inner tab strip, a tick box, a
            // new grid line): the window under the click is read once, whichever window is in front (Inform's notes window
            // stands beside a vendor document and keeps the foreground)
            else if (!res.Cancelled) ScheduleReread("click " + (o.Hit?.Row.Name.Trim() ?? le?.Name ?? ""), new IntPtr(surfaceAtClick));
            } catch (Exception ex) { Log.Error("click handling failed", ex); }
        });
    }

    /// <summary>The tab item at a point: the element itself, one of its parents (a part of the header), or the item of a tab strip under the point. Read only.</summary>
    private AutomationElement? TabItemAt(AutomationElement el, System.Drawing.Point pt)
    {
        try
        {
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            AutomationElement? e = el;
            for (var i = 0; i < 3 && e is not null; i++)
            {
                var ct = e.Properties.ControlType.ValueOrDefault;
                if (ct == ControlType.TabItem) return e;
                if (ct == ControlType.Tab)
                {
                    foreach (var item in e.FindAllChildren().Where(c => c.Properties.ControlType.ValueOrDefault == ControlType.TabItem)) // a true condition, filtered here (conditions on a WinForms tab control hide items)
                    {
                        var r = item.BoundingRectangle;
                        if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return item;
                    }
                    return null;
                }
                if (ct is ControlType.Window or ControlType.Pane && i > 0) return null;
                e = walker.GetParent(e);
            }
        }
        catch { }
        return null;
    }

    /// <summary>The window the user works in: the document in front of the workspace, else the window in front (a dialog, the main window).</summary>
    private IntPtr FrontHwnd()
    {
        lock (_gate) return _mdiActiveChild.TryGetValue(_foreground, out var doc) && doc != IntPtr.Zero ? doc : _foreground;
    }

    /// <summary>The screen title of a window handle: "Main › Document" for a document, the window's title otherwise.</summary>
    private string ScreenTitleOf(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_mdiChildren.TryGetValue(hwnd, out var top)) { var doc = _mdi.TitleOf((long)hwnd) ?? ""; var main = _knownWindows.GetValueOrDefault(top, _currentTitle); return doc.Length > 0 ? TabDocuments.Title(main, doc) : main; }
            return _knownWindows.TryGetValue(hwnd, out var t) ? ScreenTitle(hwnd, t) : CurrentTitle;
        }
    }

    /// <summary>The window a recorded-tree hit belongs to: an MDI container's own buttons are the container's (its plain title), not the document's in front.</summary>
    private string HitWindowOf(IntPtr hwnd)
    {
        lock (_gate) { if (_mdiClients.ContainsKey(hwnd) && _knownWindows.TryGetValue(hwnd, out var t) && t.Length > 0) return t; }
        return ScreenTitleOf(hwnd);
    }

    /// <summary>True when the point lies on a menu popup of the watched program that is open now, or on the one just hidden (the click closed it).</summary>
    private bool OverMenuPopup(System.Drawing.Point pt, IntPtr hidden)
    {
        List<IntPtr> popups; lock (_gate) popups = _menuPopups.ToList();
        foreach (var h in popups)
        {
            if (h != hidden && !Win32.IsWindowVisible(h)) continue;
            if (!Win32.GetWindowRect(h, out var r)) continue;
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return true;
        }
        return false;
    }

    /// <summary>The program's windows as the click tracker sees them: the trees kept per handle and Win32 reads.</summary>
    private sealed class World : IWindowWorld
    {
        private readonly UiaCollector _c;
        public World(UiaCollector c) => _c = c;
        public IReadOnlyList<ControlRow>? RowsOf(long hwnd) { lock (_c._gate) return _c._clickRows.TryGetValue(new IntPtr(hwnd), out var c) ? c : _c._rows.TryGetValue(new IntPtr(hwnd), out var r) ? r : null; }
        public bool IsVisible(long hwnd) => Win32.IsWindow(new IntPtr(hwnd)) && Win32.IsWindowVisible(new IntPtr(hwnd));
        public long WindowAt(int x, int y)
        {
            var h = Win32.WindowFromPoint(new Win32.Point { X = x, Y = y });
            return h != IntPtr.Zero && _c.IsProgramWindow(h) ? (long)h : 0;
        }
        public long RootOf(long hwnd) => hwnd == 0 ? 0 : (long)Win32.GetAncestor(new IntPtr(hwnd), Win32.GA_ROOT);
        public long DocumentOf(long hwnd)
        {
            var h = new IntPtr(hwnd);
            for (var i = 0; i < 40 && h != IntPtr.Zero; i++)
            {
                lock (_c._gate) { if (_c._mdiChildren.ContainsKey(h)) return (long)h; }
                if (Win32.MdiClientOf(h) != IntPtr.Zero) return (long)h; // a child of an MDICLIENT pane not seen yet
                if (Win32.IsTopLevel(h)) break;
                h = Win32.GetParent(h);
            }
            return 0;
        }
        public string ScreenOf(long hwnd) => _c.HitWindowOf(new IntPtr(hwnd));
        public bool MenuPopupAt(int x, int y, long goingAway) => _c.OverMenuPopup(new System.Drawing.Point(x, y), new IntPtr(goingAway));
        public IReadOnlyList<ControlRow>? FreshRowsOf(long hwnd) { lock (_c._gate) return _c._clickRows.TryGetValue(new IntPtr(hwnd), out var r) ? r : null; }
    }

    /// <summary>A click landed on a page of a document's inner tab control: the page is selected (recorded once), and the document is read again.</summary>
    private void OnTabHit(string page, string screen, DateTime now)
    {
        if (page.Length == 0) return;
        var key = screen + "\u0001" + page;
        lock (_gate)
        {
            foreach (var stale in _recentPageSelects.Where(k => (now - k.Value).TotalSeconds > 10).Select(k => k.Key).ToList()) _recentPageSelects.Remove(stale);
            if (_recentPageSelects.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < 1500) return;
            _recentPageSelects[key] = now;
            if (_innerTabs.TryGetValue(screen, out var pages)) { var k = pages.FirstOrDefault(x => x.Value == page).Key; if (k is not null) pages[k] = page; }
        }
        _status.InnerTabSelections++;
        Emit(new RawEvent { Act = Acts.Select, Ct = "TabItem", Name = page, Val = page, How = "read", WindowTitle = screen, Hwnd = _foreground });
        InferPress("tab changed", from: screen);
        ScheduleReread("tab " + page);
    }

    private void OnForeground(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        // the event can arrive after the foreground moved on (another program's popup took it for a moment and the program got it
        // back): what is in front now decides, never the late event alone (r16 and r33 were deaf for 70 s that way)
        var current = Win32.GetForegroundWindow();
        // (both ways: a late event naming the program while another program is already in front does not put it in front)
        if (current != IntPtr.Zero && current != hwnd) hwnd = current;
        if (hwnd == IntPtr.Zero) { SetInFront(false); return; }
        var mine = _pid != 0 && IsProgramWindow(hwnd);
        if (mine)
        {
            // the event may name a child control (a WinForms button has its own hwnd): always work with the top-level window
            var top = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
            if (top != IntPtr.Zero) hwnd = top;
            SetInFront(true); // a menu popup of the program is the program in front too
            lock (_gate) { if (_menuPopups.Contains(hwnd)) return; }
            var title = Win32.WindowText(hwnd);
            NoteInput();
            // untitled helper windows (parking windows, drop-downs) do not change which screen the user is on; when the window
            // the user was in has gone meanwhile, its owner (the window the drop-down belongs to) is
            if (title.Length == 0)
            {
                if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false);
                if (!FrontStillThere()) FollowFront(hwnd, "untitled window in front");
                return;
            }
            TakeFront(hwnd, title);
        }
        else SetInFront(false);
    }

    /// <summary>The window the user works in is now <paramref name="hwnd"/> (titled, of the program): the same steps for a
    /// foreground event and for the hand-over after the window in front went away.</summary>
    private void TakeFront(IntPtr hwnd, string title)
    {
        if (hwnd == _foreground && title == _currentTitle) { PollTabs(hwnd, force: false); PollMdi(hwnd, force: false); return; }
        if (title != _currentTitle) _previousTitle = _currentTitle;
        _foreground = hwnd;
        _currentTitle = title;
        if (Excluded(title)) { _status.ExcludedEvents++; return; }
        Emit(new RawEvent { Act = Acts.Foreground, Hwnd = hwnd, WindowTitle = CurrentTitle, Ct = "Window", Name = CurrentTitle });
        if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false);
        else MaybeRedump(hwnd, title);
        InferPress("window changed", from: _previousTitle);
        PollTabs(hwnd, force: true);
        PollMdi(hwnd, force: true);
    }

    /// <summary>The window the user was in is still a visible window.</summary>
    private bool FrontStillThere() => _foreground != IntPtr.Zero && Win32.IsWindow(_foreground) && Win32.IsWindowVisible(_foreground);

    /// <summary>
    /// The window in front went away with no foreground event for its successor yet: the window in front now, or the owner of
    /// the untitled drop-down in front, takes over (<see cref="FrontWindow.Pick"/>: never an untitled helper, never another
    /// program). UI thread only, never from a read: a read takes what this decided.
    /// </summary>
    private void FollowFront(IntPtr start, string why)
    {
        var chain = new List<WindowFacts>();
        var h = start == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(start, Win32.GA_ROOT);
        for (var i = 0; i < 8 && h != IntPtr.Zero; i++)
        {
            var t = Win32.WindowText(h);
            bool menu; lock (_gate) menu = _menuPopups.Contains(h);
            chain.Add(new WindowFacts((long)h, t, Win32.IsWindowVisible(h), IsProgramWindow(h), menu, t.Length > 0 && Excluded(t)));
            h = Win32.GetWindow(h, Win32.GW_OWNER);
        }
        if (FrontWindow.Pick(chain, (long)_foreground) is not { } next) return;
        Log.Info($"window in front went away ({why}): {next.Hwnd} takes over");
        TakeFront(new IntPtr(next.Hwnd), next.Title);
    }

    private void OnWindowEvent(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        bool knownTop, knownChild;
        lock (_gate) { knownTop = _knownWindows.ContainsKey(hwnd); knownChild = _mdiChildren.ContainsKey(hwnd); }
        bool knownMenu; lock (_gate) knownMenu = _menuPopups.Contains(hwnd);
        if (ev is Win32.EVENT_OBJECT_DESTROY or Win32.EVENT_OBJECT_HIDE && (knownTop || knownChild || knownMenu)) NoteInput(goingAway: hwnd);
        else if (ev is Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_DESTROY or Win32.EVENT_OBJECT_HIDE) NoteInput();
        switch (ev)
        {
            case Win32.EVENT_OBJECT_CREATE:
            case Win32.EVENT_OBJECT_SHOW:
                if (!Win32.IsTopLevel(hwnd))
                {
                    // a document of an MDI workspace: a child window of the MDICLIENT pane
                    bool known; lock (_gate) known = _mdiChildren.ContainsKey(hwnd);
                    if (known || Win32.MdiClientOf(hwnd) != IntPtr.Zero) MdiChildShown(hwnd);
                    return;
                }
                if (ev == Win32.EVENT_OBJECT_CREATE || _perWindow.ContainsKey(hwnd)) return;
                RegisterWindow(hwnd, initial: false);
                break;
            case Win32.EVENT_OBJECT_DESTROY:
            case Win32.EVENT_OBJECT_HIDE:
                bool wasMenu, wasMdiChild;
                lock (_gate) { wasMenu = _menuPopups.Remove(hwnd); wasMdiChild = _mdiChildren.ContainsKey(hwnd); }
                if (wasMenu) { MenuClosed(); return; }
                if (wasMdiChild) { if (ev == Win32.EVENT_OBJECT_DESTROY || !Win32.IsWindowVisible(hwnd)) MdiChildGone(hwnd); return; }
                if (!_knownWindows.TryGetValue(hwnd, out var title)) return;
                if (ev == Win32.EVENT_OBJECT_HIDE && Win32.IsWindowVisible(hwnd)) return;
                _knownWindows.Remove(hwnd);
                if (_perWindow.Remove(hwnd, out var list)) foreach (var d in list) { try { d.Dispose(); } catch { } }
                lock (_gate) { _rows.Remove(hwnd); _clickRows.Remove(hwnd); _activeTab.Remove(hwnd); _tabStrips.Remove(hwnd); _lastTabPoll.Remove(hwnd); }
                if (title.Length == 0 || Excluded(title)) return; // helper windows (lookups, tips, drop-downs) are not screens; their close is noise
                InferPress("window closed", from: title); // the button that closed it (OK, No, Cancel, Exit) is counted before the close
                Emit(new RawEvent { Act = Acts.Close, Hwnd = hwnd, WindowTitle = title, Ct = "Window", Name = title });
                if (_foreground != IntPtr.Zero && _foreground != hwnd) { PollTabs(_foreground, force: true); PollMdi(_foreground, force: true); }
                // the window in front closed or hid (some programs hide a form instead of destroying it): the window in front
                // now takes over at once when Windows already moved on, else the foreground event that follows does
                else if (_foreground == hwnd && _inFront) FollowFront(Win32.GetForegroundWindow(), "closed");
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                bool mdiChild; lock (_gate) mdiChild = _mdiChildren.ContainsKey(hwnd);
                if (mdiChild) { MdiChildShown(hwnd); return; } // a document renamed (the record loaded): the title is read again
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
        NoteInput();
        switch (ev)
        {
            case Win32.EVENT_SYSTEM_MENUSTART:
            case Win32.EVENT_SYSTEM_MENUPOPUPSTART:
                MenuOpened();
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
        NoteInput();
        if (ev == Win32.EVENT_SYSTEM_CAPTURESTART && _inFront) ReadPointer("capture");
    }

    /// <summary>A menu closed: if an item was highlighted or focused just before, that item was chosen (unless a real press already said so).</summary>
    private void MenuOpened() { lock (_gate) { _menuOpenedAt = DateTime.Now; _tracker.MenuOpened(); } }
    private DateTime _escapeAt = DateTime.MinValue;
    // when Tab last went down (the focus moves to the next field: a value that appears there at once is the program's)
    private DateTime _tabAt = DateTime.MinValue;

    private void MenuClosed()
    {
        var closedAt = DateTime.Now;
        lock (_gate) _tracker.MenuClosed(closedAt);
        if (Win32.WentDown(Win32.VK_ESCAPE)) lock (_gate) _escapeAt = closedAt; // read here too: it may have been the key that closed it
        bool escaped; lock (_gate) escaped = (closedAt - _escapeAt).TotalMilliseconds is >= 0 and < 500 && _escapeAt > _menuOpenedAt;
        var openedAt = _menuOpenedAt; var title = CurrentTitle;
        if (escaped) { lock (_gate) { _lastMenuItem = ("", DateTime.MinValue); _menuClick = ("", DateTime.MinValue); } return; } // closed with Escape: nothing was chosen
        // the chosen item's own focus event, and the read under the pointer, can land a moment after the menu closed (WPF): the
        // choice is made then (MenuChoice), and dated to the close
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(MenuChoice.LateFocusMs);
            (string name, DateTime t) clicked, focused;
            lock (_gate) { clicked = _menuClick; focused = _lastMenuItem; _menuClick = ("", DateTime.MinValue); _lastMenuItem = ("", DateTime.MinValue); }
            var name = MenuChoice.Pick(clicked, focused, openedAt, closedAt, escaped: false);
            if (name is null) return;
            EmitPress(new RawEvent { T = closedAt, Act = Acts.Invoke, Ct = "MenuItem", Name = name, How = PressHow.Menu, WindowTitle = title });
        });
    }

    private void OnMsaaInvoked(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront) return;
        NoteInput();
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        var ct = Msaa.ControlType(o.Role);
        if (ct is "Window" or "Pane" or "Edit" or "Text" or "Document") return;
        // a button that renames itself when pressed (Search becomes Reset) is reported under its new name: the click's own read,
        // taken when the button went down, has the name the user clicked
        var name = o.Name;
        lock (_gate)
            if (ct == "Button" && _decider.ClickedRecently(DateTime.Now) && _decider.Pending is { Ct: "Button" } p && p.Name.Length > 0 && p.Name != name) name = p.Name;
        EmitPress(new RawEvent { Act = Acts.Invoke, Ct = ct, Name = name, How = PressHow.Msaa, WindowTitle = CurrentTitle });
    }

    private void OnMsaaFocus(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront && _menuOpenedAt == DateTime.MinValue) return;
        NoteInput();
        // the focus went into a document of the workspace: it may have come to the front
        bool mdi; lock (_gate) mdi = _mdiClients.Count > 0;
        if (mdi && _foreground != IntPtr.Zero) PollMdi(_foreground, force: false);
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        if (o.Role == Msaa.RoleMenuItem) { lock (_gate) _lastMenuItem = (o.Name, DateTime.Now); return; }
        if (o.Role is Msaa.RolePushButton or Msaa.RoleSplitButton or Msaa.RoleButtonDropDown or Msaa.RoleButtonMenu or Msaa.RoleLink)
            lock (_gate) _lastActionable = (o.Name, "", Msaa.ControlType(o.Role), CurrentTitle, DateTime.Now);
    }

    /// <summary>
    /// A state change in the watched program (rate limited: they are frequent). A push button whose state gained PRESSED is a
    /// press (toolbars that raise no Invoke), and the moment to read what is under the pointer; a state change on hover
    /// (Infragistics toolbars raise one) is not, so nothing is remembered for it.
    /// </summary>
    private void OnMsaaState(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_inFront) return;
        var now = DateTime.Now;
        if (now.Second != _stateSecond.Second) { _stateSecond = now; _stateEventsThisSecond = 0; }
        if (++_stateEventsThisSecond > 40) return;
        NoteInput();
        if (idObject != Win32.OBJID_CLIENT) return;
        var o = Msaa.FromEvent(hwnd, idObject, idChild);
        if (o is null || o.Name.Length == 0) return;
        if ((o.State & Msaa.StatePressed) == 0) return;
        // no pointer read here: Infragistics raises this state on hover too, and a hovered button must never be credited with the click;
        // a pressed state counts only while the left button is down or right after a click
        bool clicking; lock (_gate) clicking = _tracker.ButtonDownSeen || _tracker.ClickedWithin(now);
        if (!clicking) return;
        if (o.Role is Msaa.RolePushButton or Msaa.RoleSplitButton or Msaa.RoleButtonDropDown)
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = "Button", Name = o.Name, How = PressHow.Msaa, WindowTitle = CurrentTitle });
    }

    /// <summary>
    /// Reads the control under the pointer (read only: ElementFromPoint, no hook) and remembers it as the pressed control when
    /// it is a button, menu item or link of the watched program. Throttled to once per 150 ms. Called on mouse capture, a
    /// PRESSED state and menu starts, which is when a click has just begun.
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
        var t = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var el = _automation.FromPoint(pt);
                if (el is null || !IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return;
                var r = Describe(el, Acts.Invoke);
                if (!PressDecider.IsActionable(r.Ct) || r.Name.Length == 0) return;
                lock (_gate) _decider.RememberPressed(new PressCandidate(r.Name, r.Aid, r.Ct, win), pt.X, pt.Y, t);
            }
            catch { }
        });
    }

    /// <summary>
    /// Something changed (a window opened, closed or changed, a document was created or destroyed, a tab or a message appeared,
    /// the program filled a value, the data changed) within 1.5 s after a click began, and no press was recorded for it: count
    /// the press. The click's target (read under the pointer when the button went down) comes first; while that read is still
    /// in flight the confirmation waits for it; the button that had focus second; for a window change a fresh read under the
    /// pointer last (the pointer still rests on the toolbar button that opened the document). The window check keeps the
    /// default button of a dialog that just opened, or a button that got focus back when a dialog closed, from counting.
    /// </summary>
    private void InferPress(string why, string from, DateTime? at = null, int maxAgeMs = 1500)
    {
        if (!_settings.PressInference) return;
        var now = at ?? DateTime.Now;
        (string name, string aid, string ct, string win, DateTime t) a; (string name, DateTime t) last;
        PressCandidate? c, pendingBefore; bool deferred, mayFocused, mayPointer;
        lock (_gate)
        {
            pendingBefore = _decider.Pending;
            c = _tracker.Confirm(now, why, from, out deferred);
            a = _lastActionable; last = _lastPress;
            mayFocused = _tracker.MayCreditFocused(now);
            mayPointer = _tracker.MayCreditPointer(now);
        }
        if (why != "value changed") Log.Info($"infer '{why}' from [{from}]: pending {(pendingBefore is null ? "none" : pendingBefore.Name + " in " + pendingBefore.Win)} -> {(c is not null ? "confirmed " + c.Name : deferred ? "waits for the live read" : "not confirmed")}");
        if (c is not null)
        {
            lock (_gate) _lastPointerPress = System.Windows.Forms.Cursor.Position;
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = c.Ct, Name = c.Name, Aid = c.Aid, How = PressHow.Inferred, WindowTitle = c.Win.Length > 0 ? c.Win : CurrentTitle, FromPointer = true });
            return;
        }
        if (deferred) return;
        bool Fresh(DateTime t) { var age = (now - t).TotalMilliseconds; return age >= -50 && age <= maxAgeMs; }
        // the focused button only when Enter or Space went down while it already had the focus: Enter in a field after which the
        // program moves the focus to a button (Edit, after a record loads; Exit on a window it opened) does not press that button
        DateTime keyAt; lock (_gate) keyAt = _tracker.PressKeyAt;
        if (mayFocused && a.name.Length > 0 && Fresh(a.t) && a.t <= keyAt.AddMilliseconds(30) && ClickTracker.ScreensRelated(a.win, from) && last.t < a.t.AddMilliseconds(-50))
        {
            lock (_gate) _lastActionable = ("", "", "", "", DateTime.MinValue);
            EmitPress(new RawEvent { Act = Acts.Invoke, Ct = a.ct, Name = a.name, Aid = a.aid, How = PressHow.Inferred, WindowTitle = a.win.Length > 0 ? a.win : CurrentTitle });
            return;
        }
        // nothing else: the control under the pointer at the time of the change is never credited (v0.9 and v0.10 did; it gave
        // "Rating" for a menu item whose document opened under the pointer, and Edit after a record loaded). A press comes from a
        // click resolved at its own point, from Enter on the focused button, or from the program's own events.
        _ = mayPointer;
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
            // one click, one event: the MSAA pressed state can flicker for a couple of seconds on the same button, an inferred
            // press can be seen by two triggers (a window opened and a tab changed), and the same name within ten seconds with
            // nothing recorded in between is the same click seen late
            var window = r.How is PressHow.Msaa or PressHow.Inferred ? 1500 : 400;
            if (_lastPress.name == r.Name && (DateTime.Now - _lastPress.t).TotalMilliseconds < window) return;
            if (r.How is PressHow.Inferred && _decider.IsRepeat(r.Name, DateTime.Now)) return;
            if (PressDecider.SameClick(_lastPress.t, _lastPressHow, r.How, _tracker.LastClickAt, _tracker.PressKeyAt, DateTime.Now)) return;
            _lastPress = (r.Name, DateTime.Now); _lastPressHow = r.How;
            _tracker.Pressed(r.Name, r.How ?? PressHow.Uia, DateTime.Now);
            if (_lastActionable.name == r.Name) _lastActionable = ("", "", "", "", DateTime.MinValue);
        }
        switch (r.How)
        {
            case PressHow.Msaa: _status.PressesMsaa++; break;
            case PressHow.Menu: _status.PressesMenu++; break;
            case PressHow.Inferred: _status.PressesInferred++; if (r.FromPointer) _status.PressesPointer++; break;
            default: _status.PressesUia++; break;
        }
        bool clicked; lock (_gate) clicked = _tracker.ClickedWithin(DateTime.Now, PressDecider.DocumentOpenMs) || _tracker.MayCreditFocused(DateTime.Now);
        if (!clicked && r.How is PressHow.Inferred) { _status.PressesWithoutClick++; Log.Info($"press without a click: {r.How} '{r.Name}' in {r.WindowTitle}"); }
        if (r.Ct == "MenuItem") lock (_gate) _lastMenuItem = (r.Name, DateTime.Now);
        Emit(r, press: true);
        ScheduleReread($"press {r.Name}");
    }

    // ---- MDI workspace: documents are child windows of the MDICLIENT pane ----

    /// <summary>A document window of the workspace was created, shown or renamed: it is in front now (or will be read as such).</summary>
    private void MdiChildShown(IntPtr child)
    {
        if (!_settings.TabDocuments) return;
        var top = Win32.GetAncestor(child, Win32.GA_ROOT);
        var client = Win32.MdiClientOf(child);
        if (top == IntPtr.Zero || client == IntPtr.Zero) return;
        var title = Win32.WindowText(child).Trim();
        lock (_gate)
        {
            _mdiChildren[child] = top;
            _mdiClients[top] = client;
            if (title.Length > 0) _mdi.Shown((long)child, title); else _mdi.Shown((long)child, "");
        }
        UpdateMdiStatus();
        PollMdi(top, force: true);
    }

    /// <summary>A document window was destroyed: the next one in z-order (if any) is the screen; its flow ends.</summary>
    private void MdiChildGone(IntPtr child)
    {
        IntPtr top;
        lock (_gate)
        {
            if (!_mdiChildren.Remove(child, out top)) return;
            _mdi.Destroyed((long)child);
            if (_mdiActiveChild.TryGetValue(top, out var active) && active == child) _mdiActiveChild.Remove(top);
            _rows.Remove(child); _clickRows.Remove(child);
            var rk = "doc:" + child;
            _fieldReads.Remove(rk); _fieldRows.Remove(rk); _fieldReadAt.Remove(rk);
        }
        PollMdi(top, force: true);
    }

    /// <summary>
    /// Reads which document is in front (GetTopWindow of the MDI client, a read) at most every two seconds after events, and
    /// makes it the screen when it changed: "Main › Document", with the document's own subtree. No document left: the window is plain again.
    /// </summary>
    private void PollMdi(IntPtr top, bool force)
    {
        if (!_settings.TabDocuments || top == IntPtr.Zero) return;
        IntPtr client;
        lock (_gate)
        {
            if (!_mdiClients.TryGetValue(top, out client)) return;
            if (!force && (DateTime.Now - _lastMdiPoll).TotalSeconds < 2) return;
            _lastMdiPoll = DateTime.Now;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var children = Win32.MdiChildrenFrontFirst(client);
                string? active; string? current; IntPtr activeChild = IntPtr.Zero;
                lock (_gate)
                {
                    foreach (var (h, _) in children) _mdiChildren[h] = top;
                    foreach (var gone in _mdiChildren.Where(kv => kv.Value == top && !children.Any(c => c.hwnd == kv.Key) && !Win32.IsWindow(kv.Key)).Select(kv => kv.Key).ToList()) { _mdiChildren.Remove(gone); _mdi.Destroyed((long)gone); }
                    active = _mdi.Reorder(children.Select(c => ((long)c.hwnd, c.title)));
                    if (active is { Length: 0 })
                    {
                        // a document without a title yet (just created): looked at again in 1 s and 3 s, since an idle document
                        // raises no event that would bring its title (Price Change was only seen when it closed)
                        RepollMdiLater(top, string.Join(",", children.Select(c => (long)c.hwnd)));
                        return;
                    }
                    current = _activeTab.GetValueOrDefault(top);
                    if (active is not null) activeChild = children.First(c => c.title.Trim() == active).hwnd;
                    if (active is not null) _mdiActiveChild[top] = activeChild; else _mdiActiveChild.Remove(top);
                }
                UpdateMdiStatus();
                if (active == current) return;
                // the document that was in front is gone (destroyed): its flow ends before the next document becomes the screen
                if (current is not null && !children.Any(c => c.title.Trim() == current)) TabGone(top, current);
                if (active is null) return;
                AutomationElement? el = null;
                try { el = _automation.FromHandle(activeChild); } catch { }
                ApplyTab(top, active, el, viaEvent: false, documentHwnd: activeChild);
            }
            catch (Exception ex) { Log.Info("MDI documents not read: " + ex.Message); }
        });
    }

    /// <summary>Documents seen as MDI children, and the ones never recorded as a screen, for the status file and the health page.</summary>
    private void UpdateMdiStatus()
    {
        lock (_gate)
        {
            _status.MdiDocuments = _mdi.TitlesSeen.Count;
            var never = _mdi.NeverRecorded(t => _mdiTitlesDumped.Contains(Core.Learning.Titles.Key(t))).Take(40).ToList();
            if (!never.SequenceEqual(_status.DocumentsNeverRecorded)) _status.DocumentsNeverRecorded = never;
        }
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
        if (cls == "#32768") { lock (_gate) _menuPopups.Add(hwnd); MenuOpened(); return; }
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

        // an untitled popup shown while the user types under a field: a lookup by its shape, decided now with Win32 reads only
        // (the program may be too busy to answer UI Automation before the list closes)
        var shapeLookup = title.Length == 0 && !initial && _inFront ? ShapeLookupCandidate(hwnd) : null;
        if (shapeLookup is not null) lock (_gate) _pendingShape = (shapeLookup, hwnd, DateTime.Now); // a list was seen under the field
        var shapeDone = 0;
        // (the shape alone is not enough: a search list flashes for full codes too; the shape is kept as the field's pending
        // lookup and becomes one only when the program completes a partial code there, see LookupCompleted)
        void ShapeFallback() { if (shapeLookup is { } sl && Interlocked.Exchange(ref shapeDone, 1) == 0) lock (_gate) _pendingShape = (sl, hwnd, DateTime.Now); }

        // UIA subscriptions scoped to this window's subtree only
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var el = _automation.FromHandle(hwnd);
                    var lib = _automation.EventLibrary;
                    if (!excluded && title.Length > 0) // an untitled popup (a lookup list) only needs its tree: registering events on a busy program times out
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
                            var menuOpened = el.RegisterAutomationEvent(lib.Element.MenuOpenedEvent, TreeScope.Subtree, (e, _) => MenuOpened());
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
                    // (an untitled popup is read at once: a lookup list may close within the second)
                    Thread.Sleep(initial || title.Length == 0 ? 50 : 400);
                    if (title.Length == 0 && !Win32.IsWindowVisible(hwnd)) { _status.PopupsGone++; _knownWindows.Remove(hwnd); ShapeFallback(); return; }
                    var rec = DumpTree(el, hwnd, title.Length > 0 ? title : Win32.WindowText(hwnd));
                    lock (_gate) { _rows[hwnd] = rec.Controls; _clickRows.Remove(hwnd); _lastDump[hwnd] = DateTime.Now; }
                    // a popup menu that is an ordinary window (toolkit menus, WinForms drop-downs): its items and access keys are kept, it is never a screen
                    if (DialogDetector.IsMenuWindow(rec.Controls) && rec.Controls.Count <= 120)
                    {
                        lock (_gate) _menuPopups.Add(hwnd);
                        MenuOpened();
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
                        if (rec.Controls.Count < 3) ShapeFallback(); // its tree could not be read: its shape decides
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
                        if (title.Length == 0 && !Win32.IsWindowVisible(hwnd)) { _status.PopupsGone++; ShapeFallback(); return; } // a list that closed: not a failed read
                        Log.Warn($"window registration failed for '{title}': {ex.Message} (attempt {attempt}, giving up)");
                        return;
                    }
                    Log.Info($"window registration of '{title}' failed: {ex.Message}; trying again (attempt {attempt + 1} of 3)");
                    Thread.Sleep(attempt == 1 ? 2000 : 6000);
                }
            }
        });
    }

    private sealed record ShapeLookup(string Name, string Aid, string Ct, string Typed, string Screen);

    /// <summary>The field typed in and the typed text when an untitled popup of a lookup's shape appears under it; null otherwise.</summary>
    private ShapeLookup? ShapeLookupCandidate(IntPtr popup)
    {
        if (!Win32.GetWindowRect(popup, out var pr)) return null;
        var popupPos = $"{pr.Left},{pr.Top} {pr.Right - pr.Left}x{pr.Bottom - pr.Top}";
        lock (_gate)
        {
            var since = _typingAt == DateTime.MinValue ? double.MaxValue : (DateTime.Now - _typingAt).TotalSeconds;
            // the field: the window with the keyboard focus (on time) else the last focused field
            var fieldPos = "";
            if (_focusWindows.Count > 0 && Win32.GetWindowRect(_focusWindows[^1].hwnd, out var fr) && fr.Bottom - fr.Top is > 0 and < 60) fieldPos = $"{fr.Left},{fr.Top} {fr.Right - fr.Left}x{fr.Bottom - fr.Top}";
            else if (_lastField.rect.Width > 0) fieldPos = $"{_lastField.rect.Left},{_lastField.rect.Top} {_lastField.rect.Width}x{_lastField.rect.Height}";
            if (fieldPos.Length == 0 || !FieldReads.LookupByShape(fieldPos, popupPos, since)) return null;
            var typed = _boxText.text is { Length: > 0 } bt && (DateTime.Now - _boxText.t).TotalSeconds <= 3 ? bt : _lastTyped.GetValueOrDefault(_lastField.aid.Length > 0 ? _lastField.aid : _lastField.name, "");
            return new ShapeLookup(_lastField.name, _lastField.aid, _lastField.ct.Length > 0 ? _lastField.ct : "Edit", typed, CurrentTitle);
        }
    }

    private (ShapeLookup? sl, IntPtr hwnd, DateTime t) _pendingShape = (null, IntPtr.Zero, DateTime.MinValue);

    /// <summary>
    /// The program completed a partial code the user typed ("100" became "1000136890" after a list was shown under the field and
    /// its first row taken): that is a lookup. Emitted once per field within 10 s, with the typed partial.
    /// </summary>
    private void LookupCompleted(string name, string aid, string ct, string partial, string screen)
    {
        IntPtr popup; lock (_gate) { popup = _pendingShape.sl is not null && (DateTime.Now - _pendingShape.t).TotalSeconds <= 10 ? _pendingShape.hwnd : IntPtr.Zero; _pendingShape = (null, IntPtr.Zero, DateTime.MinValue); }
        EmitShapeLookup(new ShapeLookup(name, aid, ct, partial, screen), popup);
    }

    private void EmitShapeLookup(ShapeLookup sl, IntPtr hwnd)
    {
        lock (_gate)
        {
            var fieldKey = sl.Aid + "\u0001" + sl.Name + "\u0001shape";
            if (_lastLookup.key.StartsWith(sl.Aid + "\u0001" + sl.Name + "\u0001", StringComparison.Ordinal) && (DateTime.Now - _lastLookup.t).TotalSeconds < 10) return;
            _lastLookup = (fieldKey, DateTime.Now);
        }
        _status.Dropdowns++; _status.LookupsByShape++;
        var pwd = Masking.IsSensitiveName(sl.Name) || Masking.IsSensitiveName(sl.Aid);
        Log.Info($"lookup under '{sl.Name}{sl.Aid}': the program completed the partial code typed there");
        Emit(new RawEvent { Act = Acts.Dropdown, Ct = sl.Ct, Name = sl.Name, Aid = sl.Aid, Val = pwd ? Masking.Masked : sl.Typed, Hwnd = hwnd, WindowTitle = sl.Screen, Items = new List<string>() });
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
        // a lookup opens under or beside the field typed in (within 40 px), and is a list that was not on screen before:
        // the same list shown again while one is still open is not a new lookup
        var popupPos = rec.Controls.Count > 0 ? rec.Controls[0].Position : "";
        if (field.rect.Width > 0 && popupPos.Length > 0 && !FieldReads.PopupServesField($"{field.rect.Left},{field.rect.Top} {field.rect.Width}x{field.rect.Height}", popupPos))
        {
            Log.Info($"list window at {popupPos} is not under or beside '{field.name}{field.aid}' ({field.rect}): not a lookup");
            return false;
        }
        if (!FieldReads.LookupMatchesTyped(typed, items)) { Log.Info($"list window under '{field.name}{field.aid}' has no row with what was typed: not a lookup"); return false; }
        var header = items[0];
        lock (_gate)
        {
            foreach (var stale in _lookupPopups.Keys.Where(h => !Win32.IsWindow(h)).ToList()) _lookupPopups.Remove(stale);
            var open = _lookupPopups.Any(kv => kv.Key != hwnd && kv.Value == header && Win32.IsWindowVisible(kv.Key));
            _lookupPopups[hwnd] = header;
            if (open) return false;
            // the same list for the same field again within 10 s (it reappears while the next cell is typed in): not a new lookup
            var fieldKey = field.aid + "" + field.name + "" + header;
            if (_lastLookup.key == fieldKey && (DateTime.Now - _lastLookup.t).TotalSeconds < 10) { _lastLookup = (fieldKey, DateTime.Now); return false; }
            _lastLookup = (fieldKey, DateTime.Now);
        }
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
            if (_mdiClients.ContainsKey(hwnd)) return; // an MDI workspace: its documents are read from the MDI client, never from a Tab control
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
                    // the workspace strip is a Tab control of the window itself, never one inside a document (Main | General | Credit)
                    // (nor a ribbon's tab strip, Home | Insert | View: those pages are toolbars, not documents)
                    strip = win.FindAllDescendants(_automation.ConditionFactory.ByControlType(ControlType.Tab)).FirstOrDefault(t => !IsInsideDocument(t, win) && !IsRibbon(t));
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
                System.Drawing.Rectangle own = default; try { own = c.Properties.BoundingRectangle.ValueOrDefault; } catch { }
                try
                {
                    foreach (var child in c.CachedChildren)
                    {
                        string cct; try { cct = child.Properties.ControlType.ValueOrDefault.ToString(); } catch { continue; }
                        if (cct == "Button") continue; // a tab's own close button is always on screen
                        bool off = true; try { off = child.Properties.IsOffscreen.ValueOrDefault; } catch { }
                        var r = child.Properties.BoundingRectangle.ValueOrDefault;
                        // the tab's own header (a WPF tab item holds its label text): always on screen, says nothing about the page
                        if (own.Width > 0 && own.Contains(r)) continue;
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

    /// <summary>A Tab control that lies inside a named Window below the top (an MDI child) is a document's inner tab control, not the workspace strip.</summary>
    private bool IsInsideDocument(AutomationElement tab, AutomationElement top)
    {
        try
        {
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            var p = walker.GetParent(tab);
            for (var up = 0; up < 12 && p is not null; up++)
            {
                if (p.Equals(top)) return false;
                string ct = "", name = "";
                try { ct = p.Properties.ControlType.ValueOrDefault.ToString(); name = (p.Properties.Name.ValueOrDefault ?? "").Trim(); } catch { }
                if (ct == "Window" && name.Length > 0) return true;
                p = walker.GetParent(p);
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// A text editor that offers no value of its own (a code or rich-text editor: only the Text pattern) holds what was typed
    /// in its text: the one with the keyboard focus is read that way (one read, at most 2000 characters) and its row gets the
    /// text as its value, so the read after the keys compares it like any field.
    /// </summary>
    /// <returns>The place of the field that has the keyboard focus (it is the field typed in when keys went down), or null.</returns>
    private string? ReadFocusedEditorText(List<ControlRow> rows)
    {
        try
        {
            var fe = _automation.FocusedElement();
            if (fe is null || !IsProgramPid(fe.Properties.ProcessId.ValueOrDefault)) return null;
            var rc = fe.BoundingRectangle; var pos = $"{rc.Left},{rc.Top} {rc.Width}x{rc.Height}";
            var ct = fe.Properties.ControlType.ValueOrDefault;
            if (ct is not (ControlType.Edit or ControlType.Document or ControlType.ComboBox or ControlType.Spinner or ControlType.DataItem)) return null;
            // (no value of its own, or an empty one beside a text: PowerShell ISE's editor reports "" as its value)
            if (ct is ControlType.Edit or ControlType.Document && (!fe.Patterns.Value.IsSupported || string.IsNullOrEmpty(fe.Patterns.Value.Pattern.Value.ValueOrDefault)))
            {
                var text = fe.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(2000);
                var row = string.IsNullOrEmpty(text) ? null : rows.FirstOrDefault(r => r.Position == pos && r.Type is "Edit" or "Document" && string.IsNullOrEmpty(r.Value));
                if (row is not null) row.Value = text!.TrimEnd('\r', '\n');
            }
            return pos;
        }
        catch { return null; }
    }

    /// <summary>A ribbon's tab strip (Office, DevExpress, Infragistics ribbons): the strip or a parent within five levels is named or classed "Ribbon".</summary>
    private bool IsRibbon(AutomationElement tab)
    {
        try
        {
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            AutomationElement? p = tab;
            for (var up = 0; up < 6 && p is not null; up++)
            {
                string name = "", cls = "";
                try { name = p.Properties.Name.ValueOrDefault ?? ""; cls = p.Properties.ClassName.ValueOrDefault ?? ""; } catch { }
                if (TabDocuments.IsRibbonPart(name, cls)) return true;
                p = walker.GetParent(p);
            }
        }
        catch { }
        return false;
    }

    /// <summary>The active document closed (its tab item is gone, nothing is selected, or its window was destroyed): the window is plain again.</summary>
    private void TabGone(IntPtr hwnd, string tab)
    {
        string windowTitle;
        lock (_gate)
        {
            _activeTab.Remove(hwnd); windowTitle = _knownWindows.TryGetValue(hwnd, out var t) ? t : _currentTitle; _closedTabs[tab] = DateTime.Now;
            foreach (var k in _closedTabs.Where(kv => (DateTime.Now - kv.Value).TotalMinutes > 1).Select(kv => kv.Key).ToList()) _closedTabs.Remove(k);
            foreach (var k in _slowReads.Where(kv => DateTime.Now > kv.Value).Select(kv => kv.Key).ToList()) _slowReads.Remove(k);
        }
        var screenTitle = TabDocuments.Title(windowTitle, tab);
        lock (_gate) { var rk = "win:" + hwnd + "|" + tab; _fieldReads.Remove(rk); _fieldRows.Remove(rk); _fieldReadAt.Remove(rk); _innerTabs.Remove(screenTitle); }
        InferPress("window closed", from: screenTitle); // Exit on the document is counted before its close
        Emit(new RawEvent { Act = Acts.Close, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = hwnd });
    }

    /// <summary>A tab document was selected: events now belong to "Window › Tab", and the tab's content is a screen of its own.</summary>
    private void ApplyTab(IntPtr top, string tabName, AutomationElement? tabElement, bool viaEvent, IntPtr documentHwnd = default)
    {
        if (top == IntPtr.Zero) top = _foreground;
        var applyAt = DateTime.Now;
        // a document read again while it is being destroyed (its Exit was pressed a moment ago) is not opened again
        lock (_gate) { if (_closedTabs.TryGetValue(tabName, out var gone) && (DateTime.Now - gone).TotalSeconds < 2 && (documentHwnd == IntPtr.Zero || !Win32.IsWindow(documentHwnd) || !Win32.IsWindowVisible(documentHwnd) || _tracker.PressedWithin(2000, DateTime.Now))) return; }
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
        InferPress(documentHwnd != IntPtr.Zero ? "window opened" : "tab changed", from: windowTitle);
        bool firstTime;
        lock (_dumpedTitles) firstTime = !_dumpedTitles.Contains(Core.Learning.Titles.Key(screenTitle));
        // the document closed while this was being applied (a read during its destruction): it was not opened
        bool ClosedSince() { lock (_gate) return _closedTabs.TryGetValue(tabName, out var c) && c >= applyAt || documentHwnd != IntPtr.Zero && !Win32.IsWindow(documentHwnd); }
        if (ClosedSince()) return;
        if (!firstTime)
        {
            Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top });
            ScheduleReread("tab " + tabName);
            bool known; lock (_gate) known = documentHwnd == IntPtr.Zero || _rows.ContainsKey(documentHwnd);
            if (known) return;
            // a document seen before under this title, in a window of its own: its own tree is read for the clicks in it (Product
            // Master and Product Analysis share a title and differ), and kept as a second record when its layout differs
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(400);
                    var rec = DumpDocument(top, tabName, tabElement, screenTitle, documentHwnd);
                    rec.Kind = ScreenKinds.Tab;
                    rec.ReachedFrom = windowTitle;
                    if (rec.Controls.Count < 5) return;
                    lock (_gate) _rows[documentHwnd] = rec.Controls;
                    Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top, Screen = rec, ScreenOnly = true });
                }
                catch (Exception ex) { Log.Warn($"document dump failed for '{screenTitle}': {ex.Message}"); }
            });
            return;
        }
        // the document is open now: its open is written at once; its tree (a large grid can take Inform many seconds to give)
        // follows as a record of its own, so a visit is never dated from when the reading finished
        Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(400);
                var rec = DumpDocument(top, tabName, tabElement, screenTitle, documentHwnd);
                rec.Kind = ScreenKinds.Tab;
                rec.ReachedFrom = windowTitle;
                if (documentHwnd != IntPtr.Zero && rec.Controls.Count >= 5) lock (_gate) _rows[documentHwnd] = rec.Controls;
                lock (_dumpedTitles) _dumpedTitles.Add(Core.Learning.Titles.Key(screenTitle));
                if (rec.Controls.Count >= 5) { lock (_gate) _mdiTitlesDumped.Add(Core.Learning.Titles.Key(tabName)); UpdateMdiStatus(); }
                _status.TabScreens++;
                Emit(new RawEvent { Act = Acts.Open, Ct = "Window", Name = screenTitle, WindowTitle = screenTitle, Hwnd = top, Screen = rec, ScreenOnly = true });
                ScheduleReread("tab " + tabName);
            }
            catch (Exception ex) { Log.Warn($"tab dump failed for '{screenTitle}': {ex.Message}"); }
        });
    }

    /// <summary>
    /// The selected document's own subtree: the MDI child window itself (read from its own handle, with every fallback the
    /// window dump has), else the child named like the tab or the on-screen sibling of the tab strip, never the window's
    /// toolbars, menu bar, status bar or the other documents. Falls back to the tab item's subtree (WinForms puts the page under the tab item).
    /// </summary>
    private ScreenRecord DumpDocument(IntPtr top, string tabName, AutomationElement? tabElement, string screenTitle, IntPtr documentHwnd = default)
    {
        if (documentHwnd != IntPtr.Zero)
        {
            try
            {
                var child = _automation.FromHandle(documentHwnd);
                var own = DumpTree(child, documentHwnd, screenTitle);
                if (own.Controls.Count >= 5) { own.Title = screenTitle; return own; }
                Log.Info($"document '{tabName}' read from its window gave {own.Controls.Count} controls; trying the whole window");
            }
            catch (Exception ex) { Log.Info($"document '{tabName}' not read from its window: {ex.Message}"); }
        }
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
    private void ScheduleReread(string why) => ScheduleReread(why, IntPtr.Zero);

    /// <summary>Same, for the window a click landed in (<paramref name="surface"/>: a document or a top-level window of the program), when it is not the one in front.</summary>
    private void ScheduleReread(string why, IntPtr surface)
    {
        if (!_inFront || _foreground == IntPtr.Zero) return;
        // where cells report themselves and a read of the window is slow (Excel), typing never asks for one: only a click,
        // a press, a tab or a window does (a read holds the program's answers back for seconds)
        if (!_settings.ReadAfterKeys && (why.StartsWith("key", StringComparison.Ordinal) || why.StartsWith("type", StringComparison.Ordinal)
            || why.StartsWith("focus", StringComparison.Ordinal) || why.StartsWith("cell", StringComparison.Ordinal) || why.StartsWith("value", StringComparison.Ordinal))) return;
        lock (_gate) { _rereadWhy = why; _rereadSurface = surface; if (why.StartsWith("click", StringComparison.Ordinal) || why.StartsWith("key", StringComparison.Ordinal)) _rereadAsked = true; }
        _rereadTimer ??= new System.Threading.Timer(_ => Reread(), null, Timeout.Infinite, Timeout.Infinite);
        _rereadTimer.Change(300, Timeout.Infinite);
        // a typed value or a key in a grid: the program prices the line a second or more later, so the row is read again at 1.5 s
        if (why.StartsWith("type", StringComparison.Ordinal) || why.StartsWith("key", StringComparison.Ordinal) || why.StartsWith("focus", StringComparison.Ordinal))
        {
            _rereadAgainTimer ??= new System.Threading.Timer(_ => Reread(), null, Timeout.Infinite, Timeout.Infinite);
            _rereadAgainTimer.Change(1500, Timeout.Infinite);
        }
    }

    private void Reread()
    {
        if (Interlocked.Exchange(ref _rereadBusy, 1) == 1) { _rereadTimer?.Change(300, Timeout.Infinite); return; }
        try
        {
            if (!_inFront || _foreground == IntPtr.Zero) return;
            // the document's own title, from the window it is read from (never the title of the moment, which a popup can flip);
            // the read is kept per window handle, so a document that takes its title keeps its baseline
            var (readKey, screen, hwnd, tab, docHwnd) = ReadTarget(forClick: true);
            if (screen.Length == 0) return;
            var batchT = DateTime.Now;
            // the grid row typed in, as it was when this read began (a Tab during a slow read moves it to the next column)
            TypedRow? typedAtStart; lock (_gate) typedAtStart = _typedRow;
            lock (_gate)
            {
                if (_slowReads.TryGetValue(readKey, out var until) && DateTime.Now < until && !_rereadAsked) return;
                _rereadAsked = false; // this read answers the click or key that asked for it
            }
            var rows = new List<ControlRow>();
            using (Cache(withFilter: true, withValues: true).Activate())
            {
                // an MDI document is read from its own window (smaller and faster than the whole workspace)
                var cached = _automation.FromHandle(docHwnd != IntPtr.Zero && Win32.IsWindow(docHwnd) ? docHwnd : hwnd);
                WalkCached(cached, 0, _settings.DumpDepth, rows, 1500, 12, onlyOnScreen: true, withValues: true, selectionReads: _settings.SelectionReads);
            }
            var took = (DateTime.Now - batchT).TotalSeconds;
            if (took > 2) { Log.Info($"document read of {screen} took {took:0.0} s ({rows.Count} rows): the next reads wait 15 s unless a click or key asks"); lock (_gate) _slowReads[readKey] = DateTime.Now.AddSeconds(15); }
            if (tab is not null && docHwnd == IntPtr.Zero)
            {
                var doc = TabDocuments.Document(rows, tab);
                if (doc.Count >= 5) rows = doc;
            }
            // inner tabs (Main | General | Credit): the page that says selected now, against the last read
            var inner = TabDocuments.InnerTabSelections(rows);
            List<string> innerChanges; bool firstInner; List<ControlRow>? recordedAtOpen = null; var baselineFromRecord = false;
            lock (_gate)
            {
                if (_rows.TryGetValue(docHwnd != IntPtr.Zero ? docHwnd : hwnd, out var rec0)) recordedAtOpen = rec0;
                firstInner = !_innerTabs.TryGetValue(screen, out var prevInner);
                // never read since it was recorded: the tree recorded when it opened is the page it showed before the first click
                if (firstInner && _rows.TryGetValue(docHwnd != IntPtr.Zero ? docHwnd : hwnd, out var recorded) && recorded.Count > 0)
                {
                    var atOpen = TabDocuments.InnerTabSelections(recorded);
                    // only the strips recorded then can have changed; a strip not in that tree keeps the page it shows now
                    if (atOpen.Keys.Any(inner.ContainsKey)) { prevInner = inner.ToDictionary(kv => kv.Key, kv => atOpen.GetValueOrDefault(kv.Key, kv.Value)); firstInner = false; }
                }
                innerChanges = firstInner ? new List<string>() : TabDocuments.InnerTabChanges(prevInner, inner);
                _innerTabs[screen] = inner;
            }
            // the newest read of a window is its click surface too (a document whose first tree read failed has one now;
            // only what is on screen is in it, so a page hidden behind another tab is never hit)
            // (kept apart from the recorded trees: labels are still resolved on the tree recorded when the window opened)
            if (rows.Count >= 5 && (docHwnd != IntPtr.Zero || tab is null)) lock (_gate) _clickRows[docHwnd != IntPtr.Zero ? docHwnd : hwnd] = rows;
            foreach (var page in innerChanges)
            {
                bool fromClick; lock (_gate) fromClick = _recentPageSelects.TryGetValue(screen + "\u0001" + page, out var at) && (DateTime.Now - at).TotalSeconds <= 5;
                if (fromClick) continue;
                _status.InnerTabSelections++;
                Emit(new RawEvent { Act = Acts.Select, Ct = "TabItem", Name = page, Val = page, How = "read", WindowTitle = screen, Hwnd = hwnd });
            }
            if (innerChanges.Count > 0) InferPress("tab changed", from: screen);
            var focusedPos = ReadFocusedEditorText(rows);
            var reads = FieldReads.FromRows(rows);
            List<FieldRead>? before; List<ControlRow>? beforeRows; HashSet<string> userKeys; (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) field, previous; string typed; string why;
            List<(IntPtr hwnd, DateTime t)> focusWindows;
            lock (_gate)
            {
                _fieldReads.TryGetValue(readKey, out before); _fieldRows.TryGetValue(readKey, out beforeRows);
                if (before is null && docHwnd != IntPtr.Zero)
                {
                    // the document was read under its window before it had a title of its own: that read is the baseline
                    var prefix = "win:" + hwnd + "|";
                    var k = _fieldReadAt.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && (DateTime.Now - kv.Value).TotalSeconds <= 30).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
                    if (k is not null) { before = _fieldReads[k]; beforeRows = _fieldRows[k]; }
                }
                // never read since it opened: the tree recorded when it opened is the baseline (the user may have typed into it
                // before the first read: Price Change and Projected PO lost their codes that way)
                // (that tree holds no values: only what the user typed is taken from this first comparison, never a fill)
                if (before is null && recordedAtOpen is { Count: >= 5 } && !ReferenceEquals(recordedAtOpen, rows)) { before = FieldReads.FromRows(recordedAtOpen); beforeRows = null; baselineFromRecord = true; }
                focusWindows = _focusWindows.Where(f => (DateTime.Now - f.t).TotalSeconds <= 8).ToList();
                foreach (var stale in _userKeysSinceRead.Where(k => (DateTime.Now - k.Value).TotalSeconds > 5).Select(k => k.Key).ToList()) _userKeysSinceRead.Remove(stale);
                userKeys = new HashSet<string>(_userKeysSinceRead.Keys, StringComparer.Ordinal);
                field = _lastField; previous = _previousField; typed = _lastTyped.GetValueOrDefault(field.aid.Length > 0 ? field.aid : field.name, ""); why = _rereadWhy;
                _fieldReads[readKey] = reads; _fieldRows[readKey] = rows; _fieldReadAt[readKey] = DateTime.Now;
            }
            InPageDialogs(readKey, rows, screen, hwnd);
            // the field that has the focus and the one that just had it: what changed there was typed by the user
            var typedKeys = new HashSet<string>(StringComparer.Ordinal);
            void AddKeys(ISet<string> set, (string name, string aid, string ct, DateTime t, System.Drawing.Rectangle rect) f)
            {
                if (f.name.Length > 0) set.Add(f.name);
                if (f.aid.Length > 0 && !Labels.IsWrapperId(f.aid)) { set.Add(f.aid); set.Add(Labels.Clean(f.aid)); }
            }
            AddKeys(typedKeys, field);
            if ((DateTime.Now - previous.t).TotalSeconds <= 8) AddKeys(typedKeys, previous);
            // a cell with no name of its own: the field under the editor that has, or just had, the focus, by place
            if (field.rect.Width > 0) { var at = FieldReads.AtRect(reads, $"{field.rect.Left},{field.rect.Top} {field.rect.Width}x{field.rect.Height}"); if (at is not null) typedKeys.Add(at.Label); }
            if (previous.rect.Width > 0 && (DateTime.Now - previous.t).TotalSeconds <= 8) { var at = FieldReads.AtRect(reads, $"{previous.rect.Left},{previous.rect.Top} {previous.rect.Width}x{previous.rect.Height}"); if (at is not null) typedKeys.Add(at.Label); }
            // the windows that had the keyboard focus (a read of the GUI thread, independent of focus events that can be missed)
            foreach (var aid in FocusAids(focusWindows)) typedKeys.Add(aid);
            // the field that has the keyboard focus at this read, when keys went down lately (a new editor that took the focus
            // with no focus event: PowerShell ISE's new script tab)
            DateTime typingNow; lock (_gate) typingNow = _typingAt;
            if (focusedPos is not null && (DateTime.Now - typingNow).TotalSeconds is >= 0 and <= 8 && FieldReads.AtRect(reads, focusedPos) is { IsCell: false } focusedRead) typedKeys.Add(focusedRead.Label);
            // (never a grid cell: a new line's default quantity sits in the focused cell, and the row read decides cells)
            foreach (var k in typedKeys) userKeys.Add(k);
            if (before is null) return; // the first read of a document is the baseline
            var (typedNow, filled) = FieldReads.Diff(before, reads, typedKeys);
            if (baselineFromRecord) { filled.Clear(); typedNow.RemoveAll(f => f.IsCell); }
            // three or more fields outside a grid changed at once: a record loaded; a field among them is the user's only when
            // the focus was seen arriving in it and a key went down after that
            var recordLoad = filled.Count(f => !f.IsCell) >= 3;
            // the grid row the user typed in: read by its own cells (or by place when the focus moved to another line), and its
            // changes kept apart from the rest so a new empty line never stands in for the line being priced
            var rowDone = TypedRowChanges(readKey, screen, hwnd, reads, rows, batchT, typedAtStart);
            if (rowDone.row is not null)
            {
                typedNow.RemoveAll(f => f.IsCell && f.Row == rowDone.row);
                filled.RemoveAll(f => f.IsCell && f.Row == rowDone.row);
            }
            foreach (var f in typedNow)
            {
                // the field's own event already reported this value: nothing new
                string? already; lock (_gate) already = _lastTyped.GetValueOrDefault(f.AutomationId.Length > 0 ? f.AutomationId : f.Label);
                if (already == f.Value) continue;
                // the field's own event reported what the keys put there a moment ago, and the text now is something else (Inform
                // puts the record's name where its code was typed): the program replaced it; the keys stay the typed value
                DateTime ownAt; lock (_gate) ownAt = _ownTypedAt.GetValueOrDefault(f.AutomationId.Length > 0 ? f.AutomationId : f.Label);
                int keysTyped; lock (_gate) keysTyped = _keysSinceFocus;
                (int x, int y, DateTime t) lastClick; DateTime lastKey; lock (_gate) { lastClick = _tracker.LastClick; lastKey = _typingAt; }
                if (FieldReads.IsReplacement(already, f.Value, (batchT - ownAt).TotalSeconds) || !UserChanged(f, batchT, strict: recordLoad)
                    || FieldReads.ChangedAfterClick(already, ownAt, lastKey, lastClick, f.Position, f.Value, batchT)
                    || recordLoad && !f.IsCell && FieldReads.LongerThanKeys(f.Value, keysTyped))
                {
                    lock (_gate) _lastTyped[f.AutomationId.Length > 0 ? f.AutomationId : f.Label] = f.Value;
                    _status.ValuesFilled++; _status.ValuesReread++;
                    Emit(new RawEvent { T = batchT, Act = Acts.Value, Ct = f.IsCell ? "DataItem" : f.Type, Name = f.Label, Aid = f.IsCell ? "" : f.AutomationId, Val = Masking.ForAppValue(f.Value, f.Label, f.AutomationId, f.IsPassword, f.Type), By = ValueBy.App, WindowTitle = screen, Hwnd = hwnd, Row = f.IsCell ? f.Row : null });
                    continue;
                }
                lock (_gate) { _lastTyped[f.AutomationId.Length > 0 ? f.AutomationId : f.Label] = f.Value; _userKeysSinceRead[f.Label] = DateTime.Now; }
                _status.ValuesTypedReread++;
                Emit(new RawEvent { T = batchT, Act = Acts.Value, Ct = f.IsCell ? "DataItem" : f.Type, Name = f.Label, Aid = f.IsCell ? "" : f.AutomationId, Val = Masking.ForEvent(f.Value, f.Label, f.AutomationId, f.IsPassword), How = "read", WindowTitle = screen, Hwnd = hwnd, Row = f.IsCell ? f.Row : null });
            }
            var anyFill = rowDone.filled;
            foreach (var f in filled)
            {
                if (f.IsPassword || Masking.IsSensitiveName(f.Label) || Masking.IsSensitiveName(f.AutomationId)) continue;
                if (userKeys.Contains(f.Label) && !typedKeys.Contains(f.Label)) continue; // typed a moment ago by the field's own event
                _status.ValuesFilled++; _status.ValuesReread++; anyFill = true;
                Emit(new RawEvent { T = batchT, Act = Acts.Value, Ct = f.IsCell ? "DataItem" : f.Type, Name = f.Label, Aid = f.IsCell ? "" : f.AutomationId, Val = Masking.ForAppValue(f.Value, f.Label, f.AutomationId, false, f.Type), By = ValueBy.App, WindowTitle = screen, Hwnd = hwnd, Row = f.IsCell ? f.Row : null });
            }
            // a change the click explains (a new row, a changed cell): confirmed against the time the read began, 300 ms after the click
            if (anyFill) InferPress("value filled", from: screen, at: batchT);
            // a lookup list that opened inside the window under the field being typed in
            if (beforeRows is not null && field.name.Length + field.aid.Length > 0 && (DateTime.Now - field.t).TotalSeconds <= 8 && field.rect.Width > 0)
            {
                var pos = $"{field.rect.Left},{field.rect.Top} {field.rect.Width}x{field.rect.Height}";
                var lookup = FieldReads.NewLookup(beforeRows, rows, pos);
                if (lookup is { } lk && FieldReads.LookupMatchesTyped(typed, lk.lines))
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

    /// <summary>
    /// A changed field the user was in is the user's only when the user did something to it: a character key went down after the
    /// focus arrived in it (or within 3 s when its arrival was not seen), or, for a drop-down list or a tick box, a click landed on
    /// it within 3 s. A page that shows its fields after a tab click, a program that moves the focus to the next field and fills
    /// it, a combo it sets when a record loads: all the program's (v0.11 and r1 recorded them as typed).
    /// </summary>
    private bool UserChanged(FieldRead f, DateTime at, bool strict = false)
    {
        DateTime typing, arrival = DateTime.MinValue; (int x, int y, DateTime t) click;
        lock (_gate)
        {
            typing = _typingAt; click = _tracker.LastClick;
            var mine = new[] { f.AutomationId, Labels.Clean(f.AutomationId), f.Label };
            foreach (var k in mine)
                if (k.Length > 0 && _focusArrivedAt.TryGetValue(k, out var a) && a > arrival) arrival = a;
            arrival = StillHere(arrival, mine);
            // the focus's arrival by the window handle whose automation id is this field's (sampled, never late)
            if (f.AutomationId.Length > 0)
                foreach (var (h, aid) in _focusAids)
                    if ((aid == f.AutomationId || aid.Length > 0 && Labels.Clean(aid) == Labels.Clean(f.AutomationId)) && _focusSince.TryGetValue(h, out var since)) { arrival = since; break; }
        }
        // an arrival long ago (the same field name in an earlier document) says nothing about this change
        if (arrival != DateTime.MinValue && (at - arrival).TotalSeconds > 30) arrival = DateTime.MinValue;
        if (typing == DateTime.MinValue && !strict) return true; // no key ever seen: the key test cannot be made (see KeyedInto)
        // a field without a window of its own, changed just after Tab ended the keys, its arrival not seen: the program filled it
        // as the focus moved in (see KeyedInto)
        if (!strict && arrival == DateTime.MinValue && !f.IsCell)
        {
            bool ownWindow; DateTime tabAt;
            lock (_gate) { ownWindow = f.AutomationId.Length > 0 && _focusAids.ContainsValue(f.AutomationId); tabAt = _tabAt; }
            // (the field has the focus now: by automation id, or by place when it has none; Qt fields have no ids, and an empty id
            // matched every focused field)
            if (!ownWindow && FieldReads.FilledOnTabArrival(typing, tabAt, at) && HasFocusNow(f)) return false;
        }
        if (strict && arrival == DateTime.MinValue && f.Type is not ("ComboBox" or "CheckBox" or "RadioButton")) return false;
        if (strict && f.Type is "ComboBox" or "CheckBox" or "RadioButton") typing = DateTime.MinValue; // a record load: only a click on it makes it the user's
        return FieldReads.UserChanged(f.Type, f.Position, at, typing, arrival, click);
    }

    /// <summary>Where the next document read comes from: its cache key (by window handle), its screen title, the window, the tab, the MDI document.</summary>
    private (string key, string screen, IntPtr hwnd, string? tab, IntPtr docHwnd) ReadTarget(bool forClick = false)
    {
        var hwnd = _foreground;
        string? tab; IntPtr docHwnd;
        lock (_gate)
        {
            // a click in a window other than the one in front: that window (or document) is read
            var s = forClick ? _rereadSurface : IntPtr.Zero;
            if (forClick) _rereadSurface = IntPtr.Zero;
            if (s != IntPtr.Zero && Win32.IsWindow(s) && s != hwnd && s != _mdiActiveChild.GetValueOrDefault(hwnd))
            {
                if (_mdiChildren.TryGetValue(s, out var top))
                {
                    var screenOf = ScreenTitleOf(s);
                    return ("doc:" + s, screenOf, top, _activeTab.GetValueOrDefault(top), s);
                }
                if (_knownWindows.TryGetValue(s, out var title) && title.Length > 0 && !Excluded(title))
                {
                    var d = _mdiActiveChild.GetValueOrDefault(s);
                    if (d != IntPtr.Zero) return ("doc:" + d, ScreenTitleOf(d), s, _activeTab.GetValueOrDefault(s), d);
                    var t = _activeTab.GetValueOrDefault(s);
                    return ("win:" + s + "|" + (t ?? ""), t is not null && _settings.TabDocuments ? TabDocuments.Title(title, t) : title, s, t, IntPtr.Zero);
                }
            }
            tab = _activeTab.GetValueOrDefault(hwnd); docHwnd = _mdiActiveChild.GetValueOrDefault(hwnd);
        }
        var screen = docHwnd != IntPtr.Zero ? ScreenTitleOf(docHwnd) : CurrentTitle;
        var key = docHwnd != IntPtr.Zero ? "doc:" + docHwnd : "win:" + hwnd + "|" + (tab ?? "");
        return (key, screen, hwnd, tab, docHwnd);
    }

    /// <summary>The automation ids of windows that had the keyboard focus (one UIA read per handle, cached).</summary>
    private List<string> FocusAids(List<(IntPtr hwnd, DateTime t)> windows)
    {
        var list = new List<string>();
        foreach (var (h, _) in windows)
        {
            string? aid;
            lock (_gate) _focusAids.TryGetValue(h, out aid);
            if (aid is null)
            {
                try { aid = _automation.FromHandle(h).Properties.AutomationId.ValueOrDefault ?? ""; } catch { aid = ""; }
                lock (_gate) { if (_focusAids.Count > 200) _focusAids.Clear(); _focusAids[h] = aid; }
            }
            if (aid.Length > 0 && !Labels.IsWrapperId(aid) && !aid.All(char.IsDigit)) { list.Add(aid); list.Add(Labels.Clean(aid)); }
        }
        return list;
    }

    /// <summary>Enter or Tab: the cell that had the focus when the key went down is the one typed in; its row is read again by place.</summary>
    private void NoteTypedRow(DateTime now)
    {
        var (readKey, _, _, _, _) = ReadTarget();
        // the editor that has the keyboard focus now, by the program's GUI thread (on time even when the UI Automation focus
        // event comes seconds late): its rectangle names the cell typed in
        System.Drawing.Rectangle guiRect = default;
        lock (_gate)
        {
            // the text box the character keys went into (Enter may already have moved the focus to the next cell)
            var typedBox = _boxText.hwnd != IntPtr.Zero && (now - _boxText.t).TotalSeconds is >= 0 and <= 3 ? _boxText.hwnd : IntPtr.Zero;
            if (typedBox != IntPtr.Zero && _boxPlace.Width > 0) guiRect = _boxPlace; // where the keys went, not where the editor moved to
            else
            {
                var h = typedBox != IntPtr.Zero ? typedBox : _focusWindows.Count > 0 ? _focusWindows[^1].hwnd : IntPtr.Zero;
                if (h != IntPtr.Zero && Win32.GetWindowRect(h, out var wr)) guiRect = new System.Drawing.Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            }
        }
        lock (_gate)
        {
            var f = _lastField;
            if (!_fieldReads.TryGetValue(readKey, out var reads)) return;
            var rect = guiRect.Width > 0 && guiRect.Height is > 0 and < 60 ? guiRect : f.rect;
            if (rect.Width <= 0 || rect == f.rect && (now - f.t).TotalSeconds > 30) return;
            var pos = $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}";
            var cell = FieldReads.AtRect(reads, pos);
            if (cell is null || !cell.IsCell || cell.Row.Length == 0) return;
            // a second key on the same row keeps the snapshot of the first (the row's values before the user's change)
            if (_typedRow is { } tr && tr.ReadKey == readKey && tr.Row == cell.Row && (now - tr.T).TotalSeconds <= 5) { _typedRow = tr with { T = now, Column = cell.Label }; return; }
            _typedRow = new TypedRow(readKey, cell.Row, cell.Label, pos, FieldReads.RowCells(reads, cell.Row), now);
        }
    }

    /// <summary>
    /// The typed row's changes since its snapshot: the typed column is the user's value, every other changed cell the program's.
    /// The row is taken from the full read when its cells are still where the typed cell was, else read on its own by place.
    /// Emits the values (with the row) and moves the snapshot on. Returns the row handled and whether the program filled a cell.
    /// </summary>
    private (string? row, bool filled) TypedRowChanges(string readKey, string screen, IntPtr hwnd, List<FieldRead> reads, List<ControlRow> rows, DateTime batchT, TypedRow? atStart)
    {
        var tr = atStart;
        if (tr is null || tr.ReadKey != readKey || (DateTime.Now - tr.T).TotalSeconds > 5) return (null, false);
        var now = FieldReads.RowCells(reads, tr.Row);
        if (!now.Any(c => SameBand(c.Position, tr.CellPosition))) now = ReadRowAt(tr.CellPosition, rows);
        if (now.Count == 0) return (null, false);
        var (typed, filledCells) = FieldReads.RowDiff(tr.Snapshot, now, tr.Column);
        // the typed cell now holds far more than the keys could write (a partial code that a lookup completed: "100" became
        // "1000136890"): the keys' text, read from the text box before Enter, is the user's; the completed value is the program's
        int keys; List<(IntPtr hwnd, string text, System.Drawing.Rectangle place, DateTime t)> hist; lock (_gate) { keys = _keysSinceFocus; hist = _boxHistory.ToList(); }
        // texts typed on this grid line in the last 10 s (a code typed in another field never counts)
        bool listSeen; lock (_gate) listSeen = _pendingShape.sl is not null && (DateTime.Now - _pendingShape.t).TotalSeconds <= 10;
        foreach (var f in typed.ToList())
        {
            // what the keys wrote in this very cell (the box typed in lay over it) in the last 10 s; nothing captured: unchanged
            var here = hist.Where(b => b.text.Trim().Length > 0 && (batchT - b.t).TotalSeconds is >= -1 and <= 10 && b.place.Width > 0
                                       && FieldReads.Overlaps($"{b.place.Left},{b.place.Top} {b.place.Width}x{b.place.Height}", f.Position)).OrderBy(b => b.t).ToList();
            if (here.Count == 0) continue;
            // a text read from the box's picture: look-alike characters (O and 0, I and 1) are settled by the cell's value
            lock (_gate) here = here.Select(b => _fromPicture.ContainsKey((b.place, b.text)) ? (b.hwnd, FieldReads.ReconcilePixelText(b.text, f.Value), b.place, b.t) : b).ToList();
            // keys went into this cell, and its value is none of what they wrote (nor that number formatted): the program's (its default)
            if (!here.Any(b => FieldReads.ShowsTyped(f.Value, b.text))) { typed.Remove(f); filledCells.Add(f); continue; }
            // the cell holds far more than the keys wrote (a partial code a lookup completed): the partial is the user's
            var box = here.LastOrDefault(b => f.Value.Trim().StartsWith(b.text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (box.text is null || !FieldReads.LongerThanKeys(f.Value, Math.Max(keys, box.text.Length))) continue;
            if (FieldReads.ShowsTyped(f.Value, box.text) && !f.Value.Trim().StartsWith(box.text.Trim(), StringComparison.OrdinalIgnoreCase)) continue; // a number formatted
            typed.Remove(f);
            filledCells.Add(f);
            typed.Add(new FieldRead { Label = f.Label, Type = f.Type, Value = box.text.Trim(), IsCell = true, Row = f.Row, Position = f.Position });
            if (listSeen) LookupCompleted(f.Label, "", "DataItem", box.text.Trim(), screen);
        }
        foreach (var f in typed)
        {
            lock (_gate) { _lastTyped[f.Label] = f.Value; _userKeysSinceRead[f.Label] = DateTime.Now; }
            _status.ValuesTypedReread++;
            Emit(new RawEvent { T = batchT, Act = Acts.Value, Ct = "DataItem", Name = f.Label, Val = Masking.ForEvent(f.Value, f.Label, "", f.IsPassword), How = "read", WindowTitle = screen, Hwnd = hwnd, Row = tr.Row });
        }
        foreach (var f in filledCells)
        {
            if (f.IsPassword || Masking.IsSensitiveName(f.Label)) continue;
            _status.ValuesFilled++; _status.ValuesReread++;
            Emit(new RawEvent { T = batchT, Act = Acts.Value, Ct = "DataItem", Name = f.Label, Val = Masking.ForAppValue(f.Value, f.Label, "", false, "DataItem"), By = ValueBy.App, WindowTitle = screen, Hwnd = hwnd, Row = tr.Row });
        }
        // the row's snapshot moves on to this read (also when the typed column changed while it was taken)
        // (a column typed into while this read ran keeps its cell's old snapshot, so the next read still finds it typed)
        lock (_gate)
        {
            if (_typedRow is { } cur && cur.ReadKey == tr.ReadKey && cur.Row == tr.Row)
            {
                var snap = cur.Column == tr.Column ? now : now.Select(c => c.Label == cur.Column ? (cur.Snapshot.FirstOrDefault(o => o.Label == c.Label) ?? c) : c).ToList();
                _typedRow = cur with { Snapshot = snap };
            }
        }
        if (typed.Count + filledCells.Count > 0) Log.Info($"typed row {tr.Row} of {screen}: {typed.Count} typed, {filledCells.Count} filled");
        return (tr.Row, filledCells.Count > 0);
    }

    /// <summary>Two rectangles on the same grid line (their vertical centres within 4 px).</summary>
    private static bool SameBand(string a, string b) =>
        Labels.TryRect(a, out _, out var ay, out _, out var ah) && Labels.TryRect(b, out _, out var by, out _, out var bh) && Math.Abs(ay + ah / 2 - (by + bh / 2)) <= 4;

    /// <summary>The grid row under a cell's rectangle, read on its own (ElementFromPoint, then up to the row, then its cells; read only).</summary>
    private List<FieldRead> ReadRowAt(string cellPosition, List<ControlRow> lastFullRead)
    {
        if (!Labels.TryRect(cellPosition, out var x, out var y, out var w, out var h)) return new();
        try
        {
            var sub = new List<ControlRow>();
            using (Cache(withFilter: true, withValues: true).Activate())
            {
                var el = _automation.FromPoint(new System.Drawing.Point(x + w / 2, y + h / 2));
                if (el is null || !IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return new();
                var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
                AutomationElement? row = el;
                for (var i = 0; i < 4 && row is not null; i++)
                {
                    if (row.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.DataItem) break;
                    row = walker.GetParent(row);
                }
                if (row is null || row.Properties.ControlType.ValueOrDefault != FlaUI.Core.Definitions.ControlType.DataItem) return new();
                WalkCached(row, 0, 3, sub, 120, 60, onlyOnScreen: false, withValues: true);
            }
            return FieldReads.RowFromSubtree(lastFullRead, cellPosition, sub);
        }
        catch (Exception ex) { Log.Info("row read skipped: " + ex.Message); return new(); }
    }

    // ---- UIA callbacks (thread pool) ----

    private void OnFocus(AutomationElement el)
    {
        try
        {
            if (!IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return;
            NoteInput();
            var r = Describe(el, Acts.Focus);
            if (r.Ct == "Window" || r.Ct == "Pane" && r.Name.Length == 0) return;
            // (a menu-bar item, "Tools", opens its menu: never the choice)
            if (r.Ct == "MenuItem") { if (!OnMenuBar(el)) lock (_gate) _lastMenuItem = (r.Name, DateTime.Now); }
            else if (r.Ct is "Button" or "SplitButton" or "Hyperlink") lock (_gate) _lastActionable = (r.Name, r.Aid, r.Ct, CurrentTitle, DateTime.Now);
            else if (r.Ct is "CheckBox" or "RadioButton") lock (_gate) _lastToggleFocus = (r.Name, DateTime.Now);
            else if (r.Ct == "TabItem") { PollTabs(_foreground, force: true); ScheduleReread("tab focus " + r.Name); } // a tab item took focus: the selection may have changed
            else if (r.Ct is "Edit" or "ComboBox" or "Spinner" or "Document" or "DataItem")
            {
                System.Drawing.Rectangle rect = default;
                try { rect = el.BoundingRectangle; } catch { }
                lock (_gate)
                {
                    if (_lastField.name != r.Name || _lastField.aid != r.Aid)
                    {
                        _previousField = _lastField; // the cell the user just left: what changed there was typed
                        if (_focusArrivedAt.Count > 500) _focusArrivedAt.Clear();
                        if (r.Aid.Length > 0) { _focusArrivedAt[r.Aid] = DateTime.Now; _focusArrivedAt[Labels.Clean(r.Aid)] = DateTime.Now; }
                        _lastActionable = ("", "", "", "", DateTime.MinValue); // the focus left the button for a field: Space here types
                        if (r.Name.Length > 0) _focusArrivedAt[r.Name] = DateTime.Now;
                    }
                    _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now, rect); _userKeysSinceRead[r.Name] = DateTime.Now; if (r.Aid.Length > 0 && !Labels.IsWrapperId(r.Aid)) _userKeysSinceRead[r.Aid] = DateTime.Now;
                    // a grid cell the user went into: until the next read, a change there is the user's (Excel commits a cell on
                    // Tab or Enter and reports it after the focus moved on, often a second late)
                    if (r.Ct == "DataItem")
                    {
                        if (_visitedCells.Count > 200) _visitedCells.Clear();
                        if (r.Name.Length > 0) _visitedCells[r.Name] = DateTime.Now;
                        if (r.Aid.Length > 0) _visitedCells[r.Aid] = DateTime.Now;
                    }
                }
                ScheduleReread("focus " + r.Name); // a grid cell typed into reports no value change: the read after the focus moved on catches it
                if (r.Ct == "DataItem" && !_settings.ReadAfterKeys) CellFocusMoved(el, r);
            }
            else ScheduleReread("focus " + r.Ct); // any focus move inside a document: an inner tab may have changed
            Emit(r);
            PollTabs(_foreground, force: false);
            PollMdi(_foreground, force: false);
        }
        catch { }
    }

    private void OnElementEvent(AutomationElement el, string act, IntPtr hwnd)
    {
        try
        {
            NoteInput();
            var r = Describe(el, act);
            if (act == Acts.Invoke && r.Ct == "Window") return;
            if (act == Acts.Invoke) { r.How = PressHow.Uia; EmitPress(r); return; }
            if (act == Acts.Select && r.Ct == "MenuItem") { lock (_gate) _lastMenuItem = (r.Name, DateTime.Now); }
            if (act == Acts.Select && r.Ct == "TabItem" && _settings.TabDocuments && r.Name.Length > 0)
            {
                // a tab item inside a document (or in a window whose documents are MDI children) is an inner page, never a document:
                // the re-read of the document reports the page that is selected now
                bool inner; lock (_gate) inner = _mdiClients.ContainsKey(hwnd);
                if (!inner) { try { inner = IsInsideDocument(el, _automation.FromHandle(hwnd)); } catch { } }
                if (inner) { ScheduleReread("tab " + r.Name); return; }
                // a ribbon's page (Home, Insert): a selection, never a document
                if (IsRibbon(el)) { r.Val ??= r.Name; Emit(r); return; }
                TabSelected(el, hwnd, r.Name); return;
            }
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

    private void OnValueChanged(AutomationElement el, PropertyId prop, object newValue) => HandleValue(el, newValue as string ?? newValue?.ToString());

    /// <summary>
    /// A field's new value, from its value event or its text event (WPF text boxes raise both): one test decides whether the
    /// user typed it or the program filled it, for both.
    /// </summary>
    private void HandleValue(AutomationElement el, string? v)
    {
        try
        {
            NoteInput();
            // a line break the field keeps after its text (Enter in a multi-line box) is not part of the value
            v = v?.TrimEnd('\r', '\n');
            var r = Describe(el, Acts.Value);
            // a link, a button, a label: not a field (Access's side panes report their links' "values")
            if (r.Ct is "Hyperlink" or "Button" or "SplitButton" or "MenuItem" or "TabItem" or "Text" or "Image" or "Window" or "Pane" or "Group") return;
            if (r.Ct == "DataItem" && v is not null) lock (_gate) { var ck = r.Aid.Length > 0 ? r.Aid : r.Name; _cellValues[ck] = v; _cellValueAt[ck] = DateTime.Now; } // (CellFocusMoved compares with it)
            if (FieldReads.IsItemPlaceholder(r.Name))
            {
                // a WPF grid editor names its row's item and column number: the column's header is its name
                string pos = ""; try { var b = el.BoundingRectangle; pos = $"{b.Left},{b.Top} {b.Width}x{b.Height}"; } catch { }
                var (rk, _, _, _, _) = ReadTarget();
                List<ControlRow>? rows; lock (_gate) _fieldRows.TryGetValue(rk, out rows);
                r.Name = rows is null ? "" : FieldReads.ColumnOfPlaceholder(r.Name, pos, rows);
                if (r.Name.Length == 0) r.Name = Labels.Clean(r.Aid);
            }
            r.Val = Masking.ForEvent(v, r.Name, r.Aid, r.IsPassword);
            // a tick box's new state right after the click on it: the press already said so
            lock (_gate) { if (r.Ct is "CheckBox" or "RadioButton" && _lastPress.name == r.Name && (DateTime.Now - _lastPress.t).TotalMilliseconds is >= 0 and < 1500) return; }
            // the field already reported what the keys wrote, then a click elsewhere changed it again (a button added to it)
            bool afterClick;
            {
                var fkey = r.Aid.Length > 0 ? r.Aid : r.Name;
                string pos = ""; try { var b = el.BoundingRectangle; pos = $"{b.Left},{b.Top} {b.Width}x{b.Height}"; } catch { }
                lock (_gate) afterClick = FieldReads.ChangedAfterClick(_lastTyped.GetValueOrDefault(fkey), _ownTypedAt.GetValueOrDefault(fkey), _typingAt, _tracker.LastClick, pos, v ?? "", DateTime.Now);
            }
            // a grid cell the user went into, keys went down since, and this is its first value since: typed there, whatever the
            // focus says now (Excel commits a cell on Tab or Enter and reports it after the focus moved on, often a second late)
            var committedCell = false;
            if (r.Ct == "DataItem" && !afterClick)
                lock (_gate)
                {
                    DateTime at = DateTime.MinValue;
                    foreach (var k in new[] { r.Name, r.Aid }) if (k.Length > 0 && _visitedCells.Remove(k, out var t) && t > at) at = t;
                    committedCell = !string.IsNullOrWhiteSpace(v) && FieldReads.CommittedCell(at, _typingAt > _anyKeyAt ? _typingAt : _anyKeyAt, DateTime.Now);
                    // the value that follows within 1.5 s is the same typing (a formula's result after a passing "0"): it replaces it
                    var gk = r.Aid.Length > 0 ? r.Aid : r.Name;
                    if (committedCell) _cellGrace[gk] = DateTime.Now.AddMilliseconds(1500);
                    else if (!string.IsNullOrWhiteSpace(v) && _cellGrace.TryGetValue(gk, out var until) && DateTime.Now <= until) committedCell = true;
                }
            if (!committedCell && (afterClick || !ChangedByUser(el, r) || !KeyedInto(r, el)))
            {
                // a total recalculated, a name looked up from a code: the program filled it in (personal fields keep their shape only)
                r.By = ValueBy.App;
                r.Val = Masking.ForAppValue(v, r.Name, r.Aid, r.IsPassword, r.Ct);
                _status.ValuesFilled++;
                Emit(r);
                InferPress("value filled", from: CurrentTitle);
                return;
            }
            // an editor lying over a grid cell names the grid, not the column: its value waits for the cell read; the value the
            // cell already had (the default quantity shown when editing starts) is not typed
            System.Drawing.Rectangle box = default; try { box = el.BoundingRectangle; } catch { }
            // (a grid cell that reports its own value names itself: it is never an editor lying over a cell)
            if (box.Width > 0 && r.Ct != "DataItem")
            {
                var (rk, _, _, _, _) = ReadTarget();
                List<FieldRead>? last; lock (_gate) _fieldReads.TryGetValue(rk, out last);
                var cell = last is null ? null : FieldReads.AtRect(last, $"{box.Left},{box.Top} {box.Width}x{box.Height}");
                if (cell is null)
                {
                    List<ControlRow>? gridRows; lock (_gate) _fieldRows.TryGetValue(rk, out gridRows);
                    if (gridRows is not null && gridRows.Any(g => (g.Type is "DataGrid" or "Table" || g.AutomationId.StartsWith("grid", StringComparison.OrdinalIgnoreCase)) && Labels.TryRect(g.Position, out var gx, out var gy, out var gw, out var gh) && box.Left >= gx && box.Top >= gy && box.Right <= gx + gw && box.Bottom <= gy + gh))
                        r.OverGridCell = true; // its value waits for the row read that names the column
                }
                if (cell is { IsCell: true })
                {
                    if (cell.Value == v || v is { Length: > 0 } && cell.Value.StartsWith(v, StringComparison.Ordinal)) return; // the cell shows it already (a late keystroke)
                    r.OverGridCell = true;
                }
            }
            lock (_gate)
            {
                // (keystrokes in the same box replace each other: "1", "10", "100EX" leave one entry, never a "1" to match)
                if (box.Width > 0 && r.Val is { Length: > 0 } tv)
                {
                    // (a jump of many characters at once is a lookup's completion, kept beside the typed partial, never over it)
                    var same = _boxHistory.FindLastIndex(b => b.place == box && (DateTime.Now - b.t).TotalSeconds <= 10 && tv.Length <= b.text.Length + 2);
                    if (same >= 0) _boxHistory[same] = (_boxHistory[same].hwnd, tv, box, DateTime.Now);
                    else { _boxHistory.Add((IntPtr.Zero, tv, box, DateTime.Now)); if (_boxHistory.Count > 8) _boxHistory.RemoveAt(0); }
                }
                r.KeysSeen = _keysSinceFocus;
                _lastTyped[r.Aid.Length > 0 ? r.Aid : r.Name] = r.Val ?? "";
                _lastKeyed[r.Aid.Length > 0 ? r.Aid : r.Name] = r.Val ?? "";
                _ownTypedAt[r.Aid.Length > 0 ? r.Aid : r.Name] = DateTime.Now;
                if (_lastField.name.Length == 0 || _lastField.aid != r.Aid) _lastField = (r.Name, r.Aid, r.Ct, DateTime.Now, box);
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
    /// <summary>A key that types (or picks in a list) went down after the focus arrived in the field, or a click landed on a drop-down or tick box: the same test the re-read uses.</summary>
    private bool KeyedInto(RawEvent r, AutomationElement el)
    {
        string pos = ""; try { var b = el.BoundingRectangle; pos = $"{b.Left},{b.Top} {b.Width}x{b.Height}"; } catch { }
        DateTime typing, arrival = DateTime.MinValue; (int x, int y, DateTime t) click;
        lock (_gate)
        {
            typing = _typingAt; click = _tracker.LastClick;
            var mine = new[] { r.Aid, Labels.Clean(r.Aid), r.Name };
            foreach (var k in mine)
                if (k.Length > 0 && _focusArrivedAt.TryGetValue(k, out var a) && a > arrival) arrival = a;
            arrival = StillHere(arrival, mine);
        }
        // the focus's arrival by the window handle, when the field has one (the UI Automation focus event can come late)
        var ownWindow = false;
        try
        {
            var h = el.Properties.NativeWindowHandle.ValueOrDefault;
            ownWindow = h != IntPtr.Zero;
            if (h != IntPtr.Zero) lock (_gate) { if (_focusSince.TryGetValue(h, out var since)) arrival = since; }
        }
        catch { }
        if (arrival != DateTime.MinValue && (DateTime.Now - arrival).TotalSeconds > 30) arrival = DateTime.MinValue; // long ago: says nothing
        // no key ever seen since the watcher attached: the key state cannot be read here (a remote session, values set by
        // automation): the test is not made, as before
        if (typing == DateTime.MinValue) return true;
        // a key read at this very event (the value changed as the key went down): the user's, whatever order the focus event
        // and this event arrived in (they come on different threads)
        if ((DateTime.Now - typing).TotalMilliseconds is >= 0 and <= 250) return true;
        // the field's focus arrival was never seen and keys went down in the last 8 s: under load UI Automation delivers a
        // value event seconds after the key (the second order of the r6 run lost its product code to the 3 s window)
        // ... unless Tab went down after the last typing key and just now: the keys were the field the user left, and a field
        // whose focus arrival is not seen yet (WPF and web pages raise the value before the focus) was filled by the program
        DateTime tabAt; lock (_gate) tabAt = _tabAt;
        // (only fields without a window of their own: WPF and web pages; a toolkit editor with its own window, Inform's, has its
        // focus arrival read from the window, and its late value after Tab is still the keys')
        // and only the field the focus moved INTO (it has the keyboard focus now); the field the user left reports its typed value
        // on leaving it (web pages do), and that value is the keys'
        if (arrival == DateTime.MinValue && !ownWindow && FieldReads.FilledOnTabArrival(typing, tabAt, DateTime.Now) && HasFocus(el)) return false;
        if (arrival == DateTime.MinValue && (DateTime.Now - typing).TotalSeconds is >= 0 and <= 8) return true;
        return FieldReads.UserChanged(r.Ct, pos, DateTime.Now, typing, arrival, click);
    }

    // the dialogs drawn inside a page that the last read of each window showed (title -> is a message)
    private readonly Dictionary<string, Dictionary<string, bool>> _inPageOpen = new(StringComparer.Ordinal);

    /// <summary>
    /// A dialog drawn inside a web page has no window of its own, so no window event: the read after a click or key shows it.
    /// A message is recorded like a message box (with its text), a small named form like a window opened; each is closed when
    /// a later read no longer shows it.
    /// </summary>
    private void InPageDialogs(string readKey, IList<ControlRow> rows, string screen, IntPtr hwnd)
    {
        var now = DialogDetector.InPage(rows).GroupBy(d => d.title).Select(g => g.First()).ToList();
        List<(string title, string? body, bool message)> opened; List<(string title, bool message)> closed;
        lock (_gate)
        {
            var before = _inPageOpen.GetValueOrDefault(readKey) ?? new Dictionary<string, bool>();
            opened = now.Where(d => !before.ContainsKey(d.title)).ToList();
            closed = before.Where(kv => !now.Any(d => d.title == kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
            if (now.Count == 0) _inPageOpen.Remove(readKey); else _inPageOpen[readKey] = now.ToDictionary(d => d.title, d => d.message);
        }
        foreach (var (title, message) in closed)
        {
            InferPress("window closed", from: message ? screen : title);
            Emit(new RawEvent { Act = Acts.Close, Ct = "Window", Name = title, WindowTitle = message ? screen : title, Hwnd = hwnd });
        }
        foreach (var (title, body, message) in opened)
        {
            if (message) _status.Dialogs++;
            Emit(new RawEvent { Act = message ? Acts.Dialog : Acts.Open, Ct = "Window", Name = title, WindowTitle = message ? title : title, Val = message ? body : null, IsDialog = message, Hwnd = hwnd });
            InferPress(message ? "dialog" : "window opened", from: screen);
        }
    }

    /// <summary>
    /// A field's focus arrival says nothing once the focus has arrived in another field since (it left this one): the keys typed
    /// after that went elsewhere. Call under the gate.
    /// </summary>
    private DateTime StillHere(DateTime arrival, string[] keys)
    {
        if (arrival == DateTime.MinValue) return arrival;
        foreach (var kv in _focusArrivedAt)
            if (kv.Value > arrival && kv.Key.Length > 0 && !keys.Contains(kv.Key)) return DateTime.MinValue;
        return arrival;
    }

    private static bool HasFocus(AutomationElement el) { try { return el.Properties.HasKeyboardFocus.ValueOrDefault; } catch { return false; } }
    private string FocusedAid() { try { return _automation.FocusedElement()?.Properties.AutomationId.ValueOrDefault ?? ""; } catch { return ""; } }

    /// <summary>The field read has the keyboard focus now: the focused element has its automation id, or, when it has none, its place.</summary>
    private bool HasFocusNow(FieldRead f)
    {
        try
        {
            var fe = _automation.FocusedElement();
            if (fe is null) return false;
            if (f.AutomationId.Length > 0) return (fe.Properties.AutomationId.ValueOrDefault ?? "") == f.AutomationId;
            var b = fe.BoundingRectangle;
            return f.Position.Length > 0 && $"{b.Left},{b.Top} {b.Width}x{b.Height}" == f.Position;
        }
        catch { return false; }
    }

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
            // the focused element is the inner editor of this field, or this field is the inner editor of the focused one: by id first, then by place
            string fa = ""; try { fa = focused.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            if (fa.Length > 0 && r.Aid.Length > 0 && (fa == r.Aid || Labels.Clean(fa) == Labels.Clean(r.Aid))) return true;
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
                if (el is null || !IsProgramPid(el.Properties.ProcessId.ValueOrDefault)) return;
                var r = Describe(el, Acts.Hint);
                if (r.Ct is not ("Button" or "SplitButton" or "MenuItem" or "CheckBox" or "RadioButton" or "Edit" or "ComboBox" or "Spinner" or "TabItem" or "Hyperlink" or "Image" or "Text")) return;
                if (r.Name.Length == 0) return;
                if (Hints.IsFieldValueTip(r.Ct, text)) return; // "Remit-to: Same" over a field is its own label and value, not a help text
                if (IsMenuOrScreenName(text, r.Name)) return;   // "Accounting" over the Sales Order button is the name of a menu, not help
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

    /// <summary>The tip text is the name of a menu, a button, a tab or a window of the program (other than the control it hangs over).</summary>
    private bool IsMenuOrScreenName(string text, string subject)
    {
        var t = text.Trim();
        if (t.Length == 0 || string.Equals(t, subject, StringComparison.OrdinalIgnoreCase)) return false;
        lock (_gate)
        {
            if (_knownWindows.Values.Any(w => string.Equals(w, t, StringComparison.OrdinalIgnoreCase))) return true;
            foreach (var rows in _rows.Values)
                if (rows.Any(c => c.Type is "MenuItem" or "MenuBar" or "TabItem" or "Button" && string.Equals(c.Name.Trim(), t, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private void OnToggleChanged(AutomationElement el, PropertyId prop, object newValue)
    {
        try
        {
            if (!_inFront) return;
            NoteInput();
            var r = Describe(el, Acts.Invoke);
            var val = newValue?.ToString() switch { "On" or "1" => "on", "Off" or "0" => "off", var s => s };
            // the user's tick needs a click that just began or the focus on the box; a box the program flips (a record loaded) is a value it filled
            var now = DateTime.Now;
            // the click on this box was already counted (its invoke came first, by MSAA or UI Automation): one click, one event
            lock (_gate) { if (_lastPress.name == r.Name && (now - _lastPress.t).TotalMilliseconds is >= 0 and < 1500) return; }
            bool clicked, focused;
            lock (_gate)
            {
                // the click landed on this box (its recorded tree or the live read said so); a box the program flips when a record loads is a value
                clicked = _decider.ClickedRecently(now) && _decider.Pending?.Name == r.Name;
                focused = false;
            }
            if (!PressDecider.ToggleIsUserAction(clicked, focused))
            {
                r.Act = Acts.Value; r.By = ValueBy.App; r.Val = val;
                _status.ValuesFilled++;
                Emit(r);
                return;
            }
            lock (_gate) _tracker.Pressed(r.Name, PressHow.Toggle, now);
            r.How = PressHow.Toggle;
            r.Val = val;
            _status.Toggles++;
            Emit(r, press: true);
            ScheduleReread("tick " + r.Name);
        }
        catch { }
    }

    private void OnTextChanged(AutomationElement el)
    {
        if (!_inFront) return; // reading the text is a cross-process call; skip it when nobody is recording anyway
        try
        {
            string? v = null;
            try { v = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault; } catch { }
            if (v is null)
            {
                try { v = el.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(500); } catch { }
            }
            if (v is null) return;
            HandleValue(el, v); // the same test as a value event: typed by the user or filled by the program
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
        if ((r.Name.Length == 0 || Labels.IsWrapperId(r.Name) || Masking.LooksLikeData(r.Name)) && r.Ct is "Edit" or "DataItem" or "ComboBox" or "Custom" or "Text")
        {
            // 0b. a grid cell editor with no name (or a name that is its own value): the column of the recorded tree whose cell holds it
            try
            {
                var rect = el.BoundingRectangle;
                List<ControlRow>? rows; lock (_gate) _rows.TryGetValue(FrontHwnd(), out rows);
                var column = rows is null || rect.IsEmpty ? null : ColumnAt(rows, rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                if (column is not null) r.Name = column;
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

    /// <summary>The column name of the recorded grid cell (DataItem) or header that holds a point, cleaned of line breaks; null when none.</summary>
    private static string? ColumnAt(List<ControlRow> rows, int x, int y)
    {
        ControlRow? best = null; long bestArea = long.MaxValue;
        foreach (var r in rows)
        {
            if (r.Type is not ("DataItem" or "HeaderItem") || r.Name.Trim().Length == 0) continue;
            if (!Labels.TryRect(r.Position, out var rx, out var ry, out var rw, out var rh) || rw <= 0 || rh <= 0) continue;
            if (r.Type == "HeaderItem") { if (x < rx || x >= rx + rw) continue; }
            else if (x < rx || x >= rx + rw || y < ry || y >= ry + rh) continue;
            var area = (long)rw * rh;
            if (best is null || (r.Type == "DataItem" && best.Type == "HeaderItem") || (r.Type == best.Type && area < bestArea)) { best = r; bestArea = area; }
        }
        if (best is null) return null;
        var name = best.Name.Replace("\r", " ").Replace("\n", " ").Trim();
        while (name.Contains("  ")) name = name.Replace("  ", " ");
        return name.Length == 0 || name.Length > 40 || Masking.LooksLikeData(name) ? null : name;
    }

    private void Emit(RawEvent r, bool press = false)
    {
        r.WindowTitle ??= CurrentTitle;
        // a field labelled with the record's name from the window's title (the vendor's name beside the notes box) is data:
        // it goes by its cleaned automation id
        // (the record's name is quoted in this window's title or in the title of any window seen before: Inform's notes window
        // carries the vendor's name while the vendor document's label shows it beside the notes box)
        if (r.WindowTitle is { Length: > 0 } wt && wt.Contains('"'))
            lock (_quotedNames) foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(wt, "\"([^\"]{3,})\"")) { _quotedNames.Add(m.Groups[1].Value.Trim()); if (_quotedNames.Count > 500) _quotedNames.Clear(); }
        if (r.Act is Acts.Value or Acts.Focus or Acts.Dropdown && r.Name.Trim().Length >= 3)
        {
            bool data; lock (_quotedNames) data = _quotedNames.Any(q => Core.Learning.Titles.NameIsRecordName(r.Name, q));
            if (data || Core.Learning.Titles.IsQuotedInTitle(r.Name, r.WindowTitle)) r.Name = Labels.Clean(r.Aid);
        }
        // grid column headers carry line breaks ("Net\r\nPrice"): one line, one space
        if (r.Name.IndexOfAny(new[] { '\r', '\n' }) >= 0) r.Name = string.Join(' ', r.Name.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        // Access names the field that has the focus after its section ("Detail Section, Code") and a combo box after its state
        r.Name = Labels.WithoutSection(r.Name);
        // a press names the words, never the shortcut a toolkit put into the name ("Settings Alt+S", "OK Enter")
        if (press || r.Act == Acts.Invoke) r.Name = Labels.WithoutShortcut(r.Name);
        // a selection of nothing (a list that lost its selection) says nothing
        if (r.Act == Acts.Select && string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Val)) return;
        // the program reporting a field again with nothing changed is no new fill (Excel re-announces the cells around an edit)
        if (r.Act == Acts.Value)
        {
            var key = (r.WindowTitle ?? "") + "\u0001" + (r.Aid.Length > 0 ? r.Aid : r.Name) + "\u0001" + (r.Row ?? "");
            var now = DateTime.Now;
            lock (_lastFill)
            {
                if (r.By == ValueBy.App)
                {
                    if (_lastFill.TryGetValue(key, out var lf) && FieldReads.RepeatFill(lf.val, lf.t, r.Val, now)) { _lastFill[key] = (lf.val, now); return; }
                    if (_lastFill.Count > 2000) _lastFill.Clear();
                    _lastFill[key] = (r.Val ?? "", now);
                }
                else
                {
                    // a grid cell telling the same typed text again (Excel re-announces cells) is no new typing
                    if (r.Ct == "DataItem" && _lastFill.TryGetValue(key, out var lt) && FieldReads.RepeatFill(lt.val, lt.t, r.Val, now)) return;
                    _lastFill[key] = (r.Val ?? "", now); // the user typed it: the program showing the same text again is no fill
                }
            }
        }
        if (!press && r.Act != Acts.Focus) lock (_gate) _decider.EventEmitted();
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
            using (Cache(true, withValues: true).Activate())
            {
                var cached = _automation.FromHandle(hwnd);
                // with values: a cell that holds its own editor (WPF template columns) shows its text only through that editor
                WalkCached(cached, 0, _settings.DumpDepth, rows, _settings.DumpRowCap, maxRows, onlyOnScreen: true, withValues: true);
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
                        for (; k < rows.Count && rows[k].Depth > r.Depth; k++) if (rows[k].Type is not ("Note") && rows[k].Name.Length > 0 && rows[k].Depth == r.Depth + 1) cells.Add(FieldReads.IsItemPlaceholder(rows[k].Name) ? FieldReads.InnerText(rows, k) : rows[k].Name);
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
            // the selection of inner tab items, only where the profile says the read is safe (asking a WinForms tab item selects it)
            if (_settings.SelectionReads) { try { cr.Add(_automation.PatternLibrary.SelectionItemPattern); cr.Add(lib.SelectionItem.IsSelected); } catch { } }
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
            catch (Exception ex)
            {
                if (!Win32.IsWindowVisible(hwnd)) { _status.PopupsGone++; break; } // the window closed while it was read (a lookup list): not a failed read
                Log.Warn($"cached tree dump failed ('{title}', filter {withFilter}): {ex.Message}"); _status.DumpsFailed++;
            }
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

    private static void WalkCached(AutomationElement el, int depth, int maxDepth, List<ControlRow> rows, int max, int gridRowCap, bool onlyOnScreen, bool withValues = false, bool selectionReads = false)
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
                else if (ct == "TabItem")
                {
                    // the selected page: the cached selection flag where the profile allows it, else the page content on screen under the tab item
                    var selected = false;
                    if (selectionReads) { try { selected = el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault ?? false; } catch { } }
                    if (!selected) // (a cached selection flag that says no, or none: the page on screen decides)
                    {
                        try
                        {
                            foreach (var c in el.CachedChildren)
                            {
                                string cct; try { cct = c.Properties.ControlType.ValueOrDefault.ToString(); } catch { continue; }
                                if (cct == "Button") continue;
                                bool off = true; try { off = c.Properties.IsOffscreen.ValueOrDefault; } catch { }
                                var cr = c.Properties.BoundingRectangle.ValueOrDefault;
                                if (rect.Width > 0 && rect.Contains(cr)) continue; // the tab's own label (WPF): says nothing about its page
                                if (!off && cr.Width > 0 && cr.Height > 0) { selected = true; break; }
                            }
                        }
                        catch { }
                    }
                    if (selected) value = "selected";
                }
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
                    if (kept < gridRowCap) { kept++; WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues, selectionReads); }
                    else if (columns.Count == 0 && kept > 0) { try { columns = c.CachedChildren.Select(x => { try { return x.Properties.Name.ValueOrDefault ?? ""; } catch { return ""; } }).Where(n => n.Length > 0).Take(30).ToList(); } catch { } }
                    continue;
                }
                WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues, selectionReads);
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
            WalkCached(c, depth + 1, maxDepth, rows, max, gridRowCap, onlyOnScreen, withValues, selectionReads);
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
        _rereadAgainTimer?.Dispose();
        try { _automation.Dispose(); } catch { }
    }
}
