"""The programs the compatibility test drives. Each: prepare() -> (exe, profile, data sources), start() -> (process, pids,
main window), drive(d, win, pids). Every drive acts only on its own instance, never saves, runs or sends anything."""
import os, subprocess, time
import compat_test as ct


def _office(exe):
    for base in (r"C:\Program Files\Microsoft Office\root\Office16", r"C:\Program Files (x86)\Microsoft Office\root\Office16"):
        p = os.path.join(base, exe)
        if os.path.exists(p): return p
    raise SystemExit(f"{exe} not found")


# ---------------------------------------------------------------- Excel: a small order sheet, formulas recalculated

def excel_prepare():
    import openpyxl
    path = ct.scratch("compat-orders.xlsx")
    wb = openpyxl.Workbook(); wb.active.title = "Orders"; wb.save(path)
    return _office("EXCEL.EXE"), None, None

def excel_start():
    p = subprocess.Popen([_office("EXCEL.EXE"), "/x", ct.scratch("compat-orders.xlsx")])
    win = ct.wait_window(p.pid, r"compat-orders")
    time.sleep(3)
    return p, {p.pid}, win

def excel_drive(d, win, pids):
    # (each step names its cell: a formula cell shows its result, so it is scored as "a typed value in that cell")
    d.keys(win, "^{HOME}")
    for c, v in (("A1", "Quantity"), ("B1", "Unit price")): d.type(win, v, name=c)
    d.type(win, "Line total", name="C1", end="{ENTER}")
    rows = [("5", "20"), ("3", "7.5"), ("12", "4"), ("2", "100")]
    for i, (q, pr) in enumerate(rows, 2):
        d.type(win, q, name=f"A{i}"); d.type(win, pr, name=f"B{i}")
        d.type(win, f"=A{i}*B{i}", name=f"C{i}", end="{ENTER}", formula=True)
    # change the quantities: Excel recalculates Line total by itself (a program fill)
    d.keys(win, "^{HOME}{DOWN}")
    for i, q in enumerate(("6", "4", "10", "3"), 2): d.type(win, q, name=f"A{i}", end="{ENTER}")
    # the ribbon: tabs and a button
    for tab in ("Formulas", "Data", "Home"):
        d.click(win, ct.first(win, title=tab, control_type="TabItem"), kind="tab", name=tab)
    d.click(win, ct.first(win, title="Bold", control_type="Button"), name="Bold")
    # a dialog, closed with Cancel
    d.keys(win, "^1", kind="dialog", name="Format Cells", settle=2.0)
    dlg = ct.dialog(win, list(pids)[0], r"^Format Cells")
    d.click(dlg, ct.first(dlg, title="Cancel", control_type="Button"), name="Cancel")


# ---------------------------------------------------------------- PowerShell ISE: a real WPF program (never runs a script)

def ise_prepare():
    return os.path.expandvars(r"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell_ise.exe"), None, None

def ise_start():
    p = subprocess.Popen([ise_prepare()[0], "-NoProfile"])
    win = ct.wait_window(p.pid, r"Powershell")
    time.sleep(4)
    return p, {p.pid}, win

def ise_drive(d, win, pids):
    pid = list(pids)[0]
    def editor():
        eds = [e for e in win.descendants(control_type="Edit") if e.element_info.name == "Text Editor" and e.is_visible()]
        return eds[0]
    d.keys(win, "^n", settle=1.5)                      # a new script tab (Untitled1.ps1)
    d.click(win, editor(), kind="focus", name="Text Editor")   # the new tab does not take the keyboard focus by itself
    d.type(win, "# a comment only, never run", name="editor", end="{ENTER}")
    d.keys(win, "^n", settle=1.5)                      # a second tab
    d.click(win, editor(), kind="focus", name="Text Editor")
    d.type(win, "# second file", name="editor", end="{ENTER}")
    tabs = [t for t in win.descendants(control_type="TabItem") if t.window_text().startswith("Untitled")]
    if tabs:
        d.click(win, tabs[0], kind="tab", name=tabs[0].window_text())
    # menus: View, then Tools > Options (a dialog), closed with Cancel
    # (opening a menu is not a choice: kind "menuopen" is not scored)
    d.click(win, ct.first(win, title="View", control_type="MenuItem"), kind="menuopen", name="View")
    d.keys(win, "{ESC}", settle=0.8)
    d.click(win, ct.first(win, title="Tools", control_type="MenuItem"), kind="menuopen", name="Tools")
    opt = ct.first(win, title_re="Options", control_type="MenuItem")
    d.click(win, opt, kind="menu", name="Options", settle=0.1)
    d.log("dialog", "Options"); time.sleep(2.5)
    dlg = ct.dialog(win, pid, r"^Options")
    d.click(dlg, ct.first(dlg, title="Cancel", control_type="Button"), name="Cancel")
    # the Edit menu by its access key, then Select All chosen with the mouse (an item that is enabled)
    d.keys(win, "%e", settle=1.0)
    d.click(win, ct.first(win, title_re="^Select All", control_type="MenuItem"), kind="menu", name="Select All")


