using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sql;

namespace AppWatcher.Core.Learning;

/// <summary>
/// Correlates values typed on a screen with the columns that changed right after a save-type action,
/// and merges exact evidence from captured SQL statements. A mapping is "seen" with at least 3 distinct
/// supporting values or any SQL statement; otherwise it is a "candidate".
/// </summary>
public static class FieldMapper
{
    public static readonly TimeSpan LookBack = TimeSpan.FromMinutes(10);
    public const string SqlPrefix = "sql:";

    /// <summary>Whole-history wrapper (tests).</summary>
    public static (List<FieldMapping> mappings, List<ScreenWrites> writes) Learn(List<UiEvent> events, List<EffectRecord> effects, List<SqlStatement>? sql = null, IEnumerable<string>? knownTables = null)
    {
        var state = new LearnState();
        var sorted = events.OrderBy(e => e.T).ToList();
        foreach (var fx in effects.OrderBy(f => f.T)) AccumulateEffect(state, sorted, fx);
        foreach (var st in (sql ?? new()).OrderBy(s => s.T)) AccumulateSql(state, sorted, st);
        return Finalize(state, knownTables ?? Array.Empty<string>());
    }

    public static void AccumulateEffect(LearnState state, List<UiEvent> sessionEvents, EffectRecord fx)
    {
        if (fx.Diff.IsEmpty) return;
        var screen = Titles.Key(fx.Win);
        if (screen.Length == 0) screen = "(unknown window)";
        state.ScreenEffects[screen] = state.ScreenEffects.GetValueOrDefault(screen) + 1;
        var typed = TypedBefore(sessionEvents, screen, fx.T);

        foreach (var td in fx.Diff.Tables)
        {
            var wk = LearnState.Key(screen, td.QualifiedName);
            if (!state.Writes.TryGetValue(wk, out var w)) state.Writes[wk] = w = new ScreenWrites { Screen = screen, Table = td.QualifiedName };
            w.Count++;
            w.Evidence.Add(fx.Source);

            foreach (var (col, val) in NewValues(td))
            {
                if (!Usable(val)) continue;
                foreach (var (field, tv) in typed)
                {
                    if (!Values.SameValue(val, tv)) continue;
                    var mk = LearnState.Key(screen, field, td.QualifiedName, col);
                    if (!state.Mappings.TryGetValue(mk, out var m))
                        state.Mappings[mk] = m = new FieldMapping { Screen = screen, Field = field, Table = td.QualifiedName, Column = col };
                    m.Evidence.Add(fx.Source);
                    m.ValueHashes.Add(LearnState.ValueHash(val!));
                }
            }
        }
    }

    /// <summary>A captured INSERT/UPDATE says exactly which table and columns a save touched.</summary>
    public static void AccumulateSql(LearnState state, List<UiEvent> sessionEvents, SqlStatement st)
    {
        if (!st.IsWrite || st.Table.Length == 0) return;
        var tableKey = SqlPrefix + st.Table;
        var wk = LearnState.Key(st.Table, st.Kind);
        if (!state.SqlWrites.TryGetValue(wk, out var sw)) state.SqlWrites[wk] = sw = new SqlTableWrite { Table = st.Table, Kind = st.Kind };
        sw.Evidence.Add(st.Source);
        foreach (var c in st.Columns) sw.Columns.Add(c);

        var screen = ScreenAt(sessionEvents, st.T);
        if (screen.Length == 0) return;
        var wk2 = LearnState.Key(screen, tableKey);
        if (!state.Writes.TryGetValue(wk2, out var w)) state.Writes[wk2] = w = new ScreenWrites { Screen = screen, Table = tableKey };
        w.SqlCount++;
        w.Evidence.Add(st.Source);

        var typed = TypedBefore(sessionEvents, screen, st.T);
        foreach (var (col, val) in st.Values)
        {
            if (!Usable(val)) continue;
            foreach (var (field, tv) in typed)
            {
                if (!Values.SameValue(val, tv)) continue;
                var mk = LearnState.Key(screen, field, tableKey, col);
                if (!state.Mappings.TryGetValue(mk, out var m))
                    state.Mappings[mk] = m = new FieldMapping { Screen = screen, Field = field, Table = tableKey, Column = col };
                m.SqlSeen++;
                if (m.SqlFiles.Count < 3 && !m.SqlFiles.Contains(st.Source)) m.SqlFiles.Add(st.Source);
                m.ValueHashes.Add(LearnState.ValueHash(val!));
            }
        }
    }

