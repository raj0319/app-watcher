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

    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(800);

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Stores or replaces the pending value for the control.</summary>
    public void Push(UiEvent valueEvent, DateTime now)
    {
        var key = valueEvent.Win + "\u0001" + valueEvent.FieldKey + "\u0001" + valueEvent.Ct;
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
