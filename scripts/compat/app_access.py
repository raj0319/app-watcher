"""Microsoft Access as a small-business "forms over a database" program, for the compatibility test.

build_db() makes a fresh .accdb with a hidden, automated Access (never visible, never in front): two tables and two forms
(Customer Entry, Order Entry). No macros and no VBA at all: Access blocks active content in files outside a trusted folder
on many PCs (this one does), and the Trust Center is the user's to change. The drive opens the forms from the navigation
pane and closes them with Ctrl+F4, as many Access users work.

ACCESS = dict(prepare, start, drive) at the bottom is the entry the compatibility test uses. The drive acts only through
d.click / d.type / d.keys / d.log, which act only while Access (the instance it started) is in front.
"""
import datetime as dt, os, re, subprocess, threading, time
import compat_test as ct

ACCESS_EXE = r"C:\Program Files\Microsoft Office\root\Office16\MSACCESS.EXE"
APP_TITLE = "Compat Orders"

# Access constants (no type library needed)
AC_FORM, AC_MACRO = 2, 4
AC_DETAIL = 0
AC_LABEL, AC_BUTTON, AC_TEXTBOX, AC_COMBO = 100, 104, 109, 111
AC_SAVE_YES = 1
DB_BOOLEAN, DB_TEXT = 1, 10
TWIPS = 1440  # one inch

SAMPLE_CUSTOMERS = [("TPC01", "Test Supply Co", "Springfield"),
                    ("GHW02", "Green Hill Wholesale", "Riverton"),
                    ("RVS03", "River Valley Supplies", "Lakeside")]
ORDER_CODE = "GHW02"            # the existing customer chosen on Order Entry
ORDER_NAME = "Green Hill Wholesale"   # what Customer Name then shows by itself


def _exe():
    for p in (ACCESS_EXE, ACCESS_EXE.replace("Program Files", "Program Files (x86)")):
        if os.path.exists(p): return p
    raise SystemExit("MSACCESS.EXE not found")


# ---------------------------------------------------------------- a hidden Access of our own

def _pid_of_hwnd(hwnd):
    import ctypes
    from ctypes import wintypes as w
    p = w.DWORD(); ctypes.windll.user32.GetWindowThreadProcessId(w.HWND(hwnd), ctypes.byref(p))
    return p.value


def _kill(pid):
    if pid: subprocess.run(["taskkill", "/f", "/pid", str(pid)], capture_output=True)


