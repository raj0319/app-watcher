using System.Text.Json;
using System.Text.Json.Serialization;
using AppWatcher.Core.Models;

namespace AppWatcher.Core.Snapshots;

/// <summary>effects/&lt;timestamp&gt;.json: what changed in the data right after one action.</summary>
public sealed class EffectRecord
{
    public DateTime T { get; set; }
    /// <summary>invoke | file-change | manual</summary>
    public string Reason { get; set; } = "";
    public string Win { get; set; } = "";
    public string TriggerName { get; set; } = "";
    public string TriggerType { get; set; } = "";
    public string TriggerAid { get; set; } = "";
    public string? TriggerEvent { get; set; }
    public DateTime BeforeTaken { get; set; }
    public DateTime AfterTaken { get; set; }
    public DiffResult Diff { get; set; } = new();
    public int SnapshotMs { get; set; }

    [JsonIgnore] public string File { get; set; } = "";
    [JsonIgnore] public string Source => "effects/" + File;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string FileNameFor(DateTime t) => t.ToString("yyyy-MM-ddTHH-mm-ss-fff") + ".json";

    public string Save(string effectsDir)
    {
        Directory.CreateDirectory(effectsDir);
        File = FileNameFor(T);
        System.IO.File.WriteAllText(Path.Combine(effectsDir, File), JsonSerializer.Serialize(this, Opts));
        return File;
    }

    public static List<EffectRecord> ReadAll(string effectsDir)
    {
        var list = new List<EffectRecord>();
        if (!Directory.Exists(effectsDir)) return list;
        foreach (var f in Directory.GetFiles(effectsDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var r = JsonSerializer.Deserialize<EffectRecord>(System.IO.File.ReadAllText(f), Opts);
                if (r is null) continue;
                r.File = Path.GetFileName(f);
                list.Add(r);
            }
            catch (Exception ex) { Log.Warn($"Skipping unreadable effect {f}: {ex.Message}"); }
        }
        return list;
    }

    public IEnumerable<string> TablesChanged => Diff.Tables.Select(t => t.QualifiedName);
}
