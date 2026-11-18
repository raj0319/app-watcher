using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Export;
using AppWatcher.Forms;

namespace AppWatcher;

/// <summary>
/// The tray icon and its menu. Owns the collector and the nightly scheduler.
/// Window policy: the only windows App Watcher ever opens are this menu, the password-protected settings
/// window, and short balloon notices. No file dialogs, no message boxes.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause");
    private readonly ToolStripMenuItem _learn = new("Learn now");
    private readonly ToolStripMenuItem _rebuild = new("Rebuild memory (full recompute)");
    private readonly ToolStripMenuItem _export = new("Export for the twin kit");
    private readonly ToolStripMenuItem _copyPath = new("Copy data folder path for AI");
    private readonly ToolStripMenuItem _copyExport = new("Copy last export path") { Enabled = false };
    private readonly ToolStripMenuItem _settings = new("Settings...");
    private readonly ToolStripMenuItem _quit = new("Quit");
    private readonly System.Windows.Forms.Timer _minute = new() { Interval = 60_000 };
    private WatcherConfig _config;
    private WatchService? _watch;
    private int _busy;
    private string? _lastExport;

    public TrayContext()
    {
        _config = ConfigStore.Load();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), _pause, _learn, _rebuild, _export, new ToolStripSeparator(), _copyPath, _copyExport, _settings, new ToolStripSeparator(), _quit });
        _icon = new NotifyIcon { Icon = IconFactory.Create(false), Text = "App Watcher", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => Balloon(StatusText(), ToolTipIcon.Info);
        _pause.Click += (_, _) => TogglePause();
        _learn.Click += (_, _) => RunLearn(manual: true, rebuild: false);
        _rebuild.Click += (_, _) => RunLearn(manual: true, rebuild: true);
        _export.Click += (_, _) => RunExport();
        _copyPath.Click += (_, _) => CopyToClipboard(DataFolder(), "Data folder path copied. Paste it into Claude Code, OpenCode or OpenClaw.");
        _copyExport.Click += (_, _) => { if (_lastExport is not null) CopyToClipboard(_lastExport, "Export path copied."); };
        _settings.Click += (_, _) => OpenSettings();
        _quit.Click += (_, _) => Quit();
        menu.Opening += (_, _) => _status.Text = StatusText();
        _minute.Tick += (_, _) => MinuteTick();

        if (_config.IsFirstRun)
        {
            using var f = new SettingsForm(_config, firstRun: true);
            if (f.ShowDialog() != DialogResult.OK) { ExitThread(); return; }
            _config = f.Config;
            ConfigStore.Save(_config);
        }
        StartWatching();
        _minute.Start();
        // startup leaves JIT and setup garbage behind; give it back once the first seconds are over
        var once = new System.Windows.Forms.Timer { Interval = 15_000 };
        once.Tick += (_, _) => { once.Stop(); once.Dispose(); MemoryTrim.Trim(); };
        once.Start();
    }

    private void Balloon(string text, ToolTipIcon kind) => _icon.ShowBalloonTip(kind == ToolTipIcon.Warning ? 15000 : 6000, "App Watcher", text, kind);

    /// <summary>Writes only our own path to the clipboard. App Watcher never reads the clipboard.</summary>
    private void CopyToClipboard(string text, string notice)
    {
        try { Clipboard.SetText(text); Balloon(notice, ToolTipIcon.Info); }
        catch (Exception ex) { Log.Warn("clipboard write failed: " + ex.Message); }
    }

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
            _pause.Text = _config.Paused ? "Resume" : "Pause";
            _icon.Icon = IconFactory.Create(_config.Paused);
            _icon.Text = $"App Watcher: {_config.App.DisplayName}{(_config.Paused ? " (paused)" : "")}";
        }
        catch (Exception ex)
        {
            Log.Error("could not start watching", ex);
            Balloon("Could not start watching: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private string StatusText()
    {
        if (_watch is null || _config.App is null) return "Not watching anything";
        var state = _config.Paused ? "paused" : _watch.IsAttached ? (_watch.InFront ? "recording" : "app not in front, idle") : "waiting for the app to start";
        return $"{_config.App.DisplayName}: {state}. {_watch.EventsWritten} events, {_watch.EffectsWritten} effects since start.";
    }

    private void TogglePause()
    {
        _config.Paused = !_config.Paused;
        ConfigStore.Save(_config);
        if (_watch is not null) _watch.Paused = _config.Paused;
        _pause.Text = _config.Paused ? "Resume" : "Pause";
        _icon.Icon = IconFactory.Create(_config.Paused);
        _icon.Text = $"App Watcher: {_config.App?.DisplayName}{(_config.Paused ? " (paused)" : "")}";
    }

    private void MinuteTick()
    {
        try
        {
            if (_config.App is null || _watch is null) return;
            if (!_watch.InFront) MemoryTrim.Trim();
            var now = DateTime.Now;
            if (now.ToString("HH:mm") == _config.LearnAt && (_config.LastLearn is null || _config.LastLearn.Value.Date != now.Date))
                RunLearn(manual: false, rebuild: false);
            if (!_config.HealthCheckDone && _watch.AttachedSince != default && (now - _watch.AttachedSince).TotalMinutes >= 60)
            {
                // first-hour report: a learn writes memory/health.md with the collector's own findings; the balloon is only the headline
                _config.HealthCheckDone = true;
                ConfigStore.Save(_config);
                _watch.SaveStatus();
                var problems = _watch.Status.Problems();
                if (_watch.MainWindowControls >= 0 && _watch.MainWindowControls < 10)
                    problems.Add($"the main window shows only {_watch.MainWindowControls} controls, so field names and buttons cannot be learned (the app may draw its own screens)");
                RunLearn(manual: false, rebuild: false);
                Balloon(problems.Count > 0
                    ? "First hour: " + string.Join("; ", problems.Take(3)) + $". Details: {Paths.ForApp(_config.App.DisplayName).Memory}\\health.md"
                    : $"First hour: screens, presses and values are being recorded normally. Details: {Paths.ForApp(_config.App.DisplayName).Memory}\\health.md", problems.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { Log.Error("minute tick failed", ex); }
    }

    private void RunLearn(bool manual, bool rebuild)
    {
        if (_config.App is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { if (manual) Balloon("Busy, try again in a moment.", ToolTipIcon.Info); return; }
        var app = _config.App.DisplayName;
        var paths = Paths.ForApp(app);
        Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = LearnRunner.Run(_config, paths, app, rebuild, out var changes);
                Log.Info($"{(rebuild ? "rebuild" : "learn")} done in {sw.ElapsedMilliseconds} ms: {changes.Count} files changed");
                if (manual) Balloon($"Learned from {result.EventCount} events and {result.EffectCount} effects: {result.Tables.Count} tables, {result.FieldMappings.Count(m => m.IsSeen)} field mappings seen, {result.Rules.Count} rules, {result.Flows.Count} flows. Memory: {paths.Memory}", ToolTipIcon.Info);
                await RunAnalysisProviders(paths);
            }
            catch (Exception ex)
            {
                Log.Error("learn failed", ex);
                if (manual) Balloon("Learn failed: " + ex.Message, ToolTipIcon.Error);
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
        if (_config.App is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var app = _config.App.DisplayName;
        var paths = Paths.ForApp(app);
        Task.Run(() =>
        {
            try
            {
                var result = LearnRunner.Run(_config, paths, app, rebuild: false, out _);
                var (folder, zip) = Exporter.Export(paths, app, result);
                _lastExport = zip;
                _copyExport.Enabled = true;
                Log.Info("export written: " + zip);
                Balloon($"Export ready: {zip}. Use 'Copy last export path' in the menu. Copy docs/raw into app-twin-kit and run /mode spec.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("export failed", ex);
                Balloon("Export failed: " + ex.Message, ToolTipIcon.Error);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private bool AskPassword(string why)
    {
        using var d = new PasswordDialog(why, pw => PasswordHasher.Verify(Dpapi.Unprotect(_config.PasswordProtected) ?? "", pw));
        return d.ShowDialog() == DialogResult.OK;
    }

    private void OpenSettings()
    {
        if (!AskPassword("Open settings")) return;
        var oldApp = _config.App?.DisplayName;
        using var f = new SettingsForm(_config, firstRun: false);
        if (f.ShowDialog() != DialogResult.OK) return;
        _config = f.Config;
        ConfigStore.Save(_config);
        if (_config.App?.DisplayName != oldApp) Log.Info($"watched app changed to {_config.App?.DisplayName}; new data folder");
        StartWatching();
    }

    private void Quit()
    {
        if (!AskPassword("Quit App Watcher")) return;
        _minute.Stop();
        _watch?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
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
