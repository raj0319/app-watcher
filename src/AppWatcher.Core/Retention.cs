namespace AppWatcher.Core;

/// <summary>
/// Raw events are the biggest files on a busy PC. Once a day's sessions are learned (they lie before the
/// learner's watermark) and older than the retention window, the day's file can go: the memory is incremental
/// and keeps the counts. Screens, effects and snapshots are never deleted here.
/// </summary>
public static class Retention
{
    /// <summary>Deletes event files older than <paramref name="days"/> whose date is before the watermark. Returns the names deleted.</summary>
    public static List<string> RotateEvents(AppPaths paths, int days, DateTime? watermark, DateTime? now = null)
    {
        var deleted = new List<string>();
        if (days <= 0 || watermark is null || !Directory.Exists(paths.Events)) return deleted;
        var cutoff = (now ?? DateTime.Now).Date.AddDays(-days);
        foreach (var f in Directory.GetFiles(paths.Events, "*.jsonl"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length < 10 || !DateTime.TryParse(stem[..10], out var day)) continue;
            if (day.Date >= cutoff || day.Date >= watermark.Value.Date) continue;
            try { File.Delete(f); deleted.Add(Path.GetFileName(f)); }
            catch (Exception ex) { Log.Warn($"retention could not delete {f}: {ex.Message}"); }
        }
        if (deleted.Count > 0) Log.Info($"retention: deleted {deleted.Count} learned event files older than {days} days");
        return deleted;
    }
}
