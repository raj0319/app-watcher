"""
Compatibility test of App Watcher on a real Windows program (not one of ours).

Starts the watcher in portable mode, starts ITS OWN instance of the program, drives that one window with the real mouse
and keyboard (only while it is in front; never Save, Run, Delete or a network action), kills the program it started,
learns, and scores what the watcher recorded against what was done:

  presses   a button or menu item clicked  -> an invoke/menu event with that name
  typed     a value typed into a field      -> a value event (not by the program) with that value
  tabs      a tab clicked                   -> a select event with that name
  dialogs   a dialog opened                 -> a dialog or open event
  screens   distinct screens recorded, tree reads failed, presses recorded that no step explains

  python -I scripts\\compat\\compat_test.py excel [--watcher path\\AppWatcher.exe] [--keep]

The scorecard goes to evidence\\compat\\<app>-<time>.md (git-ignored). Apps: see APPS at the bottom.
"""
import argparse, ctypes, datetime as dt, glob, json, os, re, shutil, subprocess, sys, time
from ctypes import wintypes as w

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
u32 = ctypes.windll.user32
u32.GetForegroundWindow.restype = w.HWND

# ---------------------------------------------------------------- watcher

def find_watcher(arg):
    if arg: return os.path.abspath(arg)
    c = glob.glob(os.path.join(ROOT, "src", "AppWatcher", "bin", "Debug", "**", "AppWatcher.exe"), recursive=True)
    if not c: sys.exit("watcher not built: dotnet build")
    return max(c, key=os.path.getmtime)

class Watcher:
    def __init__(self, exe):
        self.exe = exe
        self.data = os.path.join(os.path.dirname(exe), "AppWatcherData")
        self.proc = None
    def run(self, *args):
        return subprocess.run([self.exe, "--portable", *args], timeout=600).returncode
    def setup(self, target, profile=None, sources=None):
        if any(p for p in os.popen('tasklist /fi "imagename eq AppWatcher.exe" /fo csv /nh').read().splitlines() if "AppWatcher" in p):
            sys.exit("another App Watcher is running (only one at a time): stop it first")
        shutil.rmtree(self.data, ignore_errors=True)
        code = self.run("--setup", target, "test1234", *([profile] if profile else []))
        if code != 0: sys.exit(f"setup failed ({code})")
        cfg_path = os.path.join(self.data, "config.json")
        cfg = json.load(open(cfg_path, encoding="utf-8-sig"))
        cfg["autoDetectPending"] = False          # never raise the administrator prompt in an unattended test
        if sources: cfg["dataSources"] = sources
        json.dump(cfg, open(cfg_path, "w", encoding="utf-8"), indent=2)
        return cfg
    def start(self):
        self.proc = subprocess.Popen([self.exe, "--portable"])
        time.sleep(2)
    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.kill(); self.proc.wait(10)
    def app_folder(self):
        d = [p for p in glob.glob(os.path.join(self.data, "data", "*")) if os.path.isdir(p)]
        return max(d, key=os.path.getmtime) if d else None

# ---------------------------------------------------------------- driving (real input, own window only)

