using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppWatcher.Core.Models;

namespace AppWatcher.Core.Events;

/// <summary>Appends events to events/yyyy-MM-dd.jsonl. One open stream, flushed per line, rolled at midnight.</summary>
public sealed class EventWriter : IDisposable
{
    private readonly string _dir;
    private readonly object _gate = new();
    private FileStream? _stream;
    private string _openDate = "";
    private DateTime _last = DateTime.MinValue;
    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public EventWriter(string eventsDir)
    {
        _dir = eventsDir;
        Directory.CreateDirectory(_dir);
    }

    public DateTime LastEventTime { get { lock (_gate) return _last; } }

    public void Write(UiEvent e)
    {
        lock (_gate)
        {
            if (e.T == default) e.T = DateTime.Now;
            e.Dt = _last == DateTime.MinValue ? 0 : (long)Math.Max(0, (e.T - _last).TotalMilliseconds);
            _last = e.T;
            var date = e.T.ToString("yyyy-MM-dd");
            if (_stream is null || date != _openDate)
            {
                _stream?.Dispose();
                _stream = new FileStream(Path.Combine(_dir, date + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
                _openDate = date;
            }
            var line = JsonSerializer.Serialize(e, Opts) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stream?.Dispose(); _stream = null; }
    }
}
