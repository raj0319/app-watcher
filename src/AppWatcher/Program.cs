using System.Runtime.InteropServices;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Export;
using AppWatcher.Core.Learning;

namespace AppWatcher;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

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
        AttachConsole(-1);
        var cfg = ConfigStore.Load();
        try
        {
            switch (cmd)
            {
                case "--help":
                    Console.WriteLine("AppWatcher.exe [--tray] | --learn | --rebuild | --export | --payload | --status | --expect | --detect [exe] | --merge <folder or zip> | --trace-files <pid> <seconds> <out.json>   (add --portable to use a folder next to the exe)");
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
                    // scripted first-run (fleet installs, tests): --setup <exe> <password> [profile]; data sources can be added later in Settings
                    var rest = args.SkipWhile(a => a != "--setup").Skip(1).Where(a => !a.StartsWith("--")).ToList();
                    if (rest.Count < 2) { Console.Error.WriteLine("usage: --setup <path to exe> <settings password> [profile name]"); return 1; }
                    var exe = Path.GetFullPath(rest[0]);
                    if (!File.Exists(exe)) { Console.Error.WriteLine("not found: " + exe); return 1; }
                    var proc = Path.GetFileNameWithoutExtension(exe);
                    var tk = ToolkitDetector.Detect(exe);
                    cfg.App = new WatchedApp { ProcessName = proc, ExePath = exe, DisplayName = proc };
                    cfg.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(rest[1]));
                    cfg.Toolkit = tk.Name;
                    cfg.Backends = BackendDetector.Detect(exe, proc, null, proc);
                    var profile = rest.Count > 2 ? ProfileStore.ByName(rest[2]) : ProfileStore.Match(exe, proc, ProductName(exe), tk.Name);
                    profile?.ApplyTo(cfg);
                    ConfigStore.Save(cfg);
                    Console.WriteLine($"Set up to watch {proc} ({tk.Name}, profile {cfg.Profile ?? "none"}, {cfg.Backends.Count} backends found). Start AppWatcher.exe to begin watching.");
                    return 0;
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