class Drive:
    def __init__(self, pid_of_window):
        self.pids = set(pid_of_window if isinstance(pid_of_window, (set, list, tuple)) else [pid_of_window])
        self.steps = []
    def owns_point(self, x, y):
        u32.WindowFromPoint.restype = w.HWND
        h = u32.WindowFromPoint(w.POINT(x, y))
        p = w.DWORD(); u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        return p.value in self.pids
    def fg_pid(self):
        p = w.DWORD(); u32.GetWindowThreadProcessId(u32.GetForegroundWindow(), ctypes.byref(p)); return p.value
    def ensure_front(self, win):
        for _ in range(10):
            if self.fg_pid() in self.pids: return
            try: win.set_focus()
            except Exception: pass
            time.sleep(0.4)
        raise RuntimeError("the test program is not in front: stopping (never act on another window)")
    def log(self, kind, name, val=None, **kw):
        self.steps.append(dict(t=dt.datetime.now().isoformat(timespec="milliseconds"), kind=kind, name=name, val=val, **kw))
    def click(self, win, elem, kind="press", name=None, settle=1.0, **kw):
        from pywinauto import mouse
        self.ensure_front(win)
        for attempt in range(4):
            try:
                nm = name if name is not None else elem.window_text()
                r = elem.rectangle(); break
            except Exception:
                if attempt == 3: raise
                time.sleep(1.0)
        x, y = (r.left + r.right) // 2, (r.top + r.bottom) // 2
        # like a person: move, rest a moment, press, hold about a tenth of a second, release
        # never a click outside the test program: the window under the point must be one of its own
        if not self.owns_point(x, y): raise RuntimeError(f"the point {x},{y} of '{nm}' is not on the test program: no click")
        mouse.move(coords=(x, y)); time.sleep(0.15)
        if self.fg_pid() not in self.pids or not self.owns_point(x, y): raise RuntimeError("the test program left the front: no click")
        self.log(kind, nm, **kw)
        mouse.press(coords=(x, y)); time.sleep(0.12); mouse.release(coords=(x, y))
        time.sleep(settle)
    def keys(self, win, keys, kind=None, name=None, val=None, settle=0.8, **kw):
        """Send keys one at a time, each only while the test program is in front. Another program taking the front (a popup of
        another program) pauses the typing and marks the step interrupted (it is not scored): no key ever goes elsewhere."""
        from pywinauto.keyboard import send_keys
        self.ensure_front(win)
        if kind: self.log(kind, name, val, **kw)
        for tok in re.findall(r"[\^%+]*(?:\{[^}]+\}|.)", keys, re.S):
            if self.fg_pid() not in self.pids:
                if self.steps and kind: self.steps[-1]["interrupted"] = True
                print("  another program took the front: waiting, then going on (step marked interrupted)")
                self.ensure_front(win)
            # like a person: about 12 keys a second, and a short pause before the key that ends the value (Tab, Enter)
            if tok in ("{TAB}", "{ENTER}") and kind == "type": time.sleep(0.35)
            send_keys(tok, with_spaces=True, pause=0.0)
            time.sleep(0.08)
        time.sleep(settle)
    def type(self, win, text, name=None, settle=0.8, end="{TAB}", **kw):
        """Type a value (one step), then end it with Tab or Enter."""
        esc = re.sub(r"([+^%~(){}\[\]])", r"{\1}", text)
        self.keys(win, esc + (end or ""), kind="type", name=name, val=text, settle=settle, **kw)

# ---------------------------------------------------------------- scoring

def norm(s):
    return re.sub(r"[^a-z0-9]+", " ", (s or "").replace("&", "").lower()).strip()

def same_value(shown, typed):
    if shown == typed: return True
    num = lambda x: re.sub(r"[^0-9.\-]", "", x)
    try: return num(typed) != "" and float(num(shown)) == float(num(typed)) and re.fullmatch(r"[\s$€£,.\d\-]+", shown) is not None
    except ValueError: return False

def parse_t(s):
    s = re.sub(r"(\.\d{6})\d+", r"\1", s)
    return dt.datetime.fromisoformat(s).replace(tzinfo=None)

def load_events(folder):
    ev = []
    for f in sorted(glob.glob(os.path.join(folder, "events", "*.jsonl"))):
        for i, line in enumerate(open(f, encoding="utf-8"), 1):
            try: e = json.loads(line)
            except Exception: continue
            e["_t"] = parse_t(e["t"]); e["_ref"] = f"{os.path.basename(f)}#L{i}"; ev.append(e)
    return ev

