using System.Threading.Channels;
using AppWatcher.Core;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Native;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
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
    public bool IsPassword;
    public IntPtr Hwnd;
    public string? WindowTitle;
    /// <summary>Set for window-open events: the dumped control tree.</summary>
    public ScreenRecord? Screen;
    public bool IsDialog;
}

/// <summary>
/// Subscribes to UI Automation events of ONE process (focus, invoke, selection, value changes) and to
/// WinEvents for that process's top-level windows (open/close/title) plus the global foreground change.
/// Never installs a keyboard hook. Produces RawEvents into a channel; does no file work itself.
/// </summary>
internal sealed class UiaCollector : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly ChannelWriter<RawEvent> _out;
    private readonly List<WinEventHook> _hooks = new();
    private readonly Dictionary<IntPtr, List<IDisposable>> _perWindow = new();
    private readonly HashSet<string> _dumpedTitles = new(StringComparer.OrdinalIgnoreCase);
    private FocusChangedEventHandlerBase? _focus;
    private readonly SynchronizationContext _ui;
    private uint _pid;
    private IntPtr _foreground;
    private volatile bool _inFront;
    private string _currentTitle = "";
    private readonly Dictionary<IntPtr, string> _knownWindows = new();

    public UiaCollector(ChannelWriter<RawEvent> output, SynchronizationContext uiContext)
    {
        _out = output;
        _ui = uiContext;
    }

    public bool IsAttached => _pid != 0;
    public uint Pid => _pid;
    /// <summary>True while a window of the watched process is the foreground window.</summary>
    public bool InFront => _inFront;
    public string CurrentTitle => _currentTitle;
    public int MainWindowControlCount { get; private set; } = -1;
    public event Action<bool>? InFrontChanged;

    /// <summary>Call on the UI thread.</summary>
    public void Attach(uint pid)
    {
        Detach();
        _pid = pid;
        Log.Info($"attaching to pid {pid}");
        _hooks.Add(new WinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, OnForeground));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_SHOW, pid, OnWindowEvent));
        _hooks.Add(new WinEventHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, pid, OnWindowEvent));
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
        _pid = 0;
        SetInFront(false);
    }

    private void SetInFront(bool v)
    {
        if (_inFront == v) return;
        _inFront = v;
        InFrontChanged?.Invoke(v);
    }

    // ---- WinEvents (UI thread) ----

    private void OnForeground(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (hwnd == IntPtr.Zero) { SetInFront(false); return; }
        var pid = Win32.ProcessIdOf(hwnd);
        var mine = pid == _pid && _pid != 0;
        _foreground = hwnd;
        if (mine)
        {
            _currentTitle = Win32.WindowText(hwnd);
            SetInFront(true);
            Emit(new RawEvent { Act = Acts.Foreground, Hwnd = hwnd, WindowTitle = _currentTitle, Ct = "Window", Name = _currentTitle });
            if (!_perWindow.ContainsKey(hwnd) && Win32.IsTopLevel(hwnd)) RegisterWindow(hwnd, initial: false);
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
                if (!_knownWindows.TryGetValue(hwnd, out var title)) return;
                if (ev == Win32.EVENT_OBJECT_HIDE && Win32.IsWindowVisible(hwnd)) return;
                _knownWindows.Remove(hwnd);
                if (_perWindow.Remove(hwnd, out var list)) foreach (var d in list) { try { d.Dispose(); } catch { } }
                Emit(new RawEvent { Act = Acts.Close, Hwnd = hwnd, WindowTitle = title, Ct = "Window", Name = title });
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                if (!_knownWindows.ContainsKey(hwnd)) return;
                var t = Win32.WindowText(hwnd);
                if (t.Length > 0) { _knownWindows[hwnd] = t; if (hwnd == _foreground) _currentTitle = t; }
                break;
        }
    }

    private void RegisterWindow(IntPtr hwnd, bool initial)
    {
        var title = Win32.WindowText(hwnd);
        if (!Win32.IsWindowVisible(hwnd)) return;
        var cls = Win32.ClassName(hwnd);
        if (cls is "tooltips_class32" or "IME" or "MSCTFIME UI" or "Shell_TrayWnd") return;
        _knownWindows[hwnd] = title;
        var handlers = new List<IDisposable>();
        _perWindow[hwnd] = handlers;
        var isDialog = cls == "#32770";

        // UIA subscriptions scoped to this window's subtree only
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var el = _automation.FromHandle(hwnd);
                var lib = _automation.EventLibrary;
                var invoke = el.RegisterAutomationEvent(lib.Invoke.InvokedEvent, TreeScope.Subtree, (e, _) => OnElementEvent(e, Acts.Invoke));
                var select = el.RegisterAutomationEvent(lib.SelectionItem.ElementSelectedEvent, TreeScope.Subtree, (e, _) => OnElementEvent(e, Acts.Select));
                var value = el.RegisterPropertyChangedEvent(TreeScope.Subtree, OnValueChanged, _automation.PropertyLibrary.Value.Value);
                lock (handlers) { handlers.Add(invoke); handlers.Add(select); handlers.Add(value); }
                // Document controls (rich text, editors) report edits through the Text pattern instead of the Value property
                try
                {
                    var text = el.RegisterAutomationEvent(lib.Text.TextChangedEvent, TreeScope.Subtree, (e, _) => OnTextChanged(e));
                    lock (handlers) handlers.Add(text);
                }
                catch { /* not every provider supports it */ }

                // dump the control tree once per window title (after a short delay so the window is populated)
                Thread.Sleep(initial ? 50 : 400);
                var rec = DumpTree(el, title.Length > 0 ? title : Win32.WindowText(hwnd));
                var dialogText = isDialog || (rec.Controls.Count <= 40 && rec.Controls.Any(c => c.Type == "Button") && rec.Controls.Any(c => c.Type == "Text"))
                    ? string.Join(" / ", rec.Controls.Where(c => c.Type == "Text" && c.Name.Length > 0).Select(c => c.Name).Take(8)) : null;
                var dialogLike = isDialog || (dialogText is not null && rec.Controls.Count <= 25 && rec.Controls.Count(c => c.Type == "Button") <= 4 && rec.Controls.All(c => c.Type is not ("Edit" or "DataGrid" or "List" or "Tree" or "Document" or "Table")));
                bool firstTime;
                lock (_dumpedTitles) firstTime = _dumpedTitles.Add(Core.Learning.Titles.Key(rec.Title));
                if (!initial || _knownWindows.Count == 1) { if (MainWindowControlCount < 0 || rec.Controls.Count > MainWindowControlCount) MainWindowControlCount = Math.Max(MainWindowControlCount, rec.Controls.Count); }
                Emit(new RawEvent
                {
                    Act = dialogLike && !initial ? Acts.Dialog : Acts.Open, Hwnd = hwnd, WindowTitle = rec.Title, Ct = "Window", Name = rec.Title,
                    Val = dialogLike ? dialogText : null, Screen = firstTime ? rec : null, IsDialog = dialogLike,
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"window registration failed for '{title}': {ex.Message}");
            }
        });
    }

    // ---- UIA callbacks (thread pool) ----

    private void OnFocus(AutomationElement el)
    {
        try
        {
            if (el.Properties.ProcessId.ValueOrDefault != _pid) return;
            var r = Describe(el, Acts.Focus);
            if (r.Ct == "Window" || r.Ct == "Pane" && r.Name.Length == 0) return;
            Emit(r);
        }
        catch { }
    }

    private void OnElementEvent(AutomationElement el, string act)
    {
        try
        {
            var r = Describe(el, act);
            if (act == Acts.Invoke && r.Ct == "Window") return;
            Emit(r);
        }
        catch { }
    }

    private void OnValueChanged(AutomationElement el, PropertyId prop, object newValue)
    {
        try
        {
            var r = Describe(el, Acts.Value);
            var v = newValue as string ?? newValue?.ToString();
            r.Val = Masking.ForEvent(v, r.Name, r.Aid, r.IsPassword);
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

    private RawEvent Describe(AutomationElement el, string act)
    {
        var r = new RawEvent { Act = act };
        try { r.Ct = el.Properties.ControlType.ValueOrDefault.ToString(); } catch { r.Ct = "Unknown"; }
        try { r.Name = el.Properties.Name.ValueOrDefault ?? ""; } catch { }
        try { r.Aid = el.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
        try { r.IsPassword = el.Properties.IsPassword.ValueOrDefault; } catch { }
        if (r.Name.Length > 200) r.Name = r.Name[..200];
        if (act == Acts.Select && r.Val is null) r.Val = Masking.ForEvent(r.Name, r.Name, r.Aid, r.IsPassword);
        return r;
    }

    private void Emit(RawEvent r)
    {
        r.WindowTitle ??= _currentTitle;
        _out.TryWrite(r);
    }

    // ---- control tree dump (read only) ----

    private ScreenRecord DumpTree(AutomationElement root, string title)
    {
        var rec = new ScreenRecord { Title = title, Captured = DateTime.Now };
        var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        Walk(walker, root, 0, 12, rec.Controls, 1500);
        return rec;
    }

    private static void Walk(ITreeWalker walker, AutomationElement el, int depth, int maxDepth, List<ControlRow> rows, int max)
    {
        if (depth > maxDepth || rows.Count >= max) return;
        try
        {
            var rect = el.Properties.BoundingRectangle.ValueOrDefault;
            var ct = el.Properties.ControlType.ValueOrDefault.ToString();
            var name = (el.Properties.Name.ValueOrDefault ?? "").Replace("|", "/").Trim();
            var aid = el.Properties.AutomationId.ValueOrDefault ?? "";
            var pwd = false;
            try { pwd = el.Properties.IsPassword.ValueOrDefault; } catch { }
            rows.Add(new ControlRow { Depth = depth, Type = ct, Name = name.Length > 120 ? name[..120] : name, AutomationId = aid, Position = $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}", IsPassword = pwd });
        }
        catch { return; }
        AutomationElement? child = null;
        try { child = walker.GetFirstChild(el); } catch { }
        while (child is not null && rows.Count < max)
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
