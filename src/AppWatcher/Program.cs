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
            MessageBox.Show("App Watcher is already running. Look for its icon in the tray (bottom right, near the clock).", "App Watcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    ///   AppWatcher.exe --learn            rebuild memory/*.md from the evidence
    ///   AppWatcher.exe --export           write the twin-kit bundle and zip it
    ///   AppWatcher.exe --payload          print exactly what the optional API analysis would send
    ///   AppWatcher.exe --status           one line of status
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
                    Console.WriteLine("AppWatcher.exe [--tray] | --learn | --export | --payload | --status   (add --portable to use a folder next to the exe)");
                    return 0;
                case "--status":
                    Console.WriteLine(cfg.App is null ? "Not set up yet." : $"Watching {cfg.App.DisplayName} ({cfg.App.ProcessName}). Data: {Paths.ForApp(cfg.App.DisplayName).Root}. Sources: {cfg.DataSources.Count(s => s.Enabled)}. Last learn: {cfg.LastLearn?.ToString("yyyy-MM-dd HH:mm") ?? "never"}.");
                    return 0;
            }
            if (cfg.App is null) { Console.Error.WriteLine("App Watcher is not set up yet. Start AppWatcher.exe once and pick the app to watch."); return 2; }
            var app = cfg.App.DisplayName;
            var paths = Paths.ForApp(app);
            switch (cmd)
            {
                case "--learn":
                {
                    var result = Learner.Learn(Learner.LoadInput(paths, app));
                    var changes = MemoryWriter.Write(paths.Memory, result);
                    cfg.LastLearn = DateTime.Now; ConfigStore.Save(cfg);
                    Console.WriteLine($"Learned from {result.EventCount} events, {result.EffectCount} effects, {result.Tables.Count} tables: {result.FieldMappings.Count} field mappings, {result.Relations.Count} relations, {result.Formulas.Count} formulas, {result.Rules.Count} rules, {result.StatusFlows.Count} status flows, {result.Sequences.Count} sequences, {result.Flows.Count} flows.");
                    Console.WriteLine(changes.Count == 0 ? "Memory unchanged." : "Changed: " + string.Join(", ", changes));
                    Console.WriteLine("Memory: " + paths.Memory);
                    return 0;
                }
                case "--export":
                {
                    var result = Learner.Learn(Learner.LoadInput(paths, app));
                    MemoryWriter.Write(paths.Memory, result);
                    var (folder, zip) = Exporter.Export(paths, app, result);
                    Console.WriteLine("Export folder: " + folder);
                    Console.WriteLine("Zip: " + zip);
                    Console.WriteLine("Next: copy docs/raw into app-twin-kit/docs/raw and run /mode spec.");
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