def score(app, steps, folder, out_dir):
    ev = load_events(folder)
    used = set()
    def find(step, pred, before=1.0, after=4.0):
        t = dt.datetime.fromisoformat(step["t"])
        for k, e in enumerate(ev):
            if k in used: continue
            d = (e["_t"] - t).total_seconds()
            if -before <= d <= after and pred(e):
                used.add(k); return e
        return None
    rows = {"press": [0, 0], "menu": [0, 0], "type": [0, 0], "tab": [0, 0], "dialog": [0, 0], "open": [0, 0]}
    misses = []
    interrupted = [s for s in steps if s.get("interrupted")]
    for s in steps:
        k = s["kind"]
        if k not in rows or s.get("interrupted"): continue
        rows[k][1] += 1
        n = norm(s.get("name"))
        if k in ("press", "menu"):
            hit = find(s, lambda e: e["act"] == "invoke" and (norm(e.get("name")) == n or n and n in norm(e.get("name")) or norm(e.get("name")) and norm(e.get("name")) in n))
        elif k == "type":
            v = (s.get("val") or "").strip()
            if s.get("formula"):  # a formula cell shows its result: a typed value recorded in that cell
                hit = find(s, lambda e: e["act"] == "value" and e.get("by") != "app" and e.get("name") == s.get("name"), after=6.0)
            else:  # (the same number as the program shows it counts: "20" typed, "$20.00" shown)
                hit = find(s, lambda e: e["act"] == "value" and e.get("by") != "app" and same_value((e.get("val") or "").strip(), v), after=6.0)
        elif k == "tab":
            hit = find(s, lambda e: e["act"] == "select" and (norm(e.get("name")) == n or norm(e.get("val")) == n))
        elif k == "dialog":
            hit = find(s, lambda e: e["act"] in ("dialog", "open") and (not n or n in norm(e.get("name")) or n in norm(e.get("win"))), after=5.0)
        else:  # open
            hit = find(s, lambda e: e["act"] in ("open", "foreground") and n in norm(e.get("win")), after=6.0)
        if hit: rows[k][0] += 1
        else: misses.append(s)
    t0 = dt.datetime.fromisoformat(steps[0]["t"]) - dt.timedelta(seconds=2) if steps else dt.datetime.min
    t1 = dt.datetime.fromisoformat(steps[-1]["t"]) + dt.timedelta(seconds=8) if steps else dt.datetime.max
    # (a "menu" event is a popup menu that opened, not a press; a tick's own toggle event is its value)
    extra = [e for k, e in enumerate(ev) if k not in used and e["act"] == "invoke" and e.get("how") != "toggle" and t0 <= e["_t"] <= t1]
    status = {}
    p = os.path.join(folder, "collector-status.json")
    if os.path.exists(p): status = json.load(open(p, encoding="utf-8-sig"))
    screens = glob.glob(os.path.join(folder, "screens", "*.md"))
    logic = open(os.path.join(folder, "memory", "logic.md"), encoding="utf-8").read() if os.path.exists(os.path.join(folder, "memory", "logic.md")) else ""
    found = [l.strip() for l in logic.splitlines() if re.search(r"\bfills\b| = ", l) and "seen" in l]
    total = sum(r[1] for r in rows.values()); ok = sum(r[0] for r in rows.values())
    lines = [f"# Compatibility: {app}  ({dt.datetime.now():%Y-%m-%d %H:%M})", "",
             f"**{ok} of {total} steps recorded right** ({(100 * ok // total) if total else 0}%)", "",
             "| Kind | Recorded | Of |", "|---|---|---|"]
    lines += [f"| {k} | {r[0]} | {r[1]} |" for k, r in rows.items() if r[1]]
    lines += ["", f"- screens recorded: {len(screens)}",
              f"- tree reads ok / failed: {status.get('dumpsOk', '?')} / {status.get('dumpsFailed', '?')}",
              f"- presses recorded that no step explains: {len(extra)}",
              f"- steps interrupted by another program in front (not scored): {len(interrupted)}",
              f"- events: {len(ev)}", "", "## Missed steps", ""]
    lines += [f"- {s['t'][11:19]} {s['kind']} '{s.get('name')}'" + (f" = '{s.get('val')}'" if s.get("val") else "") for s in misses] or ["- none"]
    lines += ["", "## Unexplained presses", ""]
    lines += [f"- {e['t'][11:19]} {e['act']} '{e.get('name')}' ({e.get('how', '')}) {e['_ref']}" for e in extra[:30]] or ["- none"]
    lines += ["", "## Learned (logic.md lines with fills or formulas)", ""] + ([f"- {l}" for l in found[:20]] or ["- none"])
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"{app}-{dt.datetime.now():%Y%m%d-%H%M%S}.md")
    open(out, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    json.dump(steps, open(out[:-3] + ".steps.json", "w", encoding="utf-8"), indent=1)
    print("\n".join(lines[:12 + len(rows)]))
    print("scorecard:", out)
    return ok, total

# ---------------------------------------------------------------- helpers for apps

def wait_window(pid, title_re=None, timeout=40, backend="uia"):
    from pywinauto import Desktop
    end = time.time() + timeout
    while time.time() < end:
        try:
            for win in Desktop(backend=backend).windows(process=pid, visible_only=True):
                t = win.window_text()
                if not title_re or re.search(title_re, t, re.I): return Desktop(backend=backend).window(handle=win.handle)
        except Exception: pass  # (a busy program can fail one UI Automation call)
        time.sleep(0.5)
    raise RuntimeError(f"no window of pid {pid} matching {title_re!r}")

def dialog(win, pid, title_re, timeout=15):
    """A dialog of the program: a top-level window, or (UI Automation shows owned dialogs that way) a Window inside the main one."""
    end = time.time() + timeout
    while time.time() < end:
        try: return wait_window(pid, title_re, timeout=1)
        except RuntimeError: pass
        try:
            spec = win.child_window(title_re=title_re, control_type="Window")
            if spec.exists(timeout=0.5): return spec
        except Exception: pass
    raise RuntimeError(f"no dialog {title_re!r}")

def popup_item(win, pid, title_re, timeout=6):
    """A menu item of an open popup menu: inside the main window or in a popup window of its own (Qt, Delphi)."""
    from pywinauto import Desktop
    end = time.time() + timeout
    while time.time() < end:
        for top in [win] + list(Desktop(backend="uia").windows(process=pid)):
            try:
                for it in top.descendants(control_type="MenuItem"):
                    if re.search(title_re, it.window_text(), re.I) and it.is_visible(): return it
            except Exception: pass
        time.sleep(0.4)
    raise RuntimeError(f"no menu item {title_re!r}")

def first(win, **crit):
    crit.setdefault("found_index", 0)
    for attempt in range(4):  # (a busy program can fail one UI Automation call: try again)
        try: return win.child_window(**crit).wrapper_object()
        except Exception:
            if attempt == 3: raise
            time.sleep(1.0)

def scratch(name):
    d = os.path.join(ROOT, "evidence", "compat", "work"); os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)

