using System.Diagnostics;
using System.Text.Json;
using AppWatcher.Core.Sources;

namespace AppWatcher.Core.Snapshots;

/// <summary>
/// Keeps a rolling "before" snapshot on disk (refreshed at idle moments, so it costs nothing at save time),
/// takes "after" snapshots on demand, diffs them, and remembers which tables are hot so big databases are
/// only partly read. Cheap change signals (file mtime and size, SQLite data_version, SQL Server change
/// tracking) decide which sources and tables are re-read at all.
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
    private Dictionary<string, string> _tokens = new();
    public int LastSkippedSources { get; private set; }
    public int LastPartialSources { get; private set; }

    public SnapshotService(AppPaths paths, SnapshotSettings settings, IEnumerable<DataSourceConfig> sources)
    {
        _paths = paths;
        _settings = settings;
        _sources = SourceFactory.CreateAll(sources);
        LoadState();
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

    /// <summary>
    /// Takes a fresh snapshot of every source. With <paramref name="previous"/>, sources and tables whose cheap change
    /// signal says "unchanged" are copied from it instead of being read. Returns null when throttled.
    /// </summary>
    public DataSnapshot? Take(bool force = false, DataSnapshot? previous = null)
    {
        lock (_gate)
        {
            if (!force && (DateTime.Now - _lastSnapshotAt).TotalSeconds < _settings.MinSecondsBetweenSnapshots) return null;
            var snap = new DataSnapshot { Taken = DateTime.Now };
            var hot = HotTables;
            var sw = Stopwatch.StartNew();
            LastSkippedSources = 0; LastPartialSources = 0;
            foreach (var s in _sources)
            {
                try
                {
                    ChangeSignal signal;
                    try { signal = s.Probe(_tokens.GetValueOrDefault(s.Id)); } catch { signal = new ChangeSignal(null, null, null); }
                    if (signal.Token is not null) _tokens[s.Id] = signal.Token;

                    if (previous is not null && signal.Changed == false)
                    {
                        CopyFrom(previous, snap, s.Id);
                        LastSkippedSources++;
                        continue;
                    }
                    var onlyTables = previous is not null && signal.Changed == true ? signal.Tables : null;
                    s.Snapshot(snap, _settings, hot, onlyTables);
                    if (onlyTables is not null && previous is not null)
                    {
                        LastPartialSources++;
                        FillUnchanged(previous, snap, s.Id);
                    }
                }
                catch (Exception ex) { snap.Warnings.Add($"{s.Id}: {ex.Message}"); Log.Warn($"snapshot {s.Id} failed: {ex.Message}"); }
            }
            _lastSnapshotAt = DateTime.Now;
            SaveState();
            if (sw.ElapsedMilliseconds > 3000) Log.Warn($"snapshot took {sw.ElapsedMilliseconds} ms; consider raising the size caps or narrowing the data paths");
            return snap;
        }
    }

    private static void CopyFrom(DataSnapshot previous, DataSnapshot into, string sourceId)
    {
        if (previous.Databases.TryGetValue(sourceId, out var tables)) into.Databases[sourceId] = tables;
        foreach (var (path, entry) in previous.Files)
            if (path.StartsWith(sourceId, StringComparison.OrdinalIgnoreCase)) into.Files[path] = entry;
    }

    private static void FillUnchanged(DataSnapshot previous, DataSnapshot into, string sourceId)
    {
        if (!previous.Databases.TryGetValue(sourceId, out var prevTables) || !into.Databases.TryGetValue(sourceId, out var tables)) return;
        foreach (var (name, t) in tables.ToList())
            if (t.Skipped && t.Note == "unchanged" && prevTables.TryGetValue(name, out var old)) tables[name] = old;
    }

    /// <summary>Refreshes the rolling "before" snapshot if any source changed since it was taken.</summary>
    public bool RefreshBeforeIfChanged(bool force = false)
    {
        lock (_gate)
        {
            if (_sources.Count == 0) return false;
            var before = DataSnapshot.Load(_paths.BeforeSnapshot);
            if (!force && _beforeTaken != DateTime.MinValue && before is not null)
            {
                var changed = false;
                foreach (var s in _sources)
                {
                    var c = s.ChangedSince(_beforeTaken);
                    if (c is null || c == true) { changed = true; break; }
                }
                if (!changed) return false;
            }
            var snap = Take(force: true, previous: force ? null : before);
            if (snap is null) return false;
            snap.Save(_paths.BeforeSnapshot);
            _beforeTaken = snap.Taken;
            return true;
        }
    }

    /// <summary>Takes an "after" snapshot (only what signalled change), diffs it against the rolling "before", makes it the new "before".</summary>
    public (DiffResult diff, DateTime beforeTaken, DataSnapshot after, int ms)? CaptureEffect()
    {
        lock (_gate)
        {
            if (_sources.Count == 0) return null;
            var sw = Stopwatch.StartNew();
            var before = DataSnapshot.Load(_paths.BeforeSnapshot);
            var after = Take(force: true, previous: before)!;
            var diff = before is null ? new DiffResult() : SnapshotDiffer.Diff(before, after);
            after.Save(_paths.BeforeSnapshot);
            var beforeTaken = _beforeTaken;
            _beforeTaken = after.Taken;
            if (!diff.IsEmpty)
            {
                _recentChangedTables.Add(new HashSet<string>(diff.Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase));
                while (_recentChangedTables.Count > _settings.HotTableHistory) _recentChangedTables.RemoveAt(0);
                SaveState();
            }
            return (diff, beforeTaken, after, (int)sw.ElapsedMilliseconds);
        }
    }

    private sealed class PersistedState { public List<List<string>> Hot { get; set; } = new(); public Dictionary<string, string> Tokens { get; set; } = new(); }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_paths.HotTables)) return;
            var text = File.ReadAllText(_paths.HotTables);
            if (text.TrimStart().StartsWith("["))
            {
                var lists = JsonSerializer.Deserialize<List<List<string>>>(text);
                if (lists is not null) foreach (var l in lists) _recentChangedTables.Add(new HashSet<string>(l, StringComparer.OrdinalIgnoreCase));
                return;
            }
            var st = JsonSerializer.Deserialize<PersistedState>(text);
            if (st is null) return;
            foreach (var l in st.Hot) _recentChangedTables.Add(new HashSet<string>(l, StringComparer.OrdinalIgnoreCase));
            _tokens = st.Tokens;
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(_paths.Snapshots);
            File.WriteAllText(_paths.HotTables, JsonSerializer.Serialize(new PersistedState { Hot = _recentChangedTables.Select(s => s.ToList()).ToList(), Tokens = _tokens }));
        }
        catch { }
    }
}
