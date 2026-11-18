using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Export;
using AppWatcher.Core.Learning;
using AppWatcher.Forms;

namespace AppWatcher;

/// <summary>The tray icon and its menu. Owns the collector and the nightly scheduler.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause");
    private readonly ToolStripMenuItem _learn = new("Learn now");
    private readonly ToolStripMenuItem _export = new("Export...");
    private readonly ToolStripMenuItem _settings = new("Settings...");
    private readonly ToolStripMenuItem _open = new("Open data folder");
    private readonly ToolStripMenuItem _quit = new("Quit");
    private readonly System.Windows.Forms.Timer _minute = new() { Interval = 60_000 };
    private WatcherConfig _config;
    private WatchService? _watch;
    private int _busy;

    public TrayContext()
    {
        _config = ConfigStore.Load();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), _pause, _learn, _export, new ToolStripSeparator(), _settings, _open, new ToolStripSeparator(), _quit });
        _icon = new NotifyIcon { Icon = IconFactory.Create(false), Text = "App Watcher", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => _icon.ShowBalloonTip(3000, "App Watcher", StatusText(), ToolTipIcon.Info);
        _pause.Click += (_, _) => TogglePause();
        _learn.Click += (_, _) => RunLearn(manual: true);
        _export.Click += (_, _) => RunExport();
        _settings.Click += (_, _) => OpenSettings();
        _open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(DataFolder()) { UseShellExecute = true }); } catch { } };
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

    private string DataFolder() => _config.App is null ? Paths.Root : Paths.ForApp(_config.App.DisplayName).Root;

    private void StartWatching()
    {
        _watch?.Dispose();
        _watch = null;
        if (_config.App is null) return;
        try
        {
            _watch = new WatchService(_config, SynchronizationContext.Current!) { Paused = _config.Paused };
            _watch.Start();
            _pause.Text = _config.Paused ? "Resume" : "Pause";
            _icon.Icon = IconFactory.Create(_config.Paused);
            _icon.Text = $"App Watcher: {_config.App.DisplayName}{(_config.Paused ? " (paused)" : "")}";
        }
        catch (Exception ex)
        {
            Log.Error("could not start watching", ex);
            _icon.ShowBalloonTip(5000, "App Watcher", "Could not start watching: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private string StatusText()
    {
        if (_watch is null || _config.App is null) return "Not watching anything";
        var state = _config.Paused ? "paused" : _watch.IsAttached ? (_watch.InFront ? "recording" : "app not in front, idle") : "waiting for the app to start";
        return $"{_config.App.DisplayName}: {state}. {_watch.EventsWritten} events, {_watch.EffectsWritten} effects today.";
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
            // keep the footprint small while the watched app is not in front
            if (!_watch.InFront) MemoryTrim.Trim();
            // nightly learn
            var now = DateTime.Now;
            if (now.ToString("HH:mm") == _config.LearnAt && (_config.LastLearn is null || _config.LastLearn.Value.Date != now.Date))
                RunLearn(manual: false);
            // one-time health check after the first hour of watching
            if (!_config.HealthCheckDone && _watch.AttachedSince != default && (now - _watch.AttachedSince).TotalMinutes >= 60)
            {
                _config.HealthCheckDone = true;
                ConfigStore.Save(_config);
                var problems = new List<string>();
                if (_watch.MainWindowControls >= 0 && _watch.MainWindowControls < 10)
                    problems.Add($"the main window shows only {_watch.MainWindowControls} controls, so field names and buttons cannot be learned (the app may draw its own screens)");
                if (!_watch.HasDataSources) problems.Add("no data source is set, so what the app saves cannot be learned (add one in Settings > Data)");
                if (problems.Count > 0)
                    _icon.ShowBalloonTip(15000, "App Watcher: what it cannot learn from this app", string.Join("; ", problems) + ".", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex) { Log.Error("minute tick failed", ex); }
    }

    private void RunLearn(bool manual)
    {
        if (_config.App is null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { if (manual) _icon.ShowBalloonTip(3000, "App Watcher", "Busy, try again in a moment.", ToolTipIcon.Info); return; }
        var app = _config.App.DisplayName;
        var paths = Paths.ForApp(app);
        Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = Learner.Learn(Learner.LoadInput(paths, app));
                var changes = MemoryWriter.Write(paths.Memory, result);
                _config.LastLearn = DateTime.Now;
                ConfigStore.Save(_config);
                Log.Info($"learn done in {sw.ElapsedMilliseconds} ms: {changes.Count} files changed");
                var msg = $"Learned from {result.EventCount} events and {result.EffectCount} effects: {result.Tables.Count} tables, {result.FieldMappings.Count} field mappings, {result.Rules.Count} rules, {result.Flows.Count} flows.";
                if (manual) _icon.ShowBalloonTip(6000, "App Watcher", msg, ToolTipIcon.Info);
                if (_config.Api.Enabled)
                {
                    var key = Dpapi.Unprotect(_config.Api.ApiKeyProtected);
                    if (!string.IsNullOrEmpty(key))
                    {
                        try
                        {
                            var status = await new ClaudeApiClient(_config.Api, key).AnalyzeAndSaveAsync(paths.Memory);
                            _config.Api.LastRun = DateTime.Now; _config.Api.LastResult = status; ConfigStore.Save(_config);
                            Log.Info("API analysis: " + status);
                        }
                        catch (Exception ex)
                        {
                            _config.Api.LastRun = DateTime.Now; _config.Api.LastResult = "failed: " + ex.Message; ConfigStore.Save(_config);
                            Log.Error("API analysis failed", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("learn failed", ex);
                if (manual) _icon.ShowBalloonTip(6000, "App Watcher", "Learn failed: " + ex.Message, ToolTipIcon.Error);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
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
                var result = Learner.Learn(Learner.LoadInput(paths, app));
                MemoryWriter.Write(paths.Memory, result);
                var (folder, zip) = Exporter.Export(paths, app, result);
                Log.Info("export written: " + zip);
                _icon.ShowBalloonTip(8000, "App Watcher", $"Export ready: {Path.GetFileName(zip)}. Copy docs/raw into app-twin-kit and run /mode spec.", ToolTipIcon.Info);
                try { Process.Start(new ProcessStartInfo(paths.Export) { UseShellExecute = true }); } catch { }
            }
            catch (Exception ex)
            {
                Log.Error("export failed", ex);
                _icon.ShowBalloonTip(6000, "App Watcher", "Export failed: " + ex.Message, ToolTipIcon.Error);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private bool AskPassword(string why)
    {
        using var d = new PasswordDialog(why);
        while (d.ShowDialog() == DialogResult.OK)
        {
            if (PasswordHasher.Verify(Dpapi.Unprotect(_config.PasswordProtected) ?? "", d.Password)) return true;
            MessageBox.Show("Wrong password.", "App Watcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return false;
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
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
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
