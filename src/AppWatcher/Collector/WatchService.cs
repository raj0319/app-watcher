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
    private readonly ValueDebouncer _debounce = new();
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

    public WatchService(WatcherConfig config, SynchronizationContext ui)
    {
        _config = config;
        _paths = Paths.ForApp(config.App!.DisplayName);
        _paths.EnsureDirectories();
        _status = CollectorStatus.Load(_paths.Root) ?? new CollectorStatus { Since = DateTime.Now };
        if (_status.Since == default) _status.Since = DateTime.Now;
        _status.Version = WatcherConfig.AppVersion; _status.Toolkit = config.Toolkit; _status.Profile = config.Profile;
        _status.HasDataSources = config.DataSources.Any(d => d.Enabled);
        _events = new EventWriter(_paths.Events);
        _snapshots = new SnapshotService(_paths, config.Snapshot, config.DataSources);
        _uia = new UiaCollector(_channel.Writer, ui, config.Collector, config.Privacy, _status);
        _effects = new EffectCapture(_snapshots, config.Snapshot, config.EffectTriggers, _paths.Effects, _ => { Interlocked.Increment(ref _effectsWritten); _status.EffectsWritten++; },
            () => _uia.InFront ? _uia.CurrentTitle : "");
        _uia.InFrontChanged += inFront => { if (inFront) _effects.EnsureBeforeExists(); };
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
            byte[]? png = null;
            var wantShot = _config.Privacy.Screenshots && raw.Hwnd != IntPtr.Zero && !raw.Screen.Controls.Any(c => c.IsPassword) && raw.Screen.Kind != ScreenKinds.Session;
            if (wantShot) { png = Native.WindowCapture.Capture(raw.Hwnd); if (png is not null) raw.Screen.Screenshot = "shots/" + raw.Screen.ShotFileName; }
            if (ScreenWriter.WriteOnce(_paths.Screens, raw.Screen))
            {
                Log.Info($"screen recorded: {raw.Screen.Title} ({raw.Screen.Controls.Count} controls, {raw.Screen.Kind}, {raw.Screen.Source}{(png is null ? "" : ", picture")})");
                if (png is not null)
                {
                    try { var dir = Path.Combine(_paths.Screens, "shots"); Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, raw.Screen.ShotFileName), png); }
                    catch (Exception ex) { Log.Warn("picture not saved: " + ex.Message); }
                }
            }
        }
        if (Paused) return;
        var inFront = _uia.InFront || raw.Act is Acts.Open or Acts.Close or Acts.Dialog or Acts.Foreground;
        if (!inFront) return; // another program is in front: record nothing
        _lastActivity = DateTime.Now;

        var ev = new UiEvent { T = raw.T, Win = raw.WindowTitle ?? "", Ct = raw.Ct, Name = raw.Name, Aid = raw.Aid, Act = raw.Act, Val = raw.Val, How = raw.How };
        if (raw.Act == Acts.Value)
        {
            // one event per typed value: committed when focus moves on or after the quiet time
            _debounce.Push(ev, DateTime.Now);
            return;
        }
        FlushPending(all: true); // focus left the control (or anything else happened): commit pending values first
        Write(ev);
        if (raw.Act == Acts.Invoke) _effects.OnInvoke(ev);
    }

    private void FlushPending(bool all)
    {
        foreach (var e in _debounce.Flush(DateTime.Now, all)) Write(e);
    }

    private void Write(UiEvent e)
    {
        _events.Write(e);
        Interlocked.Increment(ref _eventsWritten);
    }

    public void Dispose()
    {
        _processTimer.Stop(); _idleTimer.Stop(); _statusTimer.Stop();
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
