using AppWatcher.Core.Models;

namespace AppWatcher.Core.Events;

/// <summary>
/// Turns one value-changed event per keystroke into one event per typed value: the value is committed
/// when focus moves on (any other event arrives) or when nothing changed for the quiet time.
/// </summary>
public sealed class ValueDebouncer
{
    private readonly Dictionary<string, (UiEvent ev, DateTime at)> _pending = new();
    private readonly object _gate = new();

    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(1000);

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Stores or replaces the pending value for the control.</summary>
    public void Push(UiEvent valueEvent, DateTime now)
    {
        // keyed by the field, not the window: the keystrokes of one value can be credited to two windows while a popup flickers
        // in front (the notes window a record opens), and the program's own fills are kept apart from the user's typing
        var key = (valueEvent.Aid.Length > 0 && !Screens.Labels.IsWrapperId(valueEvent.Aid) ? valueEvent.Aid : valueEvent.FieldKey) + "\u0001" + valueEvent.Ct + "\u0001" + (valueEvent.By ?? "");
        lock (_gate) _pending[key] = (valueEvent, now);
    }

    /// <summary>Returns the values whose quiet time has passed (or everything when <paramref name="all"/>), oldest first.</summary>
    public List<UiEvent> Flush(DateTime now, bool all = false)
    {
        lock (_gate)
        {
            var keys = _pending.Where(p => all || now - p.Value.at >= Quiet).Select(p => p.Key).ToList();
            var list = keys.Select(k => _pending[k].ev).OrderBy(e => e.T).ToList();
            foreach (var k in keys) _pending.Remove(k);
            return list;
        }
    }
}