# ---------------------------------------------------------------- Character Map: a classic Win32 dialog program

def charmap_prepare():
    return r"C:\Windows\System32\charmap.exe", None, None

def charmap_start():
    p = subprocess.Popen([charmap_prepare()[0]])
    win = ct.wait_window(p.pid, r"Character Map")
    time.sleep(2)
    return p, {p.pid}, win

def charmap_drive(d, win, pids):
    box = ct.first(win, auto_id="104", control_type="Edit")
    d.click(win, box, kind="focus", name="Characters to copy")
    d.type(win, "Order 1042", name="Characters to copy")
    d.click(win, ct.first(win, title="Select", control_type="Button"), name="Select")      # adds the selected character (a fill)
    d.click(win, ct.first(win, title="Advanced view", control_type="CheckBox"), name="Advanced view", settle=1.5)
    srch = ct.first(win, title_re="Search for", control_type="Edit")
    d.click(win, srch, kind="focus", name="Search for")
    d.type(win, "dollar", name="Search for", end="")
    d.click(win, ct.first(win, title="Search", control_type="Button"), name="Search", settle=1.5)
    d.click(win, ct.first(win, title="Advanced view", control_type="CheckBox"), name="Advanced view", settle=1.5)
    d.click(win, box, kind="focus", name="Characters to copy")
    d.keys(win, "^a", settle=0.3)
    d.type(win, "INV-7", name="Characters to copy", end="{TAB}")


# ---------------------------------------------------------------- Advanced IP Scanner: a Qt program (never scans)

def ipscan_prepare():
    return r"C:\Program Files (x86)\Advanced IP Scanner\advanced_ip_scanner.exe", None, None

def ipscan_start():
    p = subprocess.Popen([ipscan_prepare()[0]])
    win = ct.wait_window(p.pid, r"Advanced IP Scanner")
    time.sleep(4)
    return p, {p.pid}, win

def ipscan_drive(d, win, pids):
    pid = list(pids)[0]
    d.click(win, ct.first(win, title="Favorites", control_type="TabItem"), kind="tab", name="Favorites")
    d.click(win, ct.first(win, title="Results", control_type="TabItem"), kind="tab", name="Results")
    wr = win.rectangle()
    rng = [e for e in win.descendants(control_type="Edit") if e.is_visible() and wr.left <= e.rectangle().left and e.rectangle().right <= wr.right and wr.top <= e.rectangle().top and e.rectangle().bottom <= wr.bottom and e.rectangle().width() > 20][0]
    d.click(win, rng, kind="focus", name="range")
    d.keys(win, "^a", settle=0.3)
    d.type(win, "10.0.0.1-10.0.0.20", name="range", end="{TAB}")
    d.click(win, ct.first(win, title="My IP", control_type="Button"), name="My IP")         # the program fills the range
    d.click(win, ct.first(win, title_re="^Settings", control_type="MenuItem"), kind="menuopen", name="Settings")
    d.click(win, ct.popup_item(win, pid, r"^Options"), kind="menu", name="Options", settle=0.1)
    d.log("dialog", "Options"); time.sleep(2.5)
    dlg = ct.dialog(win, pid, r"Options")
    d.click(dlg, ct.first(dlg, title="Cancel", control_type="Button"), name="Cancel")
    d.click(win, ct.first(win, title_re="^Help", control_type="MenuItem"), kind="menuopen", name="Help")
    d.click(win, ct.popup_item(win, pid, r"^About"), kind="menu", name="About", settle=0.1)
    d.log("dialog", "About"); time.sleep(2.5)
    dlg = ct.dialog(win, pid, r"About")
    btn = [b for b in dlg.descendants(control_type="Button") if b.window_text().replace("&", "").startswith("OK")]
    if btn: d.click(dlg, btn[0], name=btn[0].window_text())


from app_access import ACCESS  # noqa: E402

APPS = {
    "access": ACCESS,
    "excel": dict(prepare=excel_prepare, start=excel_start, drive=excel_drive),
    "ise": dict(prepare=ise_prepare, start=ise_start, drive=ise_drive),
    "charmap": dict(prepare=charmap_prepare, start=charmap_start, drive=charmap_drive),
    "ipscan": dict(prepare=ipscan_prepare, start=ipscan_start, drive=ipscan_drive),
}
