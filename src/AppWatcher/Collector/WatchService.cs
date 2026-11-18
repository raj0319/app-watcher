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
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _processTimer;
    private readonly System.Windows.Forms.Timer _idleTimer;
    private Process? _process;
    private DateTime _lastActivity = DateTime.Now;
    private long _eventsWritten;
    private int _effectsWritten;
    private readonly Dictionary<string, (string val, DateTime t, UiEvent ev)> _pendingValues = new();

    public WatchService(WatcherConfig config, SynchronizationContext ui)
    {
        _config = config;
        _paths = Paths.ForApp(config.App!.DisplayName);
        _paths.EnsureDirectories();
        _events = new EventWriter(_paths.Events);
        _snapshots = new SnapshotService(_paths, config.Snapshot, config.DataSources);
        _uia = new UiaCollector(_channel.Writer, ui);
        _effects = new EffectCapture(_snapshots, config.Snapshot, config.EffectTriggers, _paths.Effects, _ => Interlocked.Increment(ref _effectsWritten),
            () => _uia.InFront ? _uia.CurrentTitle : "");
        _uia.InFrontChanged += inFront => { if (inFront) _effects.EnsureBeforeExists(); };
        _processTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _processTimer.Tick += (_, _) => CheckProcess();
        _idleTimer = new System.Windows.Forms.Timer { Interval = Math.Max(5, config.Snapshot.IdleSecondsBeforeSnapshot) * 1000 };
        _idleTimer.Tick += (_, _) => OnIdleTick();
        Task.Factory.StartNew(Consume, TaskCreationOptions.LongRunning);
        _events.Write(new UiEvent { Act = Acts.Start, Win = "", Name = "App Watcher started", Ct = "watcher" });
    }

    public bool Paused { get; set; }
    public bool IsAttached => _uia.IsAttached;
    public bool InFront => _uia.InFront;
    public AppPaths Paths_ => _paths;
    public long EventsWritten => Interlocked.Read(ref _eventsWritten);
    public int EffectsWritten => _effectsWritten;
    public int MainWindowControls => _uia.MainWindowControlCount;
    public bool HasDataSources => _snapshots.HasSources;
    public DateTime AttachedSince { get; private set; }

    public void Start()
    {
        CheckProcess();
        _processTimer.Start();
        _idleTimer.Start();
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
                FlushPending();
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
                timeout.CancelAfter(800);
                raw = await reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) { FlushPending(maxAge: TimeSpan.FromMilliseconds(800)); continue; }
            try { Handle(raw); }
            catch (Exception ex) { Log.Error("event handling failed", ex); }
        }
    }

    private void Handle(RawEvent raw)
    {
        // screens are dumped even when the window is not yet in front (it just opened)
        if (raw.Screen is not null)
        {
            if (ScreenWriter.WriteOnce(_paths.Screens, raw.Screen)) Log.Info($"screen recorded: {raw.Screen.Title} ({raw.Screen.Controls.Count} controls)");
        }
        if (Paused) return;
        var inFront = _uia.InFront || raw.Act is Acts.Open or Acts.Close or Acts.Dialog or Acts.Foreground;
        if (!inFront) return; // another program is in front: record nothing
        _lastActivity = DateTime.Now;

        var ev = new UiEvent
        {
            T = raw.T, Win = raw.WindowTitle ?? "", Ct = raw.Ct, Name = raw.Name, Aid = raw.Aid, Act = raw.Act, Val = raw.Val,
        };
        if (raw.Act == Acts.Value)
        {
            // coalesce keystrokes: keep only the last value per control until the user moves on
            var key = ev.Win + "\u0001" + ev.FieldKey + "\u0001" + ev.Ct;
            lock (_pendingValues) _pendingValues[key] = (ev.Val ?? "", DateTime.Now, ev);
            return;
        }
        FlushPending();
        Write(ev);
        if (raw.Act == Acts.Invoke) _effects.OnInvoke(ev);
    }

    private void FlushPending(TimeSpan? maxAge = null)
    {
        List<UiEvent> flush;
        lock (_pendingValues)
        {
            var now = DateTime.Now;
            var keys = _pendingValues.Where(p => maxAge is null || now - p.Value.t >= maxAge).Select(p => p.Key).ToList();
            flush = keys.Select(k => _pendingValues[k].ev).OrderBy(e => e.T).ToList();
            foreach (var k in keys) _pendingValues.Remove(k);
        }
        foreach (var e in flush) Write(e);
    }

    private void Write(UiEvent e)
    {
        _events.Write(e);
        Interlocked.Increment(ref _eventsWritten);
    }

    public void Dispose()
    {
        _processTimer.Stop(); _idleTimer.Stop();
        _cts.Cancel();
        FlushPending();
        try { _events.Write(new UiEvent { Act = Acts.Stop, Name = "App Watcher stopped", Ct = "watcher" }); } catch { }
        _uia.Dispose();
        _effects.Dispose();
        _events.Dispose();
        _process?.Dispose();
    }
}
