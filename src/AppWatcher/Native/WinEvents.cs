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
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_INVOKED = 0x8013;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const int OBJID_CLIENT = -4;
    public const int OBJID_MENU = -3;
    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2;
    public const int VK_LBUTTON = 0x01, VK_TAB = 0x09, VK_RETURN = 0x0D;

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
