using System.Diagnostics;
using System.Text.Json;
using AppWatcher.Core.Sources;

namespace AppWatcher.Core.Snapshots;

/// <summary>
/// Keeps a rolling "before" snapshot on disk, takes "after" snapshots on demand, diffs them,
/// and remembers which tables are hot so big databases are only partly read.
/// Everything here is read-only towards the watched app's data.
/// </summary>
public sealed class SnapshotService
{
    private readonly AppPaths _paths;
    private readonly SnapshotSettings _settings;
    private readonly object _gate = new();
    private List<ISnapshotSource> _sources;
    private DateTime _lastSnapshotAt = DateTime.MinValue;
    private DateTime _beforeTaken = DateTime.MinValue;
    private readonly List<HashSet<string>> _recentChangedTables = new();

    public SnapshotService(AppPaths paths, SnapshotSettings settings, IEnumerable<DataSourceConfig> sources)
    {
        _paths = paths;
        _settings = settings;
        _sources = SourceFactory.CreateAll(sources);
        LoadHot();
        var before = DataSnapshot.Load(_paths.BeforeSnapshot);
        if (before is not null) _beforeTaken = before.Taken;
    }

    public bool HasSources => _sources.Count > 0;
    public IReadOnlyList<ISnapshotSource> Sources => _sources;
    public DateTime BeforeTaken => _beforeTaken;
    public DateTime LastSnapshotAt => _lastSnapshotAt;

    public void ReplaceSources(IEnumerable<DataSourceConfig> sources)
    {
        lock (_gate) _sources = SourceFactory.CreateAll(sources);
    }

    public IEnumerable<string> WatchPaths => _sources.SelectMany(s => s.WatchPaths);

    /// <summary>Hot tables = tables that changed in one of the last N diffs. Null when nothing is known yet (= snapshot everything).</summary>
    public ISet<string>? HotTables
    {
        get
        {
            lock (_gate)
            {
                if (_recentChangedTables.Count == 0) return null;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in _recentChangedTables) set.UnionWith(s);
                return set;
            }
        }
    }

    /// <summary>Takes a fresh snapshot of every source. Returns null when throttled.</summary>
    public DataSnapshot? Take(bool force = false)
    {
        lock (_gate)
        {
            if (!force && (DateTime.Now - _lastSnapshotAt).TotalSeconds < _settings.MinSecondsBetweenSnapshots) return null;
            var snap = new DataSnapshot { Taken = DateTime.Now };
            var hot = HotTables;
            var sw = Stopwatch.StartNew();
            foreach (var s in _sources)
            {
                try { s.Snapshot(snap, _settings, hot); }
                catch (Exception ex) { snap.Warnings.Add($"{s.Id}: {ex.Message}"); Log.Warn($"snapshot {s.Id} failed: {ex.Message}"); }
            }
            _lastSnapshotAt = DateTime.Now;
            if (sw.ElapsedMilliseconds > 3000) Log.Warn($"snapshot took {sw.ElapsedMilliseconds} ms; consider raising the size caps or narrowing the data paths");
            return snap;
        }
    }

    /// <summary>Refreshes the rolling "before" snapshot if any source changed since it was taken.</summary>
    public bool RefreshBeforeIfChanged(bool force = false)
    {
        lock (_gate)
        {
            if (_sources.Count == 0) return false;
            if (!force && _beforeTaken != DateTime.MinValue)
            {
                var changed = false;
                foreach (var s in _sources)
                {
                    var c = s.ChangedSince(_beforeTaken);
                    if (c is null || c == true) { changed = true; break; }
                }
                if (!changed) return false;
            }
            var snap = Take(force: true);
            if (snap is null) return false;
            snap.Save(_paths.BeforeSnapshot);
            _beforeTaken = snap.Taken;
            return true;
        }
    }

    /// <summary>Takes an "after" snapshot, diffs it against the rolling "before", makes it the new "before".</summary>
    public (DiffResult diff, DateTime beforeTaken, DataSnapshot after, int ms)? CaptureEffect()
    {
        lock (_gate)
        {
            if (_sources.Count == 0) return null;
            var sw = Stopwatch.StartNew();
            var before = DataSnapshot.Load(_paths.BeforeSnapshot);
            var after = Take(force: true)!;
            DiffResult diff;
            if (before is null)
            {
                diff = new DiffResult();
            }
            else
            {
                diff = SnapshotDiffer.Diff(before, after);
            }
            after.Save(_paths.BeforeSnapshot);
            var beforeTaken = _beforeTaken;
            _beforeTaken = after.Taken;
            if (!diff.IsEmpty)
            {
                _recentChangedTables.Add(new HashSet<string>(diff.Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase));
                while (_recentChangedTables.Count > _settings.HotTableHistory) _recentChangedTables.RemoveAt(0);
                SaveHot();
            }
            return (diff, beforeTaken, after, (int)sw.ElapsedMilliseconds);
        }
    }

    private void LoadHot()
    {
        try
        {
            if (!File.Exists(_paths.HotTables)) return;
            var lists = JsonSerializer.Deserialize<List<List<string>>>(File.ReadAllText(_paths.HotTables));
            if (lists is null) return;
            foreach (var l in lists) _recentChangedTables.Add(new HashSet<string>(l, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
    }

    private void SaveHot()
    {
        try
        {
            Directory.CreateDirectory(_paths.Snapshots);
            File.WriteAllText(_paths.HotTables, JsonSerializer.Serialize(_recentChangedTables.Select(s => s.ToList()).ToList()));
        }
        catch { }
    }
}
