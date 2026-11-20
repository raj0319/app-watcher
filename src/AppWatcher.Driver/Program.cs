using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Verification;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace AppWatcher.Driver;

/// <summary>
/// Runs a scenario file against the original program with real input (UI Automation to find controls, the mouse and the
/// keyboard to act), the way the test PC's scripts did, and writes a run log the watcher's scorer reads. This is the
/// tool that clicks; the watcher itself never does. It refuses to run without --test-environment, asks for the word
/// DRIVE unless --yes is given, acts only while a window of the target process is in front, and runs a press on Save,
/// Post, Delete, Send or the like only when the step says confirm: true.
/// </summary>
internal static class Program
{
    private static readonly HashSet<string> DataChanging = new(StringComparer.OrdinalIgnoreCase) { "save", "post", "delete", "remove", "send", "submit", "approve", "pay", "void", "release", "invoice", "print", "e-mail", "email" };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    private static UIA3Automation _automation = null!;
    private static uint _pid;
    private static AutomationElement? _main;
    private static string _windowPattern = "*";
    private static StreamWriter _log = null!;
    private static int _settle = 1500;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("AppWatcher.Driver <scenario.json> (--process <name> | --pid <n>) --test-environment [--yes] [--out run.jsonl] [--settle ms]");
            Console.WriteLine("Runs the scenario against the program with real mouse and keyboard input. Test environments only: it clicks.");
            return 2;
        }
        var scenario = Scenario.Load(args[0]);
        string? process = null; var pid = 0; var test = false; var yes = false; var outPath = "run.jsonl";
        for (var i = 1; i < args.Length; i++)
            switch (args[i])
            {
                case "--process": process = args[++i]; break;
                case "--pid": pid = int.Parse(args[++i]); break;
                case "--test-environment": test = true; break;
                case "--yes": yes = true; break;
                case "--out": outPath = args[++i]; break;
                case "--settle": _settle = int.Parse(args[++i]); break;
            }
        if (!test) { Console.Error.WriteLine("Refusing to drive a program without --test-environment. This tool clicks and types in it."); return 3; }
        if (pid == 0 && process is not null) pid = Process.GetProcessesByName(process.Replace(".exe", "")).Select(p => p.Id).FirstOrDefault();
        if (pid == 0) { Console.Error.WriteLine("The target is not running (give --process <name> or --pid <n>)."); return 3; }
        if (!yes)
        {
            Console.Write($"This will click and type in process {pid} ({scenario.Steps.Count} steps of '{scenario.Name}') on what you say is a TEST environment. Type DRIVE to go on: ");
            if (Console.ReadLine()?.Trim() != "DRIVE") { Console.Error.WriteLine("Not authorised; nothing was done."); return 3; }
        }
        _pid = (uint)pid; _windowPattern = scenario.Window;
        _automation = new UIA3Automation();
        using var log = _log = new StreamWriter(outPath, append: false) { AutoFlush = true };
        var index = 0;
        foreach (var step in scenario.Steps)
        {
            index++;
            var t = DateTime.Now; var status = "done"; string? note = null;
            try
            {
                if (step.Do == StepKind.Press && DataChanging.Any(w => (step.Target ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)) && step.Confirm != true)
                { status = "blocked"; note = "a press that can change data needs confirm: true in the scenario"; }
                else if (!Run(step, out note)) status = "blocked";
            }
            catch (Exception ex) { status = "failed"; note = ex.GetType().Name + ": " + ex.Message; }
            var rec = new RunStep { T = t, Index = index, Step = step, Status = status, Note = note };
            _log.WriteLine(JsonSerializer.Serialize(rec, Scenario.Json).Replace("\r", "").Replace("\n", ""));
            Console.WriteLine($"{t:HH:mm:ss} {index,3} {step.Describe(),-40} {status}{(note is null ? "" : " (" + note + ")")}");
            if (status == "failed") break;
        }
        Console.WriteLine("Run log: " + Path.GetFullPath(outPath));
        return 0;
    }

    // ---- the steps ----

    private static bool Run(ScenarioStep s, out string? note)
    {
        note = null;
        switch (s.Do)
        {
            case StepKind.Wait: Thread.Sleep(s.Ms ?? 1000); return true;
            case StepKind.Open:
            case StepKind.Press:
            {
                var el = FindButton(s.Target ?? "");
                if (el is null) { note = $"button '{s.Target}' not found (seen: {Seen(ControlType.Button)})"; return false; }
                if (!Click(el, out note)) return false;
                Thread.Sleep(s.Do == StepKind.Open ? Math.Max(_settle, 3000) : _settle);
                return true;
            }
            case StepKind.Menu:
            {
                var path = s.Path ?? new();
                if (path.Count == 0) { note = "empty menu path"; return false; }
                var top = Main()?.FindFirstDescendant(cf => cf.ByName(path[0]).And(cf.ByControlType(ControlType.MenuItem)));
                if (top is null) { note = $"menu '{path[0]}' not found"; return false; }
                if (!Click(top, out note)) return false;
                Thread.Sleep(900);
                for (var i = 1; i < path.Count; i++)
                {
                    var item = FindAnywhere(path[i], ControlType.MenuItem) ?? FindAnywhere(path[i], null);
                    if (item is null) { note = $"menu item '{path[i]}' not found (seen: {Seen(ControlType.MenuItem)})"; Keyboard.Press(VirtualKeyShort.ESCAPE); return false; }
                    if (!Click(item, out note)) return false;
                    Thread.Sleep(900);
                }
                Thread.Sleep(Math.Max(_settle, 3000));
                return true;
            }
            case StepKind.Type:
            {
                var field = FindField(s.Field ?? "");
                if (field is null) { note = $"field '{s.Field}' not found"; return false; }
                if (!InFront()) { note = "not in front"; return false; }
                note = $"typed into '{field.AutomationId}' in [{WindowOf(field)}]";
                try { field.Focus(); } catch { ClickAt(field.BoundingRectangle.Left + 8, field.BoundingRectangle.Top + field.BoundingRectangle.Height / 2); }
                Thread.Sleep(250);
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Thread.Sleep(80);
                Keyboard.Type(s.Value ?? "");
                Thread.Sleep(300);
                Keyboard.Press(string.Equals(s.Then, "tab", StringComparison.OrdinalIgnoreCase) ? VirtualKeyShort.TAB : VirtualKeyShort.RETURN);
                Thread.Sleep(_settle);
                return true;
            }
            case StepKind.Nav:
            {
                var tab = FindAnywhere(s.Target ?? "", ControlType.TabItem) ?? FindAnywhere(s.Target ?? "", null, notText: true);
                if (tab is null) { note = $"'{s.Target}' not found (seen: {Seen(ControlType.TabItem)})"; return false; }
                if (!Click(tab, out note)) return false;
                Thread.Sleep(_settle);
                return true;
            }
            case StepKind.Hover:
            {
                var el = FindButton(s.Target ?? "") ?? FindAnywhere(s.Target ?? "", null);
                if (el is null) { note = $"'{s.Target}' not found"; return false; }
                if (!InFront()) { note = "not in front"; return false; }
                var r = el.BoundingRectangle;
                // leave first, then come back and rest: a tip shows after the pointer arrives and stays
                Glide(r.Left + r.Width / 2, r.Top + r.Height + 60); Thread.Sleep(500);
                Glide(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(1500);
                return true;
            }
            case StepKind.Prompt:
            {
                var deadline = DateTime.Now.AddSeconds(8);
                while (DateTime.Now < deadline)
                {
                    foreach (var w in Windows())
                    {
                        if (!DialogDetector.Wildcard(w.Name ?? "", s.Title ?? "*")) continue;
                        var b = w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(s.Button ?? "OK")));
                        if (b is null) continue;
                        try { SetForegroundWindow(new IntPtr(w.Properties.NativeWindowHandle.ValueOrDefault)); } catch { }
                        Thread.Sleep(300);
                        if (!Click(b, out note)) return false;
                        Thread.Sleep(_settle);
                        return true;
                    }
                    Thread.Sleep(300);
                }
                note = $"prompt '{s.Title}' with button '{s.Button}' did not appear";
                return false;
            }
        }
        return false;
    }

    // ---- finding and acting ----

    /// <summary>
    /// The process's top-level windows: what UI Automation lists under the desktop, plus every visible top-level window Win32
    /// knows (a modal box owned by the main window and a menu's drop-down are not always listed under the desktop).
    /// </summary>
    private static IEnumerable<AutomationElement> Windows()
    {
        var list = new List<AutomationElement>(); var handles = new HashSet<IntPtr>();
        try
        {
            foreach (var w in _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId((int)_pid)))
            {
                try { handles.Add(new IntPtr(w.Properties.NativeWindowHandle.ValueOrDefault)); } catch { }
                list.Add(w);
            }
        }
        catch { }
        var visible = new List<IntPtr>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var p); if (p == _pid && IsWindowVisible(h)) visible.Add(h); return true; }, IntPtr.Zero);
        foreach (var h in visible)
        {
            if (handles.Contains(h)) continue;
            try { var e = _automation.FromHandle(h); if (e is not null) { list.Add(e); handles.Add(h); } } catch { }
        }
        return list;
    }

    private static AutomationElement? Main()
    {
        if (_main is not null) { try { _ = _main.Name; return _main; } catch { _main = null; } }
        _main = Windows().FirstOrDefault(w => DialogDetector.Wildcard(w.Name ?? "", _windowPattern)) ?? Windows().FirstOrDefault();
        return _main;
    }

    private static AutomationElement? FindButton(string name)
    {
        foreach (var root in Roots())
        {
            var hit = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))).FirstOrDefault(OnTop)
                   ?? root.FindAllDescendants(cf => cf.ByControlType(ControlType.SplitButton).And(cf.ByName(name))).FirstOrDefault(OnTop);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static AutomationElement? FindAnywhere(string name, ControlType? type, bool notText = false)
    {
        foreach (var root in Roots())
        {
            // a plain walk, filtered here: a property condition evaluated on a WinForms TabControl can flip or hide its tab items
            var all = root.FindAllDescendants().Where(e => { try { return string.Equals(e.Name, name, StringComparison.Ordinal) && (type is null || e.ControlType == type); } catch { return false; } });
            var hit = all.FirstOrDefault(e => OnTop(e) && (!notText || e.ControlType != ControlType.Text));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static AutomationElement? FindField(string idOrName)
    {
        foreach (var root in Roots())
        {
            var edits = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Where(OnScreen).ToList();
            var hit = edits.FirstOrDefault(e => DialogDetector.Wildcard(e.AutomationId ?? "", idOrName) || DialogDetector.Wildcard(e.Name ?? "", idOrName) || DialogDetector.Wildcard(Labels.Clean(e.AutomationId), idOrName));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Popups and dialogs of the process first (a prompt or a lookup in front), then the main window.</summary>
    private static IEnumerable<AutomationElement> Roots()
    {
        var main = Main();
        foreach (var w in Windows()) if (main is null || !w.Equals(main)) yield return w;
        if (main is not null) yield return main;
    }

    /// <summary>The names of the controls of one type on screen in the process's windows: what a scenario could target instead.</summary>
    private static string Seen(ControlType type)
    {
        var names = new List<string>();
        foreach (var w in Windows())
        {
            try { names.Add("[" + (w.Name ?? "") + "]"); } catch { }
            try
            {
                foreach (var e in w.FindAllDescendants())
                {
                    try { if (e.ControlType == type && OnScreen(e) && !string.IsNullOrEmpty(e.Name)) names.Add(e.Name); } catch { }
                    if (names.Count > 40) break;
                }
            }
            catch { }
        }
        return string.Join(", ", names.Take(40));
    }

    private static bool OnScreen(AutomationElement e)
    {
        try { var r = e.BoundingRectangle; return !e.Properties.IsOffscreen.ValueOrDefault && r.Width > 0 && r.Height > 0; } catch { return false; }
    }

    /// <summary>On screen and really in front: the element at its centre is itself (an MDI document behind another has visible buttons too).</summary>
    private static bool OnTop(AutomationElement e)
    {
        if (!OnScreen(e)) return false;
        try
        {
            var r = e.BoundingRectangle;
            var at = _automation.FromPoint(new System.Drawing.Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
            if (at is null) return true;
            var ar = at.BoundingRectangle;
            return at.Equals(e) || (ar.Left == r.Left && ar.Top == r.Top && ar.Width == r.Width && ar.Height == r.Height) || string.Equals(at.Name, e.Name, StringComparison.Ordinal);
        }
        catch { return true; }
    }

    private static bool InFront()
    {
        for (var i = 0; i < 10; i++)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var p);
            if (p == _pid) return true;
            var main = Main();
            if (main is not null) { try { SetForegroundWindow(new IntPtr(main.Properties.NativeWindowHandle.ValueOrDefault)); } catch { } }
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>A real click at the control's centre (the toolkit's buttons react to nothing else), only while the target is in front. The note says what was clicked where.</summary>
    private static bool Click(AutomationElement el, out string? note)
    {
        note = "the target is not the window in front";
        if (!InFront()) return false;
        var r = el.BoundingRectangle;
        string name; try { name = el.Name ?? ""; } catch { name = ""; }
        if (r.Width <= 0)
        {
            try { el.Patterns.Invoke.PatternOrDefault?.Invoke(); note = $"invoked '{name}' (no rectangle) in [{WindowOf(el)}]"; return true; } catch { note = $"'{name}' has no rectangle and no invoke"; return false; }
        }
        var x = r.Left + r.Width / 2; var y = r.Top + r.Height / 2;
        if (!ClickAt(x, y)) { note = $"the target left the front before the click on '{name}' at {x},{y}"; return false; }
        note = $"clicked '{name}' at {x},{y} in [{WindowOf(el)}]";
        return true;
    }

    private static bool ClickAt(int x, int y)
    {
        Glide(x, y);
        Thread.Sleep(120);
        GetWindowThreadProcessId(GetForegroundWindow(), out var p);
        if (p != _pid) return false;
        Mouse.Click(MouseButton.Left);
        return true;
    }

    /// <summary>The title of the window the control sits in (its nearest Window ancestor).</summary>
    private static string WindowOf(AutomationElement el)
    {
        try
        {
            var h = new IntPtr(el.Properties.NativeWindowHandle.ValueOrDefault);
            if (h != IntPtr.Zero)
            {
                var root = GetAncestor(h, 2 /* GA_ROOT */);
                var sb = new System.Text.StringBuilder(256);
                if (root != IntPtr.Zero && GetWindowText(root, sb, 256) > 0) return sb.ToString();
            }
        }
        catch { }
        try
        {
            var e = el;
            for (var i = 0; i < 30 && e is not null; i++)
            {
                if (e.ControlType == ControlType.Window && !string.IsNullOrEmpty(e.Name)) return e.Name;
                e = e.Parent;
            }
        }
        catch { }
        return "?";
    }

    private static void Glide(int x, int y)
    {
        Mouse.MoveTo(new System.Drawing.Point(x - 12, y - 6)); Thread.Sleep(60);
        Mouse.MoveTo(new System.Drawing.Point(x - 4, y - 2)); Thread.Sleep(60);
        Mouse.MoveTo(new System.Drawing.Point(x, y));
    }
}
