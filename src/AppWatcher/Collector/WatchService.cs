using System.Diagnostics;
using System.Threading.Channels;
using AppWatcher.Core;
using AppWatcher.Core.Events;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Collector;

/// <summary>
/// The always-on collector for one app: finds the process, attaches the UIA collector, writes events,
/// dumps screens once, and runs effect capture. Records nothing while another program is in front.
/// It only subscribes and reads: it never invokes a control of the watched app and never opens a window.
/// </summary>
internal sealed class WatchService : IDisposable
{
    private readonly WatcherConfig _config;
    private readonly AppPaths _paths;
    private readonly Channel<RawEvent> _channel = Channel.CreateUnbounded<RawEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly UiaCollector _uia;
    private readonly EventWriter _events;
    private readonly SnapshotService _snapshots;
    private readonly EffectCapture _effects;
    // a value is committed after 2 s of quiet: under load UI Automation delivers keystrokes a second or more apart
    private readonly ValueDebouncer _debounce = new() { Quiet = TimeSpan.FromMilliseconds(2000), CellWait = TimeSpan.FromMilliseconds(3000) };
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _processTimer;
    private readonly System.Windows.Forms.Timer _idleTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly CollectorStatus _status;
    private Process? _process;
    private DateTime _lastActivity = DateTime.Now;
    private long _eventsWritten;
    private int _effectsWritten;
    private string _lastStatusJson = "";
    private readonly ShotStore _shots;
    private readonly string _shotMode;
    private System.Threading.Timer? _stateTimer;
    private (string label, string why, DateTime t) _pendingState;
    private readonly HashSet<string> _noPictureScreens = new();

    public WatchService(WatcherConfig config, SynchronizationContext ui)
    {
        _config = config;
        AppWatcher.Core.Learning.Learner.ApplyTitleMasks(config); // screen and picture file names carry the masked title, never the company name
        _paths = Paths.ForApp(config.App!.DisplayName);
        _paths.EnsureDirectories();
        _status = CollectorStatus.Load(_paths.Root) ?? new CollectorStatus { Since = DateTime.Now };
        if (_status.Since == default) _status.Since = DateTime.Now;
        _status.Version = WatcherConfig.AppVersion; _status.Toolkit = config.Toolkit; _status.Profile = config.Profile;
        _status.HasDataSources = config.DataSources.Any(d => d.Enabled);
        _events = new EventWriter(_paths.Events);
        _shots = new ShotStore(_paths.Screens) { MaxPerScreen = Math.Max(1, config.Privacy.MaxShotsPerScreen), MaxTotalBytes = Math.Max(10, config.Privacy.MaxShotMegabytes) * 1024L * 1024 };
        _shotMode = config.Privacy.EffectiveShotMode;
        _snapshots = new SnapshotService(_paths, config.Snapshot, config.DataSources);
        _uia = new UiaCollector(_channel.Writer, ui, config.Collector, config.Privacy, _status);
        _effects = new EffectCapture(_snapshots, config.Snapshot, config.EffectTriggers, _paths.Effects, _ => { Interlocked.Increment(ref _effectsWritten); _status.EffectsWritten++; },
            () => _uia.InFront ? _uia.CurrentTitle : "");
        _uia.InFrontChanged += inFront => { if (inFront) _effects.EnsureBeforeExists(); };
        _effects.DataChanged += () => _uia.InferPressFromData();
        _processTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _processTimer.Tick += (_, _) => CheckProcess();
        _idleTimer = new System.Windows.Forms.Timer { Interval = Math.Max(5, config.Snapshot.IdleSecondsBeforeSnapshot) * 1000 };
        _idleTimer.Tick += (_, _) => OnIdleTick();
        _statusTimer = new System.Windows.Forms.Timer { Interval = 120_000 };
        _statusTimer.Tick += (_, _) => SaveStatus();
        Task.Factory.StartNew(Consume, TaskCreationOptions.LongRunning);
        // who and where, as hashes: lets a merged memory count users and PCs without storing names
        _events.Write(new UiEvent { Act = Acts.Start, Win = "", Name = "App Watcher started", Ct = "watcher", Val = $"user:{Merge.Tag(Environment.UserName)} machine:{Merge.Tag(Environment.MachineName)} version:{WatcherConfig.AppVersion}" });
    }