def _pid_alive(pid):
    out = subprocess.run(["tasklist", "/fi", f"PID eq {pid}", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    return f'"{pid}"' in out


class HiddenAccess:
    """A new Access process driven by COM, kept hidden (Visible False, UserControl False). A watchdog kills that one
    process (and only it) when a step hangs, e.g. on a modal message nobody can see."""

    def __init__(self, timeout=180):
        import pythoncom, win32com.client
        pythoncom.CoInitialize()
        self.app = win32com.client.DispatchEx("Access.Application")   # always a new process
        self.app.Visible = False
        try: self.app.UserControl = False
        except Exception: pass
        self.hwnd = self.app.hWndAccessApp()
        self.pid = _pid_of_hwnd(self.hwnd)
        self._dog = threading.Timer(timeout, lambda: _kill(self.pid)); self._dog.daemon = True; self._dog.start()

    def hidden(self, where=""):
        """Stop at once (killing this Access) if its window ever shows: nothing may appear on the desktop."""
        import ctypes
        if ctypes.windll.user32.IsWindowVisible(self.hwnd):
            _kill(self.pid)
            raise RuntimeError(f"the hidden Access became visible ({where}): stopped")

    def quit(self):
        self._dog.cancel()
        app, self.app = self.app, None
        if app is not None:
            t = threading.Thread(target=lambda: self._quiet(app), daemon=True); t.start(); t.join(15)
        end = time.time() + 15
        while self.pid and _pid_alive(self.pid) and time.time() < end: time.sleep(0.5)
        if self.pid and _pid_alive(self.pid): _kill(self.pid)   # only the PID this object started

    @staticmethod
    def _quiet(app):
        try: app.CloseCurrentDatabase()
        except Exception: pass
        try: app.Quit(2)   # acQuitSaveNone: every object was saved explicitly
        except Exception: pass


# ---------------------------------------------------------------- building the database

def _prop(db, name, typ, value):
    try: db.Properties(name).Value = value
    except Exception: db.Properties.Append(db.CreateProperty(name, typ, value))


def _macro(app, name, actions, folder):
    """A named macro from the classic text format (Action + Arguments), loaded with LoadFromText."""
    lines = ["Version =196611", "ColumnsShown =0"]
    for action, args in actions:
        lines += ["Begin", f'    Action ="{action}"'] + [f'    Argument ="{a}"' for a in args] + ["End"]
    path = os.path.join(folder, f"{name}.txt")
    with open(path, "w", encoding="mbcs", newline="\r\n") as f: f.write("\n".join(lines) + "\n")
    app.LoadFromText(AC_MACRO, name, path)


def _open_form_macro(form):   # OpenForm: name, view Form, no filter, no where, data mode as set, window mode Normal
    return [("OpenForm", [form, "0", "", "", "-1", "0"])]


def _close_form_macro(form):  # Close: object type Form, the form's name, design changes not saved (never a prompt)
    return [("Close", ["2", form, "2"])]


def _new_form(app, record_source, caption, data_entry):
    frm = app.CreateForm()
    frm.Caption = caption
    if record_source:
        frm.RecordSource = record_source
        frm.DataEntry = data_entry       # opens on a new record: typing never changes an existing one
        frm.AllowAdditions = True
    else:
        frm.RecordSelectors = False; frm.NavigationButtons = False; frm.DividingLines = False; frm.ScrollBars = 0
    frm.AutoCenter = True
    return frm


def _field(app, frm, ctype, name, label, top, source=None, width=3.0, tab=None, **props):
    """A text box or combo box with an attached label on its left (the label is what a person reads)."""
    left = int(1.6 * TWIPS)
    column = source if source and not source.startswith("=") else ""
    c = app.CreateControl(frm.Name, ctype, AC_DETAIL, "", column, left, top, int(width * TWIPS), int(0.25 * TWIPS))
    c.Name = name
    if source and source.startswith("="): c.ControlSource = source
    for k, v in props.items(): setattr(c, k, v)
    if tab is not None: c.TabIndex = tab
    lab = app.CreateControl(frm.Name, AC_LABEL, AC_DETAIL, c.Name, "", int(0.2 * TWIPS), top, int(1.3 * TWIPS), int(0.25 * TWIPS))
    lab.Caption = label
    lab.Name = f"lbl {name}"
    return c


def _button(app, frm, name, caption, macro, left, top, width=1.4):
    b = app.CreateControl(frm.Name, AC_BUTTON, AC_DETAIL, "", "", int(left * TWIPS), top, int(width * TWIPS), int(0.35 * TWIPS))
    b.Name = name; b.Caption = caption; b.OnClick = macro
    b.TabStop = False   # Tab from the last field moves to the next record (which saves it), never onto the button
    return b


def _make_form(app, builder, name, acc=None):
    """Build a form with builder(app) -> its temporary name (Form1...), then save it and give it its name. The builder keeps
    every reference to the form and its controls local: a reference to a form that was closed makes a hidden Access fall
    over on the next CreateForm."""
    import gc
    tmp = builder(app)
    gc.collect()
    if acc: acc.hidden(name)
    app.DoCmd.Close(AC_FORM, tmp, AC_SAVE_YES)
    app.DoCmd.Rename(name, AC_FORM, tmp)


ROW = int(0.45 * TWIPS)


def _customer_form(app):
    # Customer Entry: Code (validated by the table), Customer Name, City
    frm = _new_form(app, "Customers", "Customer Entry", True)
    _field(app, frm, AC_TEXTBOX, "Code", "Code", 1 * ROW, "Code", width=1.2, tab=0)
    _field(app, frm, AC_TEXTBOX, "Customer Name", "Customer Name", 2 * ROW, "CustomerName", tab=1)
    _field(app, frm, AC_TEXTBOX, "City", "City", 3 * ROW, "City", width=2.0, tab=2)
    frm.Width = int(5.0 * TWIPS)
    return frm.Name


def _order_form(app):
    # Order Entry: Customer Code (a combo of the customers), Customer Name filled by the program,
    # Quantity, Unit Price, Line Total calculated by the program
    frm = _new_form(app, "Orders", "Order Entry", True)
    _field(app, frm, AC_COMBO, "Customer Code", "Customer Code", 1 * ROW, "CustomerCode", width=1.5, tab=0,
           RowSourceType="Table/Query", RowSource="SELECT Code, CustomerName FROM Customers ORDER BY Code;",
           ColumnCount=2, BoundColumn=1, ColumnWidths="1080;2880", ListWidth=int(2.8 * TWIPS), LimitToList=True)
    _field(app, frm, AC_TEXTBOX, "Customer Name", "Customer Name", 2 * ROW, "=[Customer Code].[Column](1)",
           Locked=True, TabStop=False)
    _field(app, frm, AC_TEXTBOX, "Quantity", "Quantity", 3 * ROW, "Quantity", width=1.2, tab=1)
    _field(app, frm, AC_TEXTBOX, "Unit Price", "Unit Price", 4 * ROW, "UnitPrice", width=1.2, tab=2)
    # (a tab stop, so Tab from Unit Price shows the total on this record before the next Tab moves on)
    _field(app, frm, AC_TEXTBOX, "Line Total", "Line Total", 5 * ROW, "=[Quantity]*[Unit Price]", width=1.2, tab=3,
           Locked=True, Format="Currency")
    frm.Width = int(5.0 * TWIPS)
    return frm.Name


def _menu_form(app):
    # Main Menu: unbound, a title and two buttons; the start-up form
    frm = _new_form(app, None, "Main Menu", False)
    t = app.CreateControl(frm.Name, AC_LABEL, AC_DETAIL, "", "", int(0.3 * TWIPS), int(0.2 * TWIPS), int(3 * TWIPS), int(0.35 * TWIPS))
    t.Caption = APP_TITLE; t.Name = "lblTitle"; t.FontSize = 14; t.FontBold = True
    _button(app, frm, "Customers", "Customers", "mcrOpenCustomers", 0.3, int(0.8 * TWIPS), width=1.8)
    _button(app, frm, "Orders", "Orders", "mcrOpenOrders", 0.3, int(1.4 * TWIPS), width=1.8)
    frm.Width = int(3.5 * TWIPS)
    return frm.Name


def build_db(path):
    """Create a fresh .accdb at path (an old file there is replaced) with a hidden Access, then reopen it hidden to check."""
    path = os.path.abspath(path)
    for p in (path, path[:-6] + ".laccdb"):
        if os.path.exists(p): os.remove(p)
    folder = os.path.join(os.path.dirname(path), "access-build"); os.makedirs(folder, exist_ok=True)
    acc = HiddenAccess()
    try:
        app = acc.app
        app.NewCurrentDatabase(path)
        acc.hidden("new database")
        db = app.CurrentDb()
        db.Execute("CREATE TABLE Customers (ID COUNTER CONSTRAINT PK_Customers PRIMARY KEY, Code TEXT(10), "
                   "CustomerName TEXT(50), City TEXT(30))")
        db.Execute("CREATE TABLE Orders (ID COUNTER CONSTRAINT PK_Orders PRIMARY KEY, CustomerCode TEXT(10), "
                   "Quantity DOUBLE, UnitPrice CURRENCY)")
        db.TableDefs.Refresh()
        code = db.TableDefs("Customers").Fields("Code")
        code.ValidationRule = "Len([Code])>=3"
        code.ValidationText = "Customer code must have at least 3 characters."
        del code
        for c, n, city in SAMPLE_CUSTOMERS:
            db.Execute(f"INSERT INTO Customers (Code, CustomerName, City) VALUES ('{c}', '{n}', '{city}')")

        # no macros and no VBA at all: Access blocks active content in files outside a trusted folder on many PCs (this one
        # does), and changing the Trust Center is the user's call; the forms are opened from the navigation pane instead

        _make_form(app, _customer_form, "Customer Entry", acc)
        _make_form(app, _order_form, "Order Entry", acc)

        db = app.CurrentDb()
        _prop(db, "AppTitle", DB_TEXT, APP_TITLE)
        app.CloseCurrentDatabase()
    finally:
        acc.quit()
    _check_db(path)
    return path


def _check_db(path):
    """Reopen the file read-only through DAO in a hidden Access and list what is in it (raises when something is missing).
    Never OpenCurrentDatabase: that runs the start-up form, which makes Access visible."""
    acc = HiddenAccess(timeout=90)
    try:
        db = acc.app.DBEngine.OpenDatabase(path, False, True)
        rs = db.OpenRecordset("SELECT Name, Type FROM MSysObjects WHERE Type IN (-32768, -32766)")
        objs = []
        while not rs.EOF:
            objs.append((rs.Fields("Name").Value, rs.Fields("Type").Value)); rs.MoveNext()
        rs.Close()
        rs = db.OpenRecordset("SELECT COUNT(*) FROM Customers"); rows = rs.Fields(0).Value; rs.Close()
        tables = sorted(t.Name for t in db.TableDefs if not t.Name.startswith("MSys") and not t.Name.startswith("~"))
        startup = "(none)"
        rule = db.TableDefs("Customers").Fields("Code").ValidationRule.rstrip("\x00")
        db.Close(); del rs, db
    finally:
        acc.quit()
    forms = sorted(n for n, t in objs if t == -32768)
    macros = sorted(n for n, t in objs if t == -32766)
    if forms != ["Customer Entry", "Order Entry"] or tables != ["Customers", "Orders"] or rows != 3 or macros or not rule:
        raise RuntimeError(f"the test database is not right: forms {forms}, tables {tables}, rows {rows}, "
                           f"startup {startup}, macros {macros}, rule {rule!r}")
    return dict(forms=forms, tables=tables, macros=macros, customers=rows, startup=startup, rule=rule)


# ---------------------------------------------------------------- prepare / start

_DB_PATH = ""


def access_prepare():
    # a new file name each run: Access remembers a file whose last session was ended by force (the test kills its own
    # Access at the end) and asks about it on the next open
    global _DB_PATH
    _DB_PATH = ct.scratch(f"compat-orders-{time.strftime('%H%M%S')}.accdb")
    build_db(_DB_PATH)
    return _exe(), "office", [{"kind": "access", "path": _DB_PATH, "label": "compat-orders.accdb", "enabled": True}]


def access_start():
    p = subprocess.Popen([_exe(), _DB_PATH])
    win = ct.wait_window(p.pid, rf"{APP_TITLE}|compat-orders|^(Microsoft )?Access$", timeout=60)
    time.sleep(4)   # (the navigation pane fills after the main window)
    return p, {p.pid}, win


# ---------------------------------------------------------------- finding things in Access (UI Automation names vary)

def _visible(e):
    try: return e.is_visible()
    except Exception: return False


def _all(win, control_type=None):
    for attempt in range(4):   # (a busy program can fail one UI Automation call)
        try: return [e for e in (win.descendants(control_type=control_type) if control_type else win.descendants()) if _visible(e)]
        except Exception:
            if attempt == 3: raise
            time.sleep(1.0)


def _text(e):
    try: return (e.window_text() or "").strip()
    except Exception: return ""


# Access names a form's control after its attached label (a button after its caption), but the control that has the focus
# carries its section in front ("Detail Section, Code") and a combo box its state behind ("Customer Code collapsed")
# (read from the forms' MSAA tree in a hidden Access, 2026-11-25)
_SECTION = re.compile(r"^(detail|form header|form footer|page header|page footer) section,\s*", re.I)
_STATE = re.compile(r"\s+(collapsed|expanded)$", re.I)


def _label_of(e):
    return ct.norm(_STATE.sub("", _SECTION.sub("", _text(e))))


def _form(win, caption, timeout=10):
    """The open form's document (a tab or window named after its caption); the main window when none is found."""
    end = time.time() + timeout
    while time.time() < end:
        for e in _all(win):
            try: ct_ = e.element_info.control_type
            except Exception: continue
            if ct_ in ("Window", "Pane", "Document", "Custom") and re.fullmatch(re.escape(caption), _text(e), re.I):
                return e
        time.sleep(0.5)
    return win


def _button_named(scope, caption, timeout=10):
    """A visible button whose name is the caption (the focused one carries its section in front: see _label_of)."""
    want = ct.norm(caption)
    end = time.time() + timeout
    while time.time() < end:
        for b in _all(scope, "Button"):
            if _label_of(b) == want: return b
        time.sleep(0.5)
    raise RuntimeError(f"no button {caption!r}")


def _box(scope, label, timeout=10):
    """The field a label names. Access names a text box or combo box after its attached label (see _label_of); when it
    does not, the field is the edit box level with the label, to its right."""
    want = ct.norm(label)
    end = time.time() + timeout
    while time.time() < end:
        fields = [e for t in ("Edit", "ComboBox") for e in _all(scope, t)]
        for e in fields:
            if _label_of(e) == want: return e
        for lab in _all(scope, "Text"):
            if _label_of(lab) != want: continue
            lr = lab.rectangle(); mid = (lr.top + lr.bottom) // 2
            row = [e for e in fields if e.rectangle().top - 4 <= mid <= e.rectangle().bottom + 4 and e.rectangle().left >= lr.right - 4]
            if row: return min(row, key=lambda e: e.rectangle().left)
        time.sleep(0.5)
    seen = [(e.element_info.control_type, _text(e)) for t in ("Edit", "ComboBox") for e in _all(scope, t)]
    raise RuntimeError(f"no field labelled {label!r}; fields seen: {seen[:20]}")


def _message_box(win, pid, timeout=8):
    """Access's own message: a #32770 dialog (or any other window of the process with an OK button), top-level or shown by
    UI Automation as a Window inside the main one. Its title is the AppTitle or "Microsoft Access": never matched on."""
    from pywinauto import Desktop
    def has_ok(w):
        try: return any(ct.norm(_text(b)) == "ok" for b in w.descendants(control_type="Button"))
        except Exception: return False
    main = win.handle if hasattr(win, "handle") else None
    end = time.time() + timeout
    while time.time() < end:
        try:
            for top in Desktop(backend="uia").windows(process=pid, visible_only=True):
                if top.handle == main: continue
                w = Desktop(backend="uia").window(handle=top.handle).wrapper_object()
                if top.element_info.class_name == "#32770" or has_ok(w): return w
        except Exception: pass
        try:
            for e in win.children(control_type="Window"):
                if _visible(e) and (e.element_info.class_name == "#32770" or has_ok(e)): return e
        except Exception: pass
        time.sleep(0.3)
    raise RuntimeError("no message from Access")


def _now():
    return dt.datetime.now().isoformat(timespec="milliseconds")


# ---------------------------------------------------------------- the drive

def _open_from_pane(d, win, form):
    """Open a form from the navigation pane: a click on its name (scored as a press of that name), then Enter."""
    end = time.time() + 15; item = None
    while time.time() < end and item is None:
        for e in _all(win):
            try: ct_ = e.element_info.control_type
            except Exception: continue
            if ct_ in ("ListItem", "TreeItem", "DataItem", "Button") and ct.norm(_text(e)) == ct.norm(form): item = e; break
        if item is None: time.sleep(0.5)
    if item is None: raise RuntimeError(f"no {form!r} in the navigation pane")
    d.click(win, item, kind="press", name=form, settle=0.6)
    d.keys(win, "{ENTER}", settle=2.0)
    d.log("open", form); d.steps[-1]["t"] = d.steps[-2]["t"]


def access_drive(d, win, pids):
    pid = list(pids)[0]

    # the navigation pane -> Customer Entry (a click on its name, then Enter opens it)
    _open_from_pane(d, win, "Customer Entry")
    cust = _form(win, "Customer Entry")

    # a new customer (the form opens on a new record). Code "AB" breaks the table's rule: Access says so when the
    # field is left, in a message with OK.
    d.click(win, _box(cust, "Code"), kind="focus", name="Code", settle=0.5)
    d.type(win, "AB", name="Code", end="")
    t0 = _now()
    d.keys(win, "{TAB}", settle=1.5)
    msg = _message_box(win, pid)
    d.log("dialog", _text(msg) or "Microsoft Access"); d.steps[-1]["t"] = t0   # (the step is when Tab raised it)
    ok = [b for b in _all(msg, "Button") if ct.norm(_text(b)) == "ok"]
    if ok: d.click(win, ok[0], name="OK", settle=1.2)
    else: d.keys(win, "{ENTER}", settle=1.2)        # (no button found: OK is the default button)
    # back into Code (still "AB"): select its text and type a valid code
    d.click(win, _box(cust, "Code"), kind="focus", name="Code", settle=0.5)
    d.keys(win, "{HOME}+{END}", settle=0.3)
    d.type(win, "ABC12", name="Code", settle=1.0)
    d.type(win, "Order Test Supply", name="Customer Name", settle=1.0)
    # Tab from City (the last stop) moves to a new record, which saves this one (our own test database)
    d.type(win, "Albany", name="City", settle=1.5)
    d.keys(win, "^{F4}", settle=1.5)                  # close the form (Access saves the record; no prompt)

    _open_from_pane(d, win, "Order Entry")
    orders = _form(win, "Order Entry")

    # (Customer Name and Line Total have no step anywhere: the program fills them, the watcher should see them as fills)
    # first order: an existing customer's code (Customer Name is filled by the program, never typed),
    # Quantity 5, Unit Price 20 (Line Total 100 is calculated by the program); Tab from Unit Price lands on Line Total
    d.click(win, _box(orders, "Customer Code"), kind="focus", name="Customer Code", settle=0.5)
    d.type(win, ORDER_CODE, name="Customer Code", settle=1.5)
    d.type(win, "5", name="Quantity", settle=1.0)
    d.type(win, "20", name="Unit Price", settle=1.5)
    # the next record (Tab from Line Total, the last stop), which saves this one
    d.keys(win, "{TAB}", settle=1.5)
    # second order: Quantity 3, Unit Price 7.5 (Line Total 22.5 by the program)
    d.click(win, _box(orders, "Quantity"), kind="focus", name="Quantity", settle=0.5)
    d.type(win, "3", name="Quantity", settle=1.0)
    d.type(win, "7.5", name="Unit Price", settle=1.5)
    # closing the form saves the record being edited (Access does on close) and never asks about design changes
    d.keys(win, "^{F4}", settle=1.5)


ACCESS = dict(prepare=access_prepare, start=access_start, drive=access_drive)