# ---------------------------------------------------------------- the apps

def main():
    # a drive stuck in a UI Automation call (a busy program) ends with its stack printed after 4 minutes, never hangs a session
    import faulthandler, threading
    started = []  # the processes this run started (the program, the watcher): the watchdog ends only these
    def watchdog():
        print("WATCHDOG: the drive is stuck; its stack:", flush=True)
        faulthandler.dump_traceback(all_threads=True)
        for p in started:
            try: p.kill()
            except Exception: pass
        os._exit(3)
    dog = threading.Timer(300, watchdog); dog.daemon = True; dog.start()
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from apps import APPS  # scripts/compat/apps.py

    ap = argparse.ArgumentParser()
    ap.add_argument("app", choices=sorted(APPS))
    ap.add_argument("--score-only", action="store_true", help="score the last run again (no driving)")
    ap.add_argument("--watcher")
    ap.add_argument("--keep", action="store_true", help="keep the program open at the end (default: kill it)")
    ap.add_argument("--no-watcher", action="store_true", help="drive only (checks the drive itself)")
    a = ap.parse_args()
    spec = APPS[a.app]
    wt = Watcher(find_watcher(a.watcher))
    if a.score_only:
        last = sorted(glob.glob(os.path.join(ROOT, "evidence", "compat", f"{a.app}-*.steps.json")))[-1]
        score(a.app, json.load(open(last, encoding="utf-8")), wt.app_folder(), os.path.join(ROOT, "evidence", "compat"))
        return
    target, profile, sources = spec["prepare"]()
    if not a.no_watcher:
        wt.setup(target, profile, sources)
        wt.start(); started.append(wt.proc)
    proc = None; steps = []
    try:
        proc, pids, win = spec["start"]()
        started.append(proc)
        d = Drive(pids)
        try:
            spec["drive"](d, win, pids)
        finally:
            steps = d.steps
            time.sleep(3)
            try:  # the test program's own window at the end, to check the drive did what it meant
                win.capture_as_image().save(os.path.join(ROOT, "evidence", "compat", f"{a.app}-end.png"))
            except Exception as ex: print("no end picture:", ex)
    finally:
        if a.no_watcher:
            if proc is not None and not a.keep: spec.get("stop", lambda p: p.kill())(proc)
            print(f"drive done without the watcher ({len(steps)} steps)"); return
        if proc is not None and not a.keep:
            spec.get("stop", lambda p: p.kill())(proc)
        time.sleep(2)
        wt.stop()
        wt.run("--learn")
    folder = wt.app_folder()
    try: shutil.copy(os.path.join(wt.data, "watcher.log"), os.path.join(ROOT, "evidence", "compat", f"{a.app}-last.log"))
    except Exception: pass
    if not folder or not steps: sys.exit("nothing recorded")
    score(a.app, steps, folder, os.path.join(ROOT, "evidence", "compat"))

if __name__ == "__main__":
    main()
