using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Correlates values typed on a screen with the columns that changed right after a save-type action.</summary>
public static class FieldMapper
{
    public static readonly TimeSpan LookBack = TimeSpan.FromMinutes(10);

    public static (List<FieldMapping> mappings, List<ScreenWrites> writes) Learn(List<UiEvent> events, List<EffectRecord> effects)
    {
        var mappings = new Dictionary<(string, string, string, string), FieldMapping>();
        var writes = new Dictionary<(string, string), ScreenWrites>();
        var perScreenEffects = new Dictionary<string, int>();
        var sorted = events.Where(e => e.Act is Acts.Value or Acts.Select).OrderBy(e => e.T).ToList();

        foreach (var fx in effects.Where(f => !f.Diff.IsEmpty))
        {
            var screen = Titles.Key(fx.Win);
            if (screen.Length == 0) screen = "(unknown window)";
            perScreenEffects[screen] = perScreenEffects.GetValueOrDefault(screen) + 1;
            var typed = TypedBefore(sorted, screen, fx.T);

            foreach (var td in fx.Diff.Tables)
            {
                var wk = (screen, td.QualifiedName);
                if (!writes.TryGetValue(wk, out var w)) writes[wk] = w = new ScreenWrites { Screen = screen, Table = td.QualifiedName };
                w.Count++;
                w.Evidence.Add(fx.Source);

                foreach (var (col, val) in NewValues(td))
                {
                    if (val is null || val.Trim().Length < 2) continue;
                    if (val is "true" or "false") continue;
                    foreach (var (field, tv) in typed)
                    {
                        if (!Values.SameValue(val, tv)) continue;
                        var mk = (screen, field, td.QualifiedName, col);
                        if (!mappings.TryGetValue(mk, out var m))
                            mappings[mk] = m = new FieldMapping { Screen = screen, Field = field, Table = td.QualifiedName, Column = col };
                        m.Evidence.Add(fx.Source);
                    }
                }
            }
        }
        foreach (var w in writes.Values) w.ScreenEffects = perScreenEffects[w.Screen];
        return (mappings.Values.OrderBy(m => m.Screen).ThenByDescending(m => m.Evidence.Seen).ToList(),
                writes.Values.OrderBy(w => w.Screen).ThenByDescending(w => w.Count).ToList());
    }

    /// <summary>Last value per field typed or selected on the screen in the look-back window before t.</summary>
    public static List<(string field, string value)> TypedBefore(List<UiEvent> sortedValueEvents, string screenKey, DateTime t)
    {
        var last = new Dictionary<string, string>();
        var from = t - LookBack;
        foreach (var e in sortedValueEvents)
        {
            if (e.T < from) continue;
            if (e.T > t) break;
            if (Titles.Key(e.Win) != screenKey) continue;
            if (string.IsNullOrEmpty(e.Val) || e.Val == Masking.Masked) continue;
            last[e.FieldKey] = e.Val;
        }
        return last.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static IEnumerable<(string col, string? val)> NewValues(TableDiff td)
    {
        foreach (var row in td.Added.Values)
            foreach (var (c, v) in row) yield return (c, v);
        foreach (var fields in td.Changed.Values)
            foreach (var (c, cv) in fields) yield return (c, cv.New);
    }
}
