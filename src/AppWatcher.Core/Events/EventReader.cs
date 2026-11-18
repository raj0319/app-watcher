using System.Text.Json;
using AppWatcher.Core.Models;

namespace AppWatcher.Core.Events;

public static class EventReader
{
    public static List<UiEvent> ReadAll(string eventsDir, DateTime? since = null)
    {
        var list = new List<UiEvent>();
        if (!Directory.Exists(eventsDir)) return list;
        foreach (var file in Directory.GetFiles(eventsDir, "*.jsonl").OrderBy(f => f))
        {
            // file names are "yyyy-MM-dd.jsonl", or "yyyy-MM-dd.<machine>.jsonl" after a merge
            var stem = Path.GetFileNameWithoutExtension(file);
            if (since is not null && stem.Length >= 10 && DateTime.TryParse(stem[..10], out var d) && d.Date < since.Value.Date) continue;
            list.AddRange(ReadFile(file));
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    public static IEnumerable<UiEvent> ReadFile(string file)
    {
        var name = "events/" + Path.GetFileName(file);
        var n = 0;
        // the writer keeps today's file open for append; share it fully so learn/export can run meanwhile
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            n++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            UiEvent? e = null;
            try { e = JsonSerializer.Deserialize<UiEvent>(line); } catch { /* skip bad line */ }
            if (e is null) continue;
            e.Source = $"{name}#L{n}";
            yield return e;
        }
    }
}
