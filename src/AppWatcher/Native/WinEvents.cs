using System.Runtime.InteropServices;
using System.Text;

namespace AppWatcher.Native;

/// <summary>Thin P/Invoke layer. Only WinEvent hooks (no keyboard hooks of any kind).</summary>
internal static class Win32
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MENUSTART = 0x0004;
    public const uint EVENT_SYSTEM_MENUEND = 0x0005;
    public const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    public const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
    /// <summary>A window took the mouse capture: a button went down on it (WinForms and toolkit buttons capture on mouse down).</summary>
    public const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
    public const uint EVENT_SYSTEM_CAPTUREEND = 0x0009;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint EVENT_OBJECT_SELECTION = 0x8006;
    public const uint EVENT_OBJECT_SELECTIONWITHIN = 0x8009;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_INVOKED = 0x8013;
    /// <summary>Location changes; with OBJID_CURSOR the pointer moved (a WinEvent, not a mouse hook: no input is seen or held).</summary>
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const int OBJID_CURSOR = -9;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int OBJID_CLIENT = -4;
    public const int OBJID_MENU = -3;
    public const uint GA_ROOT = 2, GA_ROOTOWNER = 3;
    public const uint GW_HWNDNEXT = 2, GW_OWNER = 4;
    public const int VK_LBUTTON = 0x01, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_ESCAPE = 0x1B;

    /// <summary>A read of the key state (no hook): bit 0 says the key went down since the previous call, bit 15 that it is down now.</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>True when the key went down since the last time anyone asked (one read, consumed).</summary>
    public static bool WentDown(int vKey) { try { return (GetAsyncKeyState(vKey) & 1) != 0; } catch { return false; } }

    private static readonly int[] TypingKeys = BuildTypingKeys();
    private static int[] BuildTypingKeys()
    {
        var k = new List<int> { 0x08, 0x2E, 0x25, 0x26, 0x27, 0x28 }; // Backspace, Delete, the arrows (a pick in a list is the user's)
        for (var v = 0x30; v <= 0x39; v++) k.Add(v);                 // digits
        for (var v = 0x41; v <= 0x5A; v++) k.Add(v);                 // letters
        for (var v = 0x60; v <= 0x6F; v++) k.Add(v);                 // number pad
        for (var v = 0xBA; v <= 0xC0; v++) k.Add(v);                 // ; = , - . / `
        for (var v = 0xDB; v <= 0xDF; v++) k.Add(v);                 // [ \ ] '
        return k.ToArray();
    }

    /// <summary>True when a key that types a character (or deletes one) went down since the last read: a read of the key state, never a hook; which key is not kept.</summary>
    public static bool TypingKeyWentDown() => TypingKeysWentDown() > 0;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    /// <summary>True when the pointer can be read and is not at 0,0 (a failed read gives 0,0).</summary>
    public static bool GetCursorPosOk() { try { return GetCursorPos(out var p) && (p.X != 0 || p.Y != 0); } catch { return false; } }

    /// <summary>How many different character keys went down since the last read (a lower bound on the characters typed).</summary>
    public static int TypingKeysWentDown()
    {
        var n = 0;
        try { foreach (var v in TypingKeys) if ((GetAsyncKeyState(v) & 1) != 0) n++; } catch { }
        return n;
    }

    /// <summary>One read of a key's state: whether it went down since the last read (bit 0) and whether it is down now (bit 15).</summary>
    public static (bool wentDown, bool down) KeyState(int vKey)
    {
        try { var s = GetAsyncKeyState(vKey); return ((s & 1) != 0, (s & 0x8000) != 0); } catch { return (false, false); }
    }

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, System.Text.StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutInt(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);

    /// <summary>
    /// The text of a text box of another program (WM_GETTEXT, a read; at most 100 ms, never when the program hangs), or null.
    /// Never for a password box (ES_PASSWORD) and never more than 200 characters.
    /// </summary>
    /// <summary>A plain text box (an EDIT window) that is not a password box: the only kind whose text the watcher reads.</summary>
    public static bool IsPlainEdit(IntPtr hwnd)
    {
        try { return hwnd != IntPtr.Zero && ClassName(hwnd).Contains("EDIT", StringComparison.OrdinalIgnoreCase) && (GetWindowLong(hwnd, -16) & 0x20) == 0; }
        catch { return false; }
    }

    public static string? EditText(IntPtr hwnd, uint timeoutMs = 30)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;
            var cls = ClassName(hwnd);
            if (!cls.Contains("EDIT", StringComparison.OrdinalIgnoreCase)) return null;      // WinForms text boxes: WindowsForms10.EDIT.*
            if ((GetWindowLong(hwnd, -16) & 0x20) != 0) return null;                          // ES_PASSWORD
            // one message, at most 30 ms: a busy program costs the 50 ms button read little
            const uint WM_GETTEXT = 0x000D, SMTO_ABORTIFHUNG = 0x0002;
            var sb = new System.Text.StringBuilder(257);
            if (SendMessageTimeoutText(hwnd, WM_GETTEXT, (IntPtr)257, sb, SMTO_ABORTIFHUNG, timeoutMs, out var got) == IntPtr.Zero) return null;
            return (int)got > 200 ? null : sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>The deepest visible window at a screen point (a read).</summary>
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize; public int flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Rect rcCaret;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    /// <summary>The window with the keyboard focus in the GUI thread that owns <paramref name="anyWindowOfThread"/> (GetGUIThreadInfo, a read), or zero.</summary>
    public static IntPtr FocusedWindowOf(IntPtr anyWindowOfThread)
    {
        if (anyWindowOfThread == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var tid = GetWindowThreadProcessId(anyWindowOfThread, out _);
            var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
            return tid != 0 && GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The MDI client pane a window is a direct child of (its parent's class contains MDICLIENT), or zero.</summary>
    public static IntPtr MdiClientOf(IntPtr hwnd)
    {
        try
        {
            var parent = GetParent(hwnd);
            return parent != IntPtr.Zero && AppWatcher.Core.Screens.MdiWorkspace.IsMdiClientClass(ClassName(parent)) ? parent : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The visible children of an MDI client pane in z-order, front first (GetTopWindow, then the next siblings).</summary>
    public static List<(IntPtr hwnd, string title)> MdiChildrenFrontFirst(IntPtr mdiClient)
    {
        var list = new List<(IntPtr, string)>();
        try
        {
            var h = GetTopWindow(mdiClient);
            var guard = 0;
            while (h != IntPtr.Zero && guard++ < 200)
            {
                if (IsWindowVisible(h)) list.Add((h, WindowText(h)));
                h = GetWindow(h, GW_HWNDNEXT);
            }
        }
        catch { }
        return list;
    }

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static bool IsTopLevel(IntPtr hwnd) => GetAncestor(hwnd, GA_ROOT) == hwnd;

    public static List<IntPtr> TopLevelWindowsOf(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && ProcessIdOf(h) == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// <summary>Wraps one SetWinEventHook. Create and dispose on the UI thread (the callbacks arrive on its message loop).</summary>
internal sealed class WinEventHook : IDisposable
{
    private readonly Win32.WinEventDelegate _callback; // kept alive for the native side
    private IntPtr _hook;

    public WinEventHook(uint min, uint max, uint pid, Action<uint, IntPtr, int, int> handler)
    {
        _callback = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            try { handler(ev, hwnd, idObject, idChild); }
            catch (Exception ex) { AppWatcher.Core.Log.Error("WinEvent handler failed", ex); }
        };
        _hook = Win32.SetWinEventHook(min, max, IntPtr.Zero, _callback, pid, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    public bool IsActive => _hook != IntPtr.Zero;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Win32.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}
