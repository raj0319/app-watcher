using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Tests;

/// <summary>Builders for synthetic evidence used across the tests.</summary>
internal static class Synthetic
{
    public static TableSnapshot Table(string[] columns, string[] pk, params string?[][] rows)
    {
        var t = new TableSnapshot { Columns = columns.ToList(), PrimaryKey = pk.ToList() };
        foreach (var r in rows)
        {
            var row = new Dictionary<string, string?>();
            for (var i = 0; i < columns.Length; i++) row[columns[i]] = r[i];
            t.Rows[Values.RowKey(row, t.PrimaryKey)] = row;
        }
        t.RowCount = t.Rows.Count;
        return t;
    }

    public static DataSnapshot Snapshot(string db, params (string name, TableSnapshot table)[] tables)
    {
        var s = new DataSnapshot();
        s.Databases[db] = tables.ToDictionary(t => t.name, t => t.table);
        return s;
    }

    public static UiEvent Ev(DateTime t, string act, string win, string name, string? val = null, string ct = "Edit", string src = "events/test.jsonl#L1")
        => new() { T = t, Act = act, Win = win, Name = name, Val = val, Ct = ct, Source = src };

    public static EffectRecord Effect(DateTime t, string win, DiffResult diff, string file = "fx.json")
        => new() { T = t, Win = win, Reason = "invoke", TriggerName = "Save", Diff = diff, File = file };

    public static TableDiff Added(string db, string table, params Dictionary<string, string?>[] rows)
    {
        var d = new TableDiff { Db = db, Table = table };
        var n = 0;
        foreach (var r in rows) d.Added[r.TryGetValue("id", out var id) && id is not null ? id : (++n).ToString()] = r;
        return d;
    }

    public static Dictionary<string, string?> Row(params (string col, string? val)[] cells) => cells.ToDictionary(c => c.col, c => c.val);
}
