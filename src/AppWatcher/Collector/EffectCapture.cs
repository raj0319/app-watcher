using System.Text.RegularExpressions;
using AppWatcher.Core;
using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Collector;

/// <summary>
/// Decides when to take the "after" snapshot: a save-type button was invoked, or a data file changed.
/// Keeps the rolling "before" snapshot fresh at idle moments. Throttled so the watched app is never slowed.
/// </summary>
internal sealed class EffectCapture : IDisposable
{
    private readonly SnapshotService _snapshots;
    private readonly SnapshotSettings _settings;
    private readonly string _effectsDir;
    private readonly Action<EffectRecord> _onEffect;
    private readonly Func<string> _currentWindow;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private Regex _triggers;
    private UiEvent? _lastTrigger;
    private DateTime _pendingAt = DateTime.MinValue;
    private string _pendingReason = "";
    private UiEvent? _pendingEvent;
    private System.Threading.Timer? _timer;
    private DateTime _lastFileChange = DateTime.MinValue;
    private DateTime _lastCapture = DateTime.MinValue;
    private DateTime _lastServerRefresh = DateTime.MinValue;

    public EffectCapture(SnapshotService snapshots, SnapshotSettings settings, IEnumerable<string> triggers, string effectsDir, Action<EffectRecord> onEffect, Func<string> currentWindow)
    {
        _snapshots = snapshots;
        _settings = settings;
        _effectsDir = effectsDir;
        _onEffect = onEffect;
        _currentWindow = currentWindow;
        _triggers = BuildRegex(triggers);
        StartWatchers();
    }

    public static Regex BuildRegex(IEnumerable<string> words)
    {
        var alt = string.Join("|", words.Select(w => w.Trim()).Where(w => w.Length > 0).Select(Regex.Escape));
        return new Regex(alt.Length == 0 ? "(?!)" : $@"(?<![a-z]){alt}(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public void UpdateTriggers(IEnumerable<string> triggers) { lock (_gate) _triggers = BuildRegex(triggers); }

    public bool IsTrigger(string? name) => !string.IsNullOrEmpty(name) && _triggers.IsMatch(name);

    /// <summary>Called for every recorded invoke event.</summary>
    public void OnInvoke(UiEvent e)
    {
        if (!IsTrigger(e.Name) && !IsTrigger(e.Aid)) return;
        lock (_gate)
        {
            _lastTrigger = e;
            Schedule("invoke", e, _settings.AfterDelayMs);
        }
    }

    /// <summary>Called when the user has been idle for a while: refresh the "before" snapshot if the data changed.</summary>
    public void OnIdle()
    {
        if (!_snapshots.HasSources) return;
        lock (_gate) { if (_pendingAt != DateTime.MinValue) return; }
        try
        {
            // servers cannot tell us cheaply whether they changed: refresh them at most every 5 minutes
            var hasServer = _snapshots.Sources.Any(s => !s.WatchPaths.Any());
            if (hasServer && (DateTime.Now - _lastServerRefresh).TotalMinutes < 5 && _snapshots.BeforeTaken != DateTime.MinValue) return;
            if (_snapshots.RefreshBeforeIfChanged()) { _lastServerRefresh = DateTime.Now; Log.Info("before-snapshot refreshed at idle"); }
        }
        catch (Exception ex) { Log.Warn("idle snapshot failed: " + ex.Message); }
    }

    public void EnsureBeforeExists()
    {
        if (!_snapshots.HasSources) return;
        if (_snapshots.BeforeTaken == DateTime.MinValue)
        {
            try { _snapshots.RefreshBeforeIfChanged(force: true); Log.Info("first before-snapshot taken"); }
            catch (Exception ex) { Log.Warn("first snapshot failed: " + ex.Message); }
        }
    }

    private string _pendingWindow = "";

    private void Schedule(string reason, UiEvent? e, int delayMs)
    {
        if (_pendingAt != DateTime.MinValue && reason == "file-change") return; // an invoke capture is already queued
        _pendingReason = reason;
        _pendingEvent = e;
        _pendingWindow = e?.Win ?? _currentWindow();
        _pendingAt = DateTime.Now.AddMilliseconds(delayMs);
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Capture(), null, delayMs, Timeout.Infinite);
    }

    private void Capture()
    {
        string reason; UiEvent? ev; string win;
        lock (_gate)
        {
            reason = _pendingReason; ev = _pendingEvent; win = _pendingWindow;
            _pendingAt = DateTime.MinValue; _pendingEvent = null;
            if ((DateTime.Now - _lastCapture).TotalSeconds < _settings.MinSecondsBetweenSnapshots) { Schedule(reason, ev, _settings.MinSecondsBetweenSnapshots * 1000); return; }
            _lastCapture = DateTime.Now;
        }
        try
        {
            var result = _snapshots.CaptureEffect();
            if (result is null) return;
            var (diff, beforeTaken, after, ms) = result.Value;
            if (diff.IsEmpty && reason == "file-change") return;
            var rec = new EffectRecord
            {
                T = DateTime.Now, Reason = reason,
                Win = win, TriggerName = ev?.Name ?? "", TriggerType = ev?.Ct ?? "", TriggerAid = ev?.Aid ?? "",
                TriggerEvent = ev?.Source, BeforeTaken = beforeTaken, AfterTaken = after.Taken, Diff = diff, SnapshotMs = ms,
            };
            rec.Save(_effectsDir);
            Log.Info($"effect captured ({reason}): {diff.Tables.Count} tables, {diff.Files.Count} files, {ms} ms");
            _onEffect(rec);
        }
        catch (Exception ex) { Log.Error("effect capture failed", ex); }
    }

    private void StartWatchers()
    {
        foreach (var p in _snapshots.WatchPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                FileSystemWatcher w;
                if (Directory.Exists(p)) w = new FileSystemWatcher(p) { IncludeSubdirectories = true };
                else if (Directory.Exists(Path.GetDirectoryName(p))) w = new FileSystemWatcher(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*") { IncludeSubdirectories = false }; // the file may not exist yet: the app creates it on its first save
                else continue;
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                w.Changed += OnFileChanged; w.Created += OnFileChanged; w.Deleted += OnFileChanged; w.Renamed += OnFileChanged;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Warn($"cannot watch {p}: {ex.Message}"); }
        }
    }

    /// <summary>A watched data file changed (raised outside the lock, on the file watcher's thread).</summary>
    public event Action? DataChanged;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _lastFileChange = DateTime.Now;
            var recent = _lastTrigger is not null && (DateTime.Now - _lastTrigger.T).TotalSeconds < 15 ? _lastTrigger : null;
            Schedule("file-change", recent, _settings.AfterDelayMs);
        }
        try { DataChanged?.Invoke(); } catch (Exception ex) { Log.Warn("data change handler failed: " + ex.Message); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
        _watchers.Clear();
    }
}
