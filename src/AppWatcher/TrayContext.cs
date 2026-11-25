using System.ComponentModel;
using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Export;
using AppWatcher.Core.Setup;
using AppWatcher.Forms;
using AppWatcher.Native;

namespace AppWatcher;

/// <summary>
/// The tray icon and its menu, in plain words. Owns the collector, the nightly learn, the first-hour report and the
/// one automatic file-access detection after the first start.
/// Window policy: the only windows App Watcher opens are the first-run wizard, this menu, the password-protected
/// settings window, the folder the user asks to open, and short tray notices. No message boxes. Every notice says
/// what happened and what to do next in one sentence.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause for an hour");
    private readonly ToolStripMenuItem _resume = new("Resume");
    private readonly ToolStripMenuItem _learn = new("Update what I learned");
    private readonly ToolStripMenuItem _export = new("Make a report folder");
    private readonly ToolStripMenuItem _open = new("Open the folder with everything collected");
    private readonly ToolStripMenuItem _settings = new("Settings...");
    private readonly ToolStripMenuItem _quit = new("Quit");
    private readonly System.Windows.Forms.Timer _minute = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _soon = new() { Interval = 20_000 };
    private WatcherConfig _config;
    private WatchService? _watch;
    private int _busy;
    private Process? _trace;
    private string _traceFile = "";
    private readonly SynchronizationContext _ui;
    private RegisteredWaitHandle? _stopWait;
    private SetupWizard? _wizard;

    /// <summary>Set once the tray has ended (Quit, a cancelled wizard, or an update stopping it); Program then skips the message loop.</summary>
    public bool Ended { get; private set; }

    public TrayContext(WaitHandle? stop = null)
    {
        _config = ConfigStore.Load();
        var menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // an update replacing this exe asks it to stop: the same clean stop as Quit, without the password
        if (stop is not null)
            _stopWait = ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => _ui.Post(_ => StopForUpdate(), null), null, Timeout.Infinite, executeOnlyOnce: true);
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), _pause, _resume, _learn, _export, _open, new ToolStripSeparator(), _settings, new ToolStripSeparator(), _quit });
        _icon = new NotifyIcon { Icon = IconFactory.Create(false), Text = "App Watcher", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => Balloon(StatusText() + ". Right-click me for more.", ToolTipIcon.Info);
        _pause.Click += (_, _) => PauseForAnHour();
        _resume.Click += (_, _) => Resume("");
        _learn.Click += (_, _) => RunLearn(manual: true, rebuild: false);
        _export.Click += (_, _) => RunExport();
        _open.Click += (_, _) => OpenFolder();
        _settings.Click += (_, _) => OpenSettings();
        _quit.Click += (_, _) => Quit();
        menu.Opening += (_, _) => _status.Text = StatusText();
        _minute.Tick += (_, _) => MinuteTick();
        _soon.Tick += (_, _) => AutoDetectTick();

        var justSetUp = false;
        if (_config.IsFirstRun)
        {
            using var w = _wizard = new SetupWizard();
            var answer = w.ShowDialog();
            _wizard = null;
            if (answer != DialogResult.OK || Ended) { ExitThread(); return; }
            _config = w.Config;
            ConfigStore.Save(_config);
            justSetUp = true;
        }
        StartWatching();
        _minute.Start();
        _soon.Start();
        if (justSetUp) Balloon($"I am watching {Name}. Just use it as usual.", ToolTipIcon.Info);
        // startup leaves JIT and setup garbage behind; give it back once the first seconds are over
        var once = new System.Windows.Forms.Timer { Interval = 15_000 };
        once.Tick += (_, _) => { once.Stop(); once.Dispose(); MemoryTrim.Trim(); };
        once.Start();
    }

    private string Name => _config.App?.DisplayName ?? "the program";
    private void Balloon(string text, ToolTipIcon kind) => _icon.ShowBalloonTip(kind == ToolTipIcon.Warning ? 15000 : 7000, "App Watcher", text, kind);
    private string DataFolder() => _config.App is null ? Paths.Root : Paths.ForApp(_config.App.DisplayName).Root;

    private void StartWatching()
    {
        _watch?.Dispose();
        _watch = null;
        if (_config.App is null) return;
        try
        {
            Core.Learning.Titles.MaskQuoted = _config.Privacy.MaskTitles;
            _watch = new WatchService(_config, SynchronizationContext.Current!) { Paused = _config.Paused };
            _watch.Start();
            try
            {
                var paths = Paths.ForApp(_config.App.DisplayName);
                var input = new Core.Learning.LearnInput { AppName = _config.App.DisplayName, Backends = _config.Backends, Toolkit = _config.Toolkit, Profile = _config.Profile, HasDataSources = _config.DataSources.Any(d => d.Enabled) };
                File.WriteAllText(Path.Combine(paths.Root, "expectations.md"), LearnRunner.ExpectationsText(Core.Learning.Learner.Expectations(input)));
            }
            catch (Exception ex) { Log.Warn("expectations not written: " + ex.Message); }
            ShowPauseState();
        }
        catch (Exception ex)
        {
            Log.Error("could not start watching", ex);
            Balloon($"I could not start watching {Name} ({ex.Message}). Open Settings and pick the program again.", ToolTipIcon.Error);
        }
    }

    private void ShowPauseState()
    {
        if (_watch is not null) _watch.Paused = _config.Paused;
        _pause.Enabled = !_config.Paused;
        _resume.Enabled = _config.Paused;
        _icon.Icon = IconFactory.Create(_config.Paused);
        _icon.Text = $"App Watcher: {Name}{(_config.Paused ? " (paused)" : "")}";
    }

    private string StatusText()
    {
        if (_watch is null || _config.App is null) return "Not watching anything yet";
        if (_config.Paused) return _config.PausedUntil is { } u ? $"Paused until {u:HH:mm}" : "Paused";
        if (!_watch.IsAttached) return $"Waiting for {Name} to start";
        return _watch.InFront ? $"Watching {Name}: {_watch.EventsWritten} actions since the start" : $"{Name} is open but not in front; waiting";
    }

    private void PauseForAnHour()
    {
        _config.Paused = true;
        _config.PausedUntil = DateTime.Now.AddHours(1);
        ConfigStore.Save(_config);
        ShowPauseState();
        Balloon($"Paused for an hour. I will start watching {Name} again at {_config.PausedUntil:HH:mm}, or choose Resume.", ToolTipIcon.Info);
    }

    private void Resume(string why)
    {
        _config.Paused = false;
        _config.PausedUntil = null;
        ConfigStore.Save(_config);
        ShowPauseState();
        Balloon($"Watching {Name} again{why}. Just use it as usual.", ToolTipIcon.Info);
    }

    private void OpenFolder()
    {
        try
        {
            var folder = DataFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Balloon($"I could not open the folder ({ex.Message}). It is under ProgramData\\AppWatcher\\data.", ToolTipIcon.Warning); }
    }

    private void MinuteTick()
    {
        try
        {
            if (_config.App is null || _watch is null) return;
            if (!_watch.InFront) MemoryTrim.Trim();
            var now = DateTime.Now;
            if (_config.Paused && _config.PausedUntil is { } until && now >= until) Resume(" (the hour is up)");
            if (now.ToString("HH:mm") == _config.LearnAt && (_config.LastLearn is null || _config.LastLearn.Value.Date != now.Date))
                RunLearn(manual: false, rebuild: false);
            var first = _config.FirstStart ?? (_watch.AttachedSince == default ? (DateTime?)null : _watch.AttachedSince);
            if (!_config.HealthCheckDone && first is not null && (now - first.Value).TotalMinutes >= 60) FirstHour();
        }
        catch (Exception ex) { Log.Error("minute tick failed", ex); }
    }

    /// <summary>One hour after the first start: health.md in plain words even if no learn ran, one notice, then a learn.</summary>
    private void FirstHour()
    {
        _config.HealthCheckDone = true;
        ConfigStore.Save(_config);
        _watch?.SaveStatus();
        var paths = Paths.ForApp(Name);
        PlainReport.WriteFirstHourHealth(paths, _config, Name);
        var facts = PlainReport.FromDisk(paths, _config, Name);
        Balloon(PlainReport.TrayFirstHour(facts), PlainReport.Problems(facts).Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        RunLearn(manual: false, rebuild: false);
    }

    // ---------- the one automatic file-access detection ----------

    private void AutoDetectTick()
    {
        if (!_config.AutoDetectPending) { _soon.Stop(); return; }
        if (_config.App is null || _watch?.ProcessId is not int pid || _config.Paused) return;
        _config.AutoDetectPending = false;
        ConfigStore.Save(_config);
        _soon.Stop();
        var kind = Elevation.Check();
        if (kind == Elevation.Kind.NotAdmin) { Log.Info("automatic file-access detection skipped: this user is not an administrator"); return; }
        if (kind == Elevation.Kind.CanElevate)
            Balloon($"To find where {Name} keeps its data, Windows will ask you once for permission. Say No if you prefer; I keep watching either way.", ToolTipIcon.Info);
        var delay = new System.Windows.Forms.Timer { Interval = kind == Elevation.Kind.CanElevate ? 6000 : 200 };
        delay.Tick += (_, _) => { delay.Stop(); delay.Dispose(); StartTrace(pid, kind); };
        delay.Start();
    }

    private void StartTrace(int pid, Elevation.Kind kind)
    {
        _traceFile = Path.Combine(Paths.Root, "file-trace.json");
        try { File.Delete(_traceFile); } catch { }
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var args = $"--trace-files {pid} 120 \"{_traceFile}\"" + (Paths.RootOverride is not null ? " --portable" : "");
        // the permission prompt can stay open for a while: start the process off the tray's thread so the menu keeps working
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            Process? started = null;
            try
            {
                started = kind == Elevation.Kind.Elevated
                    ? Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })
                    : Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Win32Exception) { Log.Info("automatic file-access detection: permission not given; skipped"); return; }
            catch (Exception ex) { Log.Warn("automatic file-access detection could not start: " + ex.Message); return; }
            if (started is null) return;
            if (ui is null) { Log.Warn("automatic file-access detection: no tray thread to report back to"); return; }
            ui.Post(_ => WaitForTrace(started), null);
        });
    }

    private void WaitForTrace(Process started)
    {
        _trace = started;
        var poll = new System.Windows.Forms.Timer { Interval = 3000 };
        poll.Tick += (_, _) =>
        {
            try { if (!_trace.HasExited) return; } catch { }
            poll.Stop(); poll.Dispose();
            ApplyTrace();
        };
        poll.Start();
    }

    private void ApplyTrace()
    {
        try
        {
            if (!File.Exists(_traceFile)) return;
            var files = FileAccessTrace.Load(_traceFile);
            try { File.Delete(_traceFile); } catch { }
            var added = SetupDefaults.AddTracedSources(_config, files.Select(f => (f.Key, f.Value.Reads, f.Value.Writes)));
            if (added == 0) { Log.Info("automatic file-access detection found no new data files"); return; }
            ConfigStore.Save(_config);
            StartWatching();
            Balloon($"I found where {Name} keeps its data ({added} file{(added == 1 ? "" : "s")}). I will now also learn what it saves.", ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Warn("file-access detection result not applied: " + ex.Message); }
    }

    // ---------- learn and export ----------

    private void RunLearn(bool manual, bool rebuild)
    {
        if (_config.App is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { if (manual) Balloon("I am still busy with the last request. Try again in a minute.", ToolTipIcon.Info); return; }
        var app = _config.App.DisplayName;
        var paths = Paths.ForApp(app);
        _watch?.SaveStatus(); // the status file is otherwise saved every two minutes: learn from today's counts
        Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = LearnRunner.Run(_config, paths, app, rebuild, out var changes);
                Log.Info($"{(rebuild ? "rebuild" : "learn")} done in {sw.ElapsedMilliseconds} ms: {changes.Count} files changed");
                if (manual)
                    Balloon($"I updated what I learned: {PlainReport.Plural(result.Screens.Count, "screen")}, {PlainReport.Plural(result.Rules.Count, "rule")} and {PlainReport.Plural(result.Flows.Count, "flow")} so far. Choose \"Open the folder with everything collected\" to read it.", ToolTipIcon.Info);
                await RunAnalysisProviders(paths);
            }
            catch (Exception ex)
            {
                Log.Error("learn failed", ex);
                if (manual) Balloon($"I could not update what I learned ({ex.Message}). I will try again tonight at {_config.LearnAt}.", ToolTipIcon.Error);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    /// <summary>Optional analyses, both off by default. Only memory/*.md leaves the folder.</summary>
    private async Task RunAnalysisProviders(AppPaths paths)
    {
        if (_config.Api.Enabled)
        {
            var key = Dpapi.Unprotect(_config.Api.ApiKeyProtected);
            if (!string.IsNullOrEmpty(key))
            {
                try { _config.Api.LastResult = await new ClaudeApiClient(_config.Api, key).AnalyzeAndSaveAsync(paths.Memory); }
                catch (Exception ex) { _config.Api.LastResult = "failed: " + ex.Message; Log.Error("API analysis failed", ex); }
                _config.Api.LastRun = DateTime.Now; ConfigStore.Save(_config);
            }
        }
        if (_config.Local.Enabled)
        {
            var file = _config.Api.Enabled ? "architecture-reviewed-local.md" : "architecture-reviewed.md";
            try { _config.Local.LastResult = await new LocalLlmClient(_config.Local).AnalyzeAndSaveAsync(paths.Memory, file); }
            catch (Exception ex) { _config.Local.LastResult = "failed: " + ex.Message; Log.Error("local analysis failed", ex); }
            _config.Local.LastRun = DateTime.Now; ConfigStore.Save(_config);
        }
    }

    private void RunExport()
    {
        if (_config.App is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Balloon("I am still busy with the last request. Try again in a minute.", ToolTipIcon.Info); return; }
        var app = _config.App.DisplayName;
        var paths = Paths.ForApp(app);
        Balloon("Making the report folder; this takes a few seconds.", ToolTipIcon.Info);
        Task.Run(() =>
        {
            try
            {
                var result = LearnRunner.Run(_config, paths, app, rebuild: false, out _);
                var (_, zip) = Exporter.Export(paths, app, result);
                Log.Info("export written: " + zip);
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true }); } catch { }
                Balloon("Your report folder is ready and I opened it. Send the selected zip file to whoever is studying the program.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("export failed", ex);
                Balloon($"I could not make the report folder ({ex.Message}). Try again in a minute.", ToolTipIcon.Error);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    // ---------- settings and quit ----------

    private bool AskPassword(string why)
    {
        using var d = new PasswordDialog(why, pw => PasswordHasher.Verify(Dpapi.Unprotect(_config.PasswordProtected) ?? "", pw));
        return d.ShowDialog() == DialogResult.OK;
    }

    private void OpenSettings()
    {
        if (!AskPassword("Open settings")) return;
        var oldApp = _config.App?.DisplayName;
        using var f = new SettingsForm(_config, firstRun: false, requestRebuild: () => RunLearn(manual: true, rebuild: true));
        if (f.ShowDialog() != DialogResult.OK) return;
        _config = f.Config;
        ConfigStore.Save(_config);
        var changed = _config.App?.DisplayName != oldApp;
        if (changed) Log.Info($"watched program changed to {_config.App?.DisplayName}; new data folder");
        StartWatching();
        if (changed) { _soon.Start(); Balloon($"I am watching {Name} now. Just use it as usual.", ToolTipIcon.Info); }
    }

    private void Quit()
    {
        if (!AskPassword("Quit App Watcher")) return;
        _minute.Stop(); _soon.Stop();
        _watch?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    /// <summary>A newer copy is being installed over this exe: stop cleanly so the file can be replaced; the installer starts the new one.</summary>
    private void StopForUpdate()
    {
        if (Ended) return;
        Log.Info("stopping so an update can replace the program file");
        if (_wizard is not null) { Ended = true; _wizard.DialogResult = DialogResult.Cancel; return; }
        _minute.Stop(); _soon.Stop();
        _watch?.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        Ended = true;
        _stopWait?.Unregister(null);
        _stopWait = null;
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}

internal static class MemoryTrim
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static DateTime _last = DateTime.MinValue;

    /// <summary>Returns unused managed memory and lets Windows page out what the idle tray app does not need. At most once per 5 minutes.</summary>
    public static void Trim()
    {
        if ((DateTime.Now - _last).TotalMinutes < 5) return;
        _last = DateTime.Now;
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }
}

internal static class IconFactory
{
    public static Icon Create(bool paused)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(40, 110, 200);
            using var pen = new Pen(body, 4f);
            g.DrawEllipse(pen, 4, 9, 24, 14);
            using var iris = new SolidBrush(paused ? Color.FromArgb(110, 110, 110) : Color.FromArgb(20, 60, 130));
            g.FillEllipse(iris, 11, 11, 10, 10);
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, 14, 13, 3, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