    /// <summary>Resolves "sql:table" names to the snapshot's qualified names, merges, applies the seen/candidate rule and SQL precedence.</summary>
    public static (List<FieldMapping> mappings, List<ScreenWrites> writes) Finalize(LearnState state, IEnumerable<string> knownTables)
    {
        var known = knownTables.ToList();
        string Resolve(string table)
        {
            if (!table.StartsWith(SqlPrefix)) return table;
            var bare = table[SqlPrefix.Length..];
            var hit = known.FirstOrDefault(k => k.EndsWith(" / " + bare, StringComparison.OrdinalIgnoreCase) || k.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase));
            return hit ?? "sql / " + bare;
        }

        var merged = new Dictionary<string, FieldMapping>();
        foreach (var m in state.Mappings.Values)
        {
            var table = Resolve(m.Table);
            var key = LearnState.Key(m.Screen, m.Field, table, m.Column);
            if (!merged.TryGetValue(key, out var t)) merged[key] = t = new FieldMapping { Screen = m.Screen, Field = m.Field, Table = table, Column = m.Column };
            t.Evidence.Seen += m.Evidence.Seen;
            foreach (var f in m.Evidence.Files) if (t.Evidence.Files.Count < 3 && !t.Evidence.Files.Contains(f)) t.Evidence.Files.Add(f);
            t.SqlSeen += m.SqlSeen;
            foreach (var f in m.SqlFiles) if (t.SqlFiles.Count < 3 && !t.SqlFiles.Contains(f)) t.SqlFiles.Add(f);
            t.ValueHashes.UnionWith(m.ValueHashes);
        }
        // SQL evidence resolves conflicts: a field that maps to several columns keeps only the SQL-backed ones when any exist
        var result = new List<FieldMapping>();
        foreach (var g in merged.Values.GroupBy(m => LearnState.Key(m.Screen, m.Field)))
        {
            var list = g.ToList();
            if (list.Any(m => m.SqlSeen > 0)) list = list.Where(m => m.SqlSeen > 0).ToList();
            result.AddRange(list);
        }

        var writes = new Dictionary<string, ScreenWrites>();
        foreach (var w in state.Writes.Values)
        {
            var table = Resolve(w.Table);
            var key = LearnState.Key(w.Screen, table);
            if (!writes.TryGetValue(key, out var t)) writes[key] = t = new ScreenWrites { Screen = w.Screen, Table = table };
            t.Count += w.Count; t.SqlCount += w.SqlCount;
            foreach (var f in w.Evidence.Files) if (t.Evidence.Files.Count < 3 && !t.Evidence.Files.Contains(f)) t.Evidence.Files.Add(f);
            t.Evidence.Seen += w.Evidence.Seen;
        }
        foreach (var w in writes.Values) w.ScreenEffects = state.ScreenEffects.GetValueOrDefault(w.Screen);

        return (result.OrderBy(m => m.Screen).ThenBy(m => m.Field).ThenByDescending(m => m.SqlSeen).ThenByDescending(m => m.ValueHashes.Count).ThenBy(m => m.Table).ThenBy(m => m.Column).ToList(),
                writes.Values.OrderBy(w => w.Screen).ThenByDescending(w => w.Count + w.SqlCount).ThenBy(w => w.Table).ToList());
    }

    private static bool Usable(string? val) => val is not null && val.Trim().Length >= 2 && val is not ("true" or "false") && val != Masking.Masked;

    /// <summary>Last value per field typed or selected on the screen in the look-back window before t.</summary>
    public static List<(string field, string value)> TypedBefore(List<UiEvent> sortedEvents, string screenKey, DateTime t)
    {
        var last = new Dictionary<string, string>();
        var from = t - LookBack;
        foreach (var e in sortedEvents)
        {
            if (e.T < from) continue;
            if (e.T > t) break;
            if (e.Act is not (Acts.Value or Acts.Select)) continue;
            if (Titles.Key(e.Win) != screenKey) continue;
            if (string.IsNullOrEmpty(e.Val) || e.Val == Masking.Masked) continue;
            last[e.FieldKey] = e.Val;
        }
        return last.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>The window of the watched app that was in front at time t (last event before t within the look-back).</summary>
    public static string ScreenAt(List<UiEvent> sortedEvents, DateTime t)
    {
        if (t == DateTime.MinValue) return "";
        string win = "";
        foreach (var e in sortedEvents)
        {
            if (e.T > t) break;
            if (t - e.T > LookBack) continue;
            var k = Titles.Key(e.Win);
            if (k.Length > 0) win = k;
        }
        return win;
    }

    private static IEnumerable<(string col, string? val)> NewValues(TableDiff td)
    {
        foreach (var row in td.Added.Values)
            foreach (var (c, v) in row) yield return (c, v);
        foreach (var fields in td.Changed.Values)
            foreach (var (c, cv) in fields) yield return (c, cv.New);
    }
}
