using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using AppWatcher.Core.Screens;

namespace AppWatcher.Native;

/// <summary>
/// The older accessibility interface (IAccessible). Two uses, both read-only: naming the object behind a
/// WinEvent (a button that was invoked or focused when UI Automation stays silent), and walking a window's
/// tree when UI Automation shows fewer than ten controls (Delphi, Java with the Access Bridge, custom toolkits).
/// </summary>
internal static class Msaa
{
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromEvent(IntPtr hwnd, uint idObject, uint idChild, [MarshalAs(UnmanagedType.Interface)] out IAccessible? acc, [MarshalAs(UnmanagedType.Struct)] out object? child);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint idObject, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? acc);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessible acc, int iChildStart, int cChildren, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);

    [DllImport("oleacc.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRoleText(uint role, StringBuilder text, uint max);

    private static Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private const int ChildSelf = 0;
    public const int RolePushButton = 0x2B, RoleMenuItem = 0x0C, RoleCheckButton = 0x2C, RoleRadioButton = 0x2D, RoleText = 0x2A, RoleStaticText = 0x29, RoleComboBox = 0x2E,
        RoleList = 0x21, RoleListItem = 0x22, RoleOutline = 0x23, RoleOutlineItem = 0x24, RoleTable = 0x18, RoleCell = 0x1D, RolePageTab = 0x25, RolePageTabList = 0x3C, RoleWindow = 0x09,
        RoleClient = 0x0A, RolePane = 0x10, RoleGrouping = 0x14, RoleDocument = 0x0F, RoleMenuBar = 0x02, RoleMenuPopup = 0x0B, RoleToolbar = 0x16, RoleLink = 0x1E, RoleSpinButton = 0x34,
        RoleTitleBar = 0x01, RoleScrollBar = 0x03, RoleSplitButton = 0x3E, RoleButtonDropDown = 0x38, RoleButtonMenu = 0x39, RoleGraphic = 0x28, RoleColumnHeader = 0x19, RoleRowHeader = 0x1A;
    public const int StatePressed = 0x8, StateInvisible = 0x8000, StateChecked = 0x10, StateFocused = 0x4;

    public sealed record Named(string Name, int Role, string RoleName, int State);

    /// <summary>Name and role of the object a WinEvent was raised for. Null when it cannot be read.</summary>
    public static Named? FromEvent(IntPtr hwnd, int idObject, int idChild)
    {
        try
        {
            if (AccessibleObjectFromEvent(hwnd, unchecked((uint)idObject), unchecked((uint)idChild), out var acc, out var child) != 0 || acc is null) return null;
            var c = child ?? ChildSelf;
            string name = ""; int role = 0; int state = 0;
            try { name = acc.get_accName(c) as string ?? ""; } catch { }
            try { role = RoleOf(acc.get_accRole(c)); } catch { }
            try { state = StateOf(acc.get_accState(c)); } catch { }
            return new Named(name.Trim(), role, RoleName(role), state);
        }
        catch { return null; }
    }

    private static int RoleOf(object? r) => r is int i ? i : r is uint u ? (int)u : 0;
    private static int StateOf(object? s) => s is int i ? i : s is uint u ? (int)u : 0;

    public static string RoleName(int role)
    {
        var sb = new StringBuilder(64);
        return GetRoleText((uint)role, sb, 64) > 0 ? sb.ToString() : $"role {role}";
    }

    /// <summary>UIA-style control type for an MSAA role, so screens from both paths look the same.</summary>
    public static string ControlType(int role) => role switch
    {
        RolePushButton or RoleButtonDropDown or RoleButtonMenu => "Button",
        RoleSplitButton => "SplitButton",
        RoleMenuItem => "MenuItem",
        RoleCheckButton => "CheckBox",
        RoleRadioButton => "RadioButton",
        RoleText => "Edit",
        RoleStaticText => "Text",
        RoleComboBox => "ComboBox",
        RoleList => "List",
        RoleListItem => "ListItem",
        RoleOutline => "Tree",
        RoleOutlineItem => "TreeItem",
        RoleTable => "Table",
        RoleCell => "DataItem",
        RoleColumnHeader or RoleRowHeader => "HeaderItem",
        RolePageTab => "TabItem",
        RolePageTabList => "Tab",
        RoleWindow => "Window",
        RoleDocument => "Document",
        RoleMenuBar => "MenuBar",
        RoleMenuPopup => "Menu",
        RoleToolbar => "ToolBar",
        RoleLink => "Hyperlink",
        RoleSpinButton => "Spinner",
        RoleTitleBar => "TitleBar",
        RoleScrollBar => "ScrollBar",
        RoleGraphic => "Image",
        RoleClient or RolePane or RoleGrouping => "Pane",
        _ => "Custom",
    };

    /// <summary>Walks a window's IAccessible tree into control rows (bounded). Names only; no automation ids exist in MSAA.</summary>
    public static List<ControlRow> Dump(IntPtr hwnd, int maxRows, int maxDepth)
    {
        var rows = new List<ControlRow>();
        try
        {
            if (AccessibleObjectFromWindow(hwnd, unchecked((uint)Win32.OBJID_WINDOW), ref IidIAccessible, out var acc) != 0 || acc is null) return rows;
            Walk(acc, ChildSelf, 0, maxDepth, rows, maxRows);
        }
        catch { }
        return rows;
    }

    private static void Walk(IAccessible acc, object child, int depth, int maxDepth, List<ControlRow> rows, int max)
    {
        if (depth > maxDepth || rows.Count >= max) return;
        string name = ""; int role = 0; int state = 0; var pos = "";
        try { name = (acc.get_accName(child) as string ?? "").Replace("|", "/").Trim(); } catch { }
        try { role = RoleOf(acc.get_accRole(child)); } catch { }
        try { state = StateOf(acc.get_accState(child)); } catch { }
        if ((state & StateInvisible) != 0 && depth > 0) return;
        try { acc.accLocation(out var l, out var t, out var w, out var h, child); pos = $"{l},{t} {w}x{h}"; } catch { }
        if (role is RoleTitleBar or RoleScrollBar) return;
        rows.Add(new ControlRow { Depth = depth, Type = ControlType(role), Name = name.Length > 120 ? name[..120] : name, AutomationId = "", Position = pos, IsPassword = false });
        if (child is int i && i != ChildSelf) return; // a simple element has no children of its own
        int count;
        try { count = acc.accChildCount; } catch { return; }
        if (count <= 0) return;
        var children = new object[Math.Min(count, 500)];
        int got;
        try { if (AccessibleChildren(acc, 0, children.Length, children, out got) != 0) return; } catch { return; }
        for (var k = 0; k < got && rows.Count < max; k++)
        {
            var c = children[k];
            if (c is IAccessible childAcc) Walk(childAcc, ChildSelf, depth + 1, maxDepth, rows, max);
            else if (c is int id) Walk(acc, id, depth + 1, maxDepth, rows, max);
        }
    }
}
