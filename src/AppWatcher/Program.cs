using System.Runtime.InteropServices;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
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
    /// Headless commands, usable from a terminal or from Claude Code while the tray app keeps running:
    ///   AppWatcher.exe --learn            incremental learn: rebuild memory/*.md from the evidence
    ///   AppWatcher.exe --rebuild          full recompute from day one (drops learn-state.json)
    ///   AppWatcher.exe --export           write the twin-kit bundle, the XES log, and zip them
    ///   AppWatcher.exe --payload          print exactly what the optional API analysis would send
    ///   AppWatcher.exe --status           one line of status
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
                    Console.WriteLine("AppWatcher.exe [--tray] | --learn | --rebuild | --export | --payload | --status | --trace-files <pid> <seconds> <out.json>   (add --portable to use a folder next to the exe)");
                    return 0;
                case "--status":
                    Console.WriteLine(cfg.App is null ? "Not set up yet." : $"Watching {cfg.App.DisplayName} ({cfg.App.ProcessName}). Data: {Paths.ForApp(cfg.App.DisplayName).Root}. Sources: {cfg.DataSources.Count(s => s.Enabled)}. SQL capture: {(cfg.SqlCapture.Enabled ? "on" : "off")}. Last learn: {cfg.LastLearn?.ToString("yyyy-MM-dd HH:mm") ?? "never"}.");
                    return 0;
                case "--trace-files":
                {
                    var rest = args.SkipWhile(a => a != "--trace-files").Skip(1).Where(a => !a.StartsWith("--")).ToList();
                    if (rest.Count < 3 || !int.TryParse(rest[0], out var pid) || !int.TryParse(rest[1], out var secs)) { Console.Error.WriteLine("usage: --trace-files <pid> <seconds> <out.json>"); return 1; }
                    return Collector.FileAccessTrace.Run(pid, secs, rest[2]);
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
}

/// <summary>One place that runs a learn (incremental or full), persists the state and writes the memory files.</summary>
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
            cfg.LastLearn = DateTime.Now;
            ConfigStore.Save(cfg);
            return result;
        }
    }
}
