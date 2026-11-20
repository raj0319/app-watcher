using System.Runtime.InteropServices;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Events;
using AppWatcher.Core.Export;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Verification;

namespace AppWatcher;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    private const int StdOutput = -11;
    private const uint FileTypeDisk = 1, FileTypePipe = 3;

    /// <summary>
    /// AppWatcher.exe is a windowed program. When a shell runs it with output to a file or a pipe
    /// (`appwatcher --detect x > out.txt`, `| more`, or a test reading its output), that handle is already set and
    /// AttachConsole would replace it with the console, so the output would be lost. Attach only when stdout goes nowhere.
    /// </summary>
    internal static bool StdoutRedirected()
    {
        var h = GetStdHandle(StdOutput);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
        var type = GetFileType(h);
        return type is FileTypeDisk or FileTypePipe;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--portable")) Paths.RootOverride = Path.Combine(AppContext.BaseDirectory, "AppWatcherData");
        var cmd = args.FirstOrDefault(a => a.StartsWith("--") && a != "--portable");
        if (cmd is not null && cmd != "--tray") return Cli(cmd, args);

        using var mutex = new Mutex(true, @"Local\AppWatcher.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // already running: no window, no message box; the tray icon of the running copy is the only UI
            Log.Info("second copy started and left; App Watcher is already running");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        Log.Info("App Watcher starting");
        Application.Run(new TrayContext());
        Log.Info("App Watcher stopped");
        return 0;
    }

    /// <summary>
    /// Headless commands, usable from a terminal or from Claude Code while the tray app keeps running.
    /// The exe is a windowed program, so a plain shell does not wait for it: use scripts\appwatcher.cmd
    /// (start /wait) or "Start-Process -Wait -NoNewWindow" to get the exit code and the output.
    ///   AppWatcher.exe --learn            incremental learn: rebuild memory/*.md from the evidence
    ///   AppWatcher.exe --rebuild          full recompute from day one (drops learn-state.json)
    ///   AppWatcher.exe --export           write the twin-kit bundle, the XES log, and zip them
    ///   AppWatcher.exe --payload          print exactly what the optional API analysis would send
    ///   AppWatcher.exe --status           one line of status
    ///   AppWatcher.exe --expect           what can be learned from the configured app (toolkit, backend, data)
    ///   AppWatcher.exe --detect [exe]     toolkit, matching profile and backends of a program file (default: the watched app)
    ///   AppWatcher.exe --merge path       fold another PC's data folder or zip into this app's folder, then learn
    ///   AppWatcher.exe --trace-files pid seconds out.json   (admin) record the files one process touches
    /// Add --portable to use the AppWatcherData folder next to the exe instead of %ProgramData%.
    /// </summary>
    private static int Cli(string cmd, string[] args)
    {
        if (!StdoutRedirected()) AttachConsole(-1);
        var cfg = ConfigStore.Load();
        try
        {
            switch (cmd)
            {
                case "--help":
                    Console.WriteLine("AppWatcher.exe [--tray] | --learn | --rebuild | --export | --payload | --status | --expect | --detect [exe] | --merge <folder or zip> | --verify <scenario.json> <run.jsonl> | --trace-files <pid> <seconds> <out.json>   (add --portable to use a folder next to the exe)");
                    Console.WriteLine("From a shell use scripts\\appwatcher.cmd <command> so the shell waits for the output.");
                    return 0;
                case "--status":
                    Console.WriteLine(cfg.App is null ? "Not set up yet." : $"Watching {cfg.App.DisplayName} ({cfg.App.ProcessName}). Data: {Paths.ForApp(cfg.App.DisplayName).Root}. Sources: {cfg.DataSources.Count(s => s.Enabled)}. SQL capture: {(cfg.SqlCapture.Enabled ? "on" : "off")}. Profile: {cfg.Profile ?? "none"}. Toolkit: {cfg.Toolkit ?? "unknown"}. Last learn: {cfg.LastLearn?.ToString("yyyy-MM-dd HH:mm") ?? "never"}.");
                    return 0;
                case "--trace-files":
                {
                    var rest = args.SkipWhile(a => a != "--trace-files").Skip(1).Where(a => !a.StartsWith("--")).ToList();
                    if (rest.Count < 3 || !int.TryParse(rest[0], out var pid) || !int.TryParse(rest[1], out var secs)) { Console.Error.WriteLine("usage: --trace-files <pid> <seconds> <out.json>"); return 1; }
                    return Collector.FileAccessTrace.Run(pid, secs, rest[2]);
                }
                case "--setup":
                {
                    // scripted first run for IT: the wizard's work without the window, with the same defaults
                    var rest = args.SkipWhile(a => a != "--setup").Skip(1).Where(a => !a.StartsWith("--")).ToList();
                    if (rest.Count < 2) { Console.Error.WriteLine("usage: --setup <path to exe> <settings password> [profile name]"); return 1; }
                    var exe = Path.GetFullPath(rest[0]);
                    if (!File.Exists(exe)) { Console.Error.WriteLine("not found: " + exe); return 1; }
                    if (rest[1].Length < 4) { Console.Error.WriteLine("the password needs at least 4 characters"); return 1; }
                    var proc = Path.GetFileNameWithoutExtension(exe);
                    var plan = Core.Setup.SetupDefaults.Prepare(exe, proc);
                    var fresh = new WatcherConfig();
                    var forced = rest.Count > 2 ? ProfileStore.ByName(rest[2]) : null;
                    if (rest.Count > 2 && forced is null) { Console.Error.WriteLine($"no profile named {rest[2]}"); return 1; }
                    Core.Setup.SetupDefaults.Apply(fresh, plan, forced);
                    fresh.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(rest[1]));
                    ConfigStore.Save(fresh);
                    Console.WriteLine($"Set up to watch {plan.FriendlyName} (profile {fresh.Profile ?? "none"}, {fresh.DataSources.Count(d => d.Enabled)} data places ticked). Start AppWatcher.exe to begin watching.");
                    Console.WriteLine(Core.Setup.SetupDefaults.WhatIWillLearn(plan));
                    return 0;
                }
                case "--render-ui":
                {
                    // pictures of App Watcher's own windows for the docs, drawn off-screen (nothing is shown, nothing of other programs)
                    var outDir = Path.GetFullPath(args.SkipWhile(a => a != "--render-ui").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "ui");
                    Directory.CreateDirectory(outDir);
                    Application.EnableVisualStyles();
                    // a made-up program, so the pictures show nothing of this PC (no real files, titles or connections)
                    var demoPlan = new Core.Setup.SetupPlan
                    {
                        FriendlyName = "DDI inForm", ProcessName = "inform", ExePath = @"C:\DDI\inForm\inForm.exe",
                        ProfileReason = "program", Profile = ProfileStore.ByName("ddi-inform"),
                        Backends = { new Core.Learning.BackendInfo { Kind = "universe", Host = "uv01", Port = 31438, Where = "tcp connection" } },
                    };
                    using (var w = new Forms.SetupWizard())
                    {
                        OffScreen(w);
                        w.ShowStep(0, demoPlan);
                        Render(w, Path.Combine(outDir, "wizard-1-program.png"));
                        w.ShowStep(1); Render(w, Path.Combine(outDir, "wizard-2-privacy.png"));
                        w.ShowStep(2); Render(w, Path.Combine(outDir, "wizard-3-password.png"));
                    }
                    var sample = new WatcherConfig
                    {
                        App = new WatchedApp { DisplayName = demoPlan.FriendlyName, ProcessName = demoPlan.ProcessName, ExePath = demoPlan.ExePath }, PasswordProtected = "x",
                        DataSources = { new DataSourceConfig { Kind = "dropfolder", Path = @"\\server\exports\inform", Label = "exports", Enabled = true } },
                    };
                    using (var s = new Forms.SettingsForm(sample, firstRun: false))
                    {
                        OffScreen(s);
                        s.ShowView(advanced: false); Render(s, Path.Combine(outDir, "settings-simple.png"));
                        s.ShowView(advanced: true, page: 1); Render(s, Path.Combine(outDir, "settings-advanced-data.png"));
                        s.ShowView(advanced: true, page: 2); Render(s, Path.Combine(outDir, "settings-advanced-learning.png"));
                        s.ShowView(advanced: true, page: 3); Render(s, Path.Combine(outDir, "settings-advanced-privacy.png"));
                        s.ShowView(advanced: true, page: 4); Render(s, Path.Combine(outDir, "settings-advanced-ai.png"));
                    }
                    Console.WriteLine("Wrote the pictures to " + outDir);
                    return 0;

                    // controls only exist once a window is shown: show it fully transparent, far off-screen, not in the taskbar
                    static void OffScreen(Form f)
                    {
                        f.StartPosition = FormStartPosition.Manual;
                        f.Location = new Point(-32000, -32000);
                        f.ShowInTaskbar = false;
                        f.Opacity = 0;
                        f.Show();
                        Application.DoEvents();
                    }

                    static void Render(Form f, string path)
                    {
                        f.PerformLayout();
                        Application.DoEvents();
                        using var bmp = new Bitmap(f.Width, f.Height);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                case "--detect":
                {
                    var exe = args.SkipWhile(a => a != "--detect").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? cfg.App?.ExePath;
                    if (string.IsNullOrEmpty(exe)) { Console.Error.WriteLine("usage: --detect <path to exe> (or set up the watched app first)"); return 1; }
                    var tk = ToolkitDetector.Detect(exe);
                    var proc = Path.GetFileNameWithoutExtension(exe);
                    var profile = ProfileStore.Match(exe, proc, ProductName(exe), tk.Name);
                    int? pid = null;
                    try { pid = System.Diagnostics.Process.GetProcessesByName(proc).FirstOrDefault()?.Id; } catch { }
                    var backends = BackendDetector.Detect(exe, proc, pid, proc);
                    Core.Setup.SetupDefaults.ApplyProfileBackendKind(backends, profile);
                    Console.WriteLine($"Toolkit: {tk.Name} ({string.Join("; ", tk.Hints.Take(6))})");
                    Console.WriteLine($"Expectation: {tk.Expectation}");
                    Console.WriteLine($"Profile: {profile?.Name ?? "none"}{(profile?.Note is null ? "" : " - " + profile.Note)}");
                    Console.WriteLine(backends.Count == 0 ? "Backends: none found (no connection strings, DSNs or open connections)" : "Backends:");
                    foreach (var b in backends) Console.WriteLine("  - " + b.Describe());
                    return 0;
                }
            }
            if (cfg.App is null) { Console.Error.WriteLine("App Watcher is not set up yet. Start AppWatcher.exe once and pick the app to watch."); return 2; }
            var app = cfg.App.DisplayName;
            var paths = Paths.ForApp(app);
            switch (cmd)
            {
                case "--learn":
                case "--rebuild":
                {
                    var result = LearnRunner.Run(cfg, paths, app, rebuild: cmd == "--rebuild", out var changes);
                    Console.WriteLine($"{(result.Incremental ? "Incremental learn" : "Full learn")} from {result.EventCount} events, {result.EffectCount} effects, {result.Tables.Count} tables: {result.FieldMappings.Count(m => m.IsSeen)} field mappings seen + {result.FieldMappings.Count(m => !m.IsSeen)} candidates, {result.Relations.Count} relations, {result.Formulas.Count} formulas, {result.Rules.Count} rules, {result.StatusFlows.Count} status flows, {result.Sequences.Count} sequences, {result.Flows.Count} flows.");
                    Console.WriteLine(changes.Count == 0 ? "Memory unchanged." : "Changed: " + string.Join(", ", changes));
                    Console.WriteLine("Memory: " + paths.Memory);
                    return 0;
                }
                case "--export":
                {
                    var result = LearnRunner.Run(cfg, paths, app, rebuild: false, out _);
                    var (folder, zip) = Exporter.Export(paths, app, result);
                    Console.WriteLine("Export folder: " + folder);
                    Console.WriteLine("Zip: " + zip);
                    Console.WriteLine("Next: copy docs/raw into app-twin-kit/docs/raw and run /mode spec. events.xes opens in PM4Py, ProM or Disco.");
                    return 0;
                }
                case "--payload":
                    Console.WriteLine(AnalystPrompt.BuildUserMessage(paths.Memory));
                    return 0;
                case "--expect":
                {
                    var input = Learner.LoadInput(paths, app, cfg);
                    foreach (var line in Learner.Expectations(input)) Console.WriteLine(line);
                    var problems = input.Collector?.Problems() ?? new();
                    foreach (var p in problems) Console.WriteLine("Collector: " + p);
                    return 0;
                }
                case "--verify":
                {
                    // the scorecard of a driven run: what AppWatcher.Driver did (run.jsonl) against what the watcher recorded
                    var rest = args.SkipWhile(a => a != "--verify").Skip(1).Where(a => !a.StartsWith("--")).ToList();
                    if (rest.Count < 2) { Console.Error.WriteLine("usage: --verify <scenario.json> <run.jsonl>"); return 1; }
                    var scenario = Scenario.Load(rest[0]);
                    var run = ScenarioScorer.ReadRun(rest[1]);
                    var card = ScenarioScorer.Score(scenario.Name, run, EventReader.ReadAll(paths.Events));
                    Directory.CreateDirectory(paths.Verification);
                    var stem = Path.Combine(paths.Verification, $"{scenario.Name}-{DateTime.Now:yyyy-MM-dd-HHmmss}");
                    File.WriteAllText(stem + ".md", ScenarioScorer.Markdown(card));
                    File.WriteAllText(stem + ".json", ScenarioScorer.Json(card));
                    Console.WriteLine($"{card.Passed} passed, {card.Failed} failed, {card.Blocked} blocked, {card.Untested} untested of {card.Steps.Count} steps; {card.UnexplainedPresses.Count} press(es) no step explains.");
                    foreach (var (kind, v) in card.ByKind.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.WriteLine($"  {kind}: {v.passed}/{v.passed + v.failed} seen" + (v.blocked > 0 ? $", {v.blocked} blocked" : "") + (v.untested > 0 ? $", {v.untested} untested" : ""));
                    Console.WriteLine("Scorecard: " + stem + ".md");
                    return 0;
                }
                case "--merge":
                {
                    var src = args.SkipWhile(a => a != "--merge").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                    if (string.IsNullOrEmpty(src)) { Console.Error.WriteLine("usage: --merge <another PC's data folder or zip>"); return 1; }
                    var m = Merge.Run(paths, src);
                    Console.WriteLine($"Merged from PC {m.Machine}: {m.EventFiles} event files, {m.EffectFiles} effects, {m.ScreensNew} new screens, {m.ScreensMerged} merged.");
                    var result = LearnRunner.Run(cfg, paths, app, rebuild: true, out var changes);
                    Console.WriteLine($"Rebuilt memory from {result.EventCount} events in {result.SessionCount} sessions; {result.Health.UsersSeen} users on {result.Health.MachinesSeen} PCs. Changed: {string.Join(", ", changes)}");
                    return 0;
                }
                default:
                    Console.Error.WriteLine("Unknown command " + cmd + ". Try --help.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            Log.Error("cli " + cmd + " failed", ex);
            return 1;
        }
    }

    public static string? ProductName(string exe)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName; } catch { return null; }
    }
}

/// <summary>One place that runs a learn (incremental or full), persists the state, writes the memory files and applies retention.</summary>
internal static class LearnRunner
{
    private static readonly object Gate = new();

    public static LearnResult Run(WatcherConfig cfg, AppPaths paths, string app, bool rebuild, out List<string> changes)
    {
        lock (Gate)
        {
            var statePath = Path.Combine(paths.Memory, Learner.StateFile);
            var state = rebuild ? new LearnState() : LearnState.Load(statePath);
            var input = Learner.LoadInput(paths, app, cfg, since: state.Watermark?.AddHours(-1));
            var result = Learner.Learn(input, state, out var committed);
            committed.Save(statePath);
            changes = MemoryWriter.Write(paths.Memory, result);
            File.WriteAllText(Path.Combine(paths.Root, "expectations.md"), ExpectationsText(result.Expectations, result.Health.CollectorProblems));
            if (cfg.RetentionDays > 0) Retention.RotateEvents(paths, cfg.RetentionDays, committed.Watermark);
            cfg.LastLearn = DateTime.Now;
            ConfigStore.Save(cfg);
            return result;
        }
    }

    public static string ExpectationsText(IEnumerable<string> expectations, IEnumerable<string>? problems = null)
    {
        var sb = new System.Text.StringBuilder("# What App Watcher can learn from this app\n\n");
        foreach (var e in expectations) sb.Append("- ").Append(e).Append('\n');
        var p = problems?.ToList() ?? new();
        if (p.Count > 0) { sb.Append("\n## What the collector could not do so far\n\n"); foreach (var x in p) sb.Append("- ").Append(x).Append('\n'); }
        return sb.ToString();
    }
}
