using AppWatcher.Core.Models;

namespace AppWatcher.Core.Events;

/// <summary>
/// Turns one value-changed event per keystroke into one event per typed value. A value is committed when the user moves on
/// (focus to another field, a press, a closed window, a message, a menu) or when nothing changed for the quiet time; the
/// program's own reactions while the user types (a document taking its title, a tab read, a lookup list) do not commit it,
/// so "0002" typed before the record loaded and "0002776" after it are one value, written once with the final window.
/// An empty typed value is never written. A grid editor's value (the editor reports the grid's name, not the column) waits
/// a little longer and is dropped when the cell read reports the same value with its column name.
/// </summary>
public sealed class ValueDebouncer
{
    private readonly Dictionary<string, (UiEvent ev, DateTime at)> _pending = new();
    private readonly List<(UiEvent ev, DateTime at)> _recentCells = new();
    private readonly object _gate = new();

    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(1000);
    /// <summary>How long a grid editor's value waits for the cell read that names its column.</summary>
    public TimeSpan CellWait { get; init; } = TimeSpan.FromMilliseconds(1500);

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    private static string KeyOf(UiEvent e) =>
        (e.Aid.Length > 0 && !Screens.Labels.IsWrapperId(e.Aid) ? e.Aid : e.FieldKey) + "\u0001" + e.Ct + "\u0001" + (e.By ?? "") + "\u0001" + (e.Row ?? "");

    /// <summary>Stores or replaces the pending value for the control.</summary>
    public void Push(UiEvent valueEvent, DateTime now)
    {
        // keyed by the field, not the window: the keystrokes of one value can be credited to two windows while a popup flickers
        // in front or the document takes its title, and the program's own fills are kept apart from the user's typing
        lock (_gate) _pending[KeyOf(valueEvent)] = (valueEvent, now);
    }

    /// <summary>Returns the values whose quiet time has passed (or everything when <paramref name="all"/>), oldest first.</summary>
    public List<UiEvent> Flush(DateTime now, bool all = false)
    {
        lock (_gate) return Take(now, k => all || Due(_pending[k], now));
    }

    /// <summary>
    /// Another event arrived: the values it ends. A press, a closed window, a message, a menu or the end of a session ends
    /// every pending value; a focus move ends every value except the focused field's own; anything the program does by itself
    /// (open, select, foreground, hint, dropdown, result) ends none.
    /// </summary>
    public List<UiEvent> FlushFor(UiEvent other, DateTime now)
    {
        lock (_gate)
        {
            switch (other.Act)
            {
                case Acts.Invoke or Acts.Close or Acts.Dialog or Acts.Menu or Acts.Stop or Acts.Start:
                    return Take(now, _ => true);
                case Acts.Focus:
                    var focused = other.Aid.Length > 0 && !Screens.Labels.IsWrapperId(other.Aid) ? other.Aid : other.FieldKey;
                    return Take(now, k => !k.StartsWith(focused + "\u0001", StringComparison.Ordinal) || Due(_pending[k], now));
                default:
                    return Take(now, k => Due(_pending[k], now));
            }
        }
    }

    private bool Due((UiEvent ev, DateTime at) p, DateTime now) => now - p.at >= (p.ev.OverGridCell ? CellWait : Quiet);

    private List<UiEvent> Take(DateTime now, Func<string, bool> which)
    {
        var keys = _pending.Keys.Where(which).ToList();
        var taken = keys.Select(k => _pending[k]).ToList();
        foreach (var k in keys) _pending.Remove(k);
        _recentCells.RemoveAll(c => now - c.at > CellWait * 4);
        var list = new List<UiEvent>();
        foreach (var (ev, at) in taken.OrderBy(p => p.ev.T))
        {
            if (ev.By is null && string.IsNullOrWhiteSpace(ev.Val)) continue; // never an empty typed value
            if (ev.OverGridCell && SameCellRead(ev, taken)) continue;          // the cell read names the column: the editor's copy goes
            if (ev.By is null && ev.Ct == "DataItem") _recentCells.Add((ev, at));
            list.Add(ev);
        }
        return list;
    }

    /// <summary>A cell value the user typed, read with its column name within the wait, with the same value as the editor's.</summary>
    private bool SameCellRead(UiEvent editor, List<(UiEvent ev, DateTime at)> taken)
    {
        // the same value, or the cell already shows more of it (a late keystroke of the editor); within twice the wait (the row
        // read can come 1.5 s after the editor's event when the program is busy)
        bool Match(UiEvent c) => c.By is null && c.Ct == "DataItem" && (c.Val == editor.Val || c.Val is { Length: > 0 } && editor.Val is { Length: > 0 } && c.Val.StartsWith(editor.Val, StringComparison.Ordinal))
                                 && Math.Abs((c.T - editor.T).TotalMilliseconds) <= 2 * CellWait.TotalMilliseconds;
        return taken.Any(t => Match(t.ev)) || _recentCells.Any(t => Match(t.ev)) || _pending.Values.Any(p => Match(p.ev));
    }
}