    public bool Paused { get; set; }
    public bool IsAttached => _uia.IsAttached;
    public bool InFront => _uia.InFront;
    public long EventsWritten => Interlocked.Read(ref _eventsWritten);
    public int EffectsWritten => _effectsWritten;
    public int MainWindowControls => _uia.MainWindowControlCount;
    public bool HasDataSources => _snapshots.HasSources;
    public DateTime AttachedSince { get; private set; }
    public int? ProcessId => _process?.Id;
    public CollectorStatus Status => _status;

    public void Start()
    {
        CheckProcess();
        _processTimer.Start();
        _idleTimer.Start();
        _statusTimer.Start();
    }

    public void SaveStatus()
    {
        try
        {
            _status.Updated = DateTime.Now;
            var json = System.Text.Json.JsonSerializer.Serialize(_status);
            if (json == _lastStatusJson) return;
            _lastStatusJson = json;
            _status.Save(_paths.Root);
        }
        catch (Exception ex) { Log.Warn("status save failed: " + ex.Message); }
    }

    private void CheckProcess()
    {
        try
        {
            if (_process is not null)
            {
                _process.Refresh();
                if (!_process.HasExited) return;
                Log.Info("watched process exited");
                _process.Dispose(); _process = null;
                _uia.Detach();
                FlushPending(all: true);
                SaveStatus();
                return;
            }
            var name = _config.App!.ProcessName;
            var candidates = Process.GetProcessesByName(name);
            Process? pick = null;
            foreach (var p in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_config.App.ExePath) && !string.Equals(p.MainModule?.FileName, _config.App.ExePath, StringComparison.OrdinalIgnoreCase) && pick is not null) continue;
                    if (pick is null || (p.MainWindowHandle != IntPtr.Zero && pick.MainWindowHandle == IntPtr.Zero)) pick = p;
                }
                catch { pick ??= p; }
            }
            foreach (var p in candidates) if (!ReferenceEquals(p, pick)) p.Dispose();
            if (pick is null) return;
            _process = pick;
            AttachedSince = DateTime.Now;
            _uia.Attach((uint)pick.Id);
        }
        catch (Exception ex) { Log.Error("process check failed", ex); }
    }

    private void OnIdleTick()
    {
        if (!_uia.IsAttached || Paused) return;
        if ((DateTime.Now - _lastActivity).TotalSeconds < _config.Snapshot.IdleSecondsBeforeSnapshot) return;
        ThreadPool.QueueUserWorkItem(_ => _effects.OnIdle());
    }

    private async Task Consume()
    {
        var reader = _channel.Reader;
        while (!_cts.IsCancellationRequested)
        {
            RawEvent? raw = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(400);
                raw = await reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) { FlushPending(all: false); continue; }
            try { Handle(raw); }
            catch (Exception ex) { Log.Error("event handling failed", ex); }
        }
    }

    private void Handle(RawEvent raw)
    {
        // screens are dumped even when the window is not yet in front (it just opened)
        if (raw.Screen is not null)
        {
            // one picture per distinct screen: taken before the record is written (the window may close soon), kept only
            // when the record is new or replaced, never for windows with a password field, never when excluded
            byte[]? png = null; var hash = "";
            var noPicture = raw.Screen.Controls.Any(c => c.IsPassword) || raw.Screen.Kind is ScreenKinds.Session or ScreenKinds.Menu;
            if (noPicture) lock (_noPictureScreens) _noPictureScreens.Add(Core.Learning.Titles.Key(raw.Screen.Title));
            var wantShot = _shotMode != "off" && raw.Hwnd != IntPtr.Zero && !noPicture;
            if (wantShot) { png = Native.WindowCapture.Capture(raw.Hwnd, out hash); if (png is not null) raw.Screen.Screenshot = ShotStore.FolderFor(raw.Screen.Title) + "/01-first-seen.png"; }
            if (ScreenWriter.WriteOnce(_paths.Screens, raw.Screen))
            {
                Log.Info($"screen recorded: {raw.Screen.Title} ({raw.Screen.Controls.Count} controls, {raw.Screen.Kind}, {raw.Screen.Source}{(png is null ? "" : ", picture")})");
                if (png is not null) StoreShot(raw.Screen.Title, "first-seen", png, hash, raw.T, "screen record");
            }
        }
        if (raw.ScreenOnly) return; // a second layout under a title: the record is kept, no event is written for it
        if (raw.Act == Acts.Dropdown && _shotMode == "actions" && raw.Hwnd != IntPtr.Zero && !Paused && _uia.InFront)
        {
            // the suggestion list is its own little window and goes away fast: picture it now
            var png = Native.WindowCapture.Capture(raw.Hwnd, out var hash);
            if (png is not null) StoreShot(raw.WindowTitle ?? _uia.CurrentTitle, "dropdown-" + raw.Name, png, hash, raw.T, $"dropdown on {raw.Name}");
        }
        if (Paused) return;
        var inFront = _uia.InFront || raw.Act is Acts.Open or Acts.Close or Acts.Dialog or Acts.Foreground;
        if (!inFront) return; // another program is in front: record nothing
        _lastActivity = DateTime.Now;

        var ev = new UiEvent { T = raw.T, Win = raw.WindowTitle ?? "", Ct = raw.Ct, Name = raw.Name, Aid = raw.Aid, Act = raw.Act, Val = raw.Val, How = raw.How, By = raw.By, Row = raw.Row, OverGridCell = raw.OverGridCell };
        if (raw.Act == Acts.Value)
        {
            // one event per typed value: committed when focus moves on or after the quiet time
            _debounce.Push(ev, DateTime.Now);
            return;
        }
        Commit(_debounce.FlushFor(ev, DateTime.Now)); // the user moved on (focus, press, close, message): commit what that ends
        if (raw.Act == Acts.Dropdown) { ev.Items = raw.Items; _status.Dropdowns = Math.Max(_status.Dropdowns, 0); }
        if (raw.Act == Acts.Menu) ev.Items = raw.Items;
        Write(ev);
        if (raw.Act == Acts.Invoke) _effects.OnInvoke(ev);
        // a new state of the screen may follow: picture it (and read what a grid shows) once the app has settled
        switch (raw.Act)
        {
            case Acts.Invoke: ScheduleState((raw.Ct == "MenuItem" ? "after-menu-" : "after-press-") + ev.FieldKey, $"{ev.Act} {ev.FieldKey}", readGrids: true); break;
            case Acts.Select when raw.Ct == "TabItem": ScheduleState("after-tab-" + ev.FieldKey, $"tab {ev.FieldKey}", readGrids: true); break;
            case Acts.Select: ScheduleState("after-pick-" + ev.FieldKey, $"pick {ev.FieldKey}", readGrids: false); break;
            case Acts.Dialog: ScheduleState("dialog-" + Core.Learning.Titles.Key(ev.Name), $"dialog {ev.Name}", readGrids: false); break;
            case Acts.Close: ScheduleState("after-close-" + Core.Learning.Titles.Key(ev.Name), $"close {ev.Name}", readGrids: true); break;
            case Acts.Open when raw.Screen is null: ScheduleState("after-open-" + Core.Learning.Titles.Key(ev.Name), $"open {ev.Name}", readGrids: true); break;
        }
    }

    private void FlushPending(bool all) => Commit(_debounce.Flush(DateTime.Now, all));

    private void Commit(List<UiEvent> values)
    {
        foreach (var e in values)
        {
            Write(e);
            if (e.By != ValueBy.App) _status.ValuesTyped++; // one typed value, after the keystrokes settled
            // a value the program filled in follows the user's own action, whose picture already shows it
            if (e.By != ValueBy.App) ScheduleState("after-type-" + e.FieldKey, $"type {e.FieldKey}", readGrids: true);
        }
    }

    /// <summary>One picture (and grid read) per settled state: calls within the settle time collapse into the last one.</summary>
    private void ScheduleState(string label, string why, bool readGrids)
    {
        if (_shotMode != "actions" && !readGrids) return;
        lock (_noPictureScreens) _pendingState = (label, why + (readGrids ? "|grids" : ""), DateTime.Now);
        _stateTimer ??= new System.Threading.Timer(_ => CaptureState(), null, Timeout.Infinite, Timeout.Infinite);
        _stateTimer.Change(650, Timeout.Infinite);
    }

    private void CaptureState()
    {
        try
        {
            (string label, string why, DateTime t) p;
            lock (_noPictureScreens) p = _pendingState;
            if (Paused || !_uia.InFront || !_uia.IsAttached) return;
            var hwnd = _uia.ForegroundHwnd;
            var title = _uia.CurrentTitle;
            if (hwnd == IntPtr.Zero || title.Length == 0) return;
            var key = Core.Learning.Titles.Key(title);
            bool noPicture; lock (_noPictureScreens) noPicture = _noPictureScreens.Contains(key);
            var readGrids = p.why.EndsWith("|grids");
            var why = readGrids ? p.why[..^6] : p.why;
            if (_shotMode == "actions" && !noPicture)
            {
                var png = Native.WindowCapture.Capture(hwnd, out var hash);
                if (png is not null) StoreShot(title, p.label, png, hash, p.t, why);
            }
            if (readGrids)
            {
                foreach (var (grid, lines) in _uia.DumpGrids(hwnd))
                {
                    var action = why.Contains(' ') ? why[(why.IndexOf(' ') + 1)..] : why;
                    Write(new UiEvent { T = DateTime.Now, Win = title, Ct = "DataGrid", Name = action, Val = grid, Act = Acts.Result, Items = lines });
                    _status.Results++;
                }
            }
        }
        catch (Exception ex) { Log.Warn("state capture failed: " + ex.Message); }
    }

    private readonly object _ocrGate = new();

    private void StoreShot(string title, string label, byte[] png, string hash, DateTime t, string why)
    {
        var path = _shots.Add(title, label, png, hash, t, why, out var skipped);
        if (path is null) { _status.PicturesSkipped++; return; }
        _status.Pictures++;
        Log.Info($"picture: {path} ({why})");
        if (_config.Privacy.ReadTextFromPictures && Native.Ocr.Available)
        {
            // read the text on the picture with the built-in Windows OCR, on a background thread, serialized (the engine is not re-entrant)
            var full = Path.Combine(_paths.Screens, path.Replace('/', Path.DirectorySeparatorChar));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Native.Ocr.Result? r;
                    lock (_ocrGate) r = Native.Ocr.Read(png);
                    if (r is null || r.Lines.Count == 0) return;
                    File.WriteAllText(full + ".ocr.json", System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
                    File.WriteAllText(full + ".txt", r.Text);
                    _status.OcrPages++;
                }
                catch (Exception ex) { Log.Warn("OCR sidecar not written: " + ex.Message); }
            });
        }
    }

    private void Write(UiEvent e)
    {
        _events.Write(e);
        Interlocked.Increment(ref _eventsWritten);
    }

    public void Dispose()
    {
        _processTimer.Stop(); _idleTimer.Stop(); _statusTimer.Stop();
        _stateTimer?.Dispose();
        _cts.Cancel();
        FlushPending(all: true);
        try { _events.Write(new UiEvent { Act = Acts.Stop, Name = "App Watcher stopped", Ct = "watcher" }); } catch { }
        SaveStatus();
        _uia.Dispose();
        _effects.Dispose();
        _events.Dispose();
        _process?.Dispose();
    }
}
