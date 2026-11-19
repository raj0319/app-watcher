using System.Text.Json;

namespace AppWatcher.Core.Models;

/// <summary>
/// What the collector managed to do, written to &lt;app&gt;/collector-status.json every few minutes, so the
/// learner's health page can say why something is missing (tree dumps failed, no presses seen, no data source).
/// </summary>
public sealed class CollectorStatus
{
    public DateTime Updated { get; set; }
    public DateTime Since { get; set; }
    public string Version { get; set; } = "";
    public string? Toolkit { get; set; }
    public string? Profile { get; set; }
    public int WindowsSeen { get; set; }
    public int DumpsOk { get; set; }
    public int DumpsFailed { get; set; }
    public int DumpsEmpty { get; set; }
    public int DumpsMsaa { get; set; }
    public int TabScreens { get; set; }
    public int PressesUia { get; set; }
    public int PressesMsaa { get; set; }
    public int PressesMenu { get; set; }
    public int PressesInferred { get; set; }
    public int Toggles { get; set; }
    public int Dialogs { get; set; }
    public int ValuesTyped { get; set; }
    /// <summary>Values the program filled in itself (a calculated total, a name looked up from a code).</summary>
    public int ValuesFilled { get; set; }
    public int Dropdowns { get; set; }
    public int Results { get; set; }
    public int Pictures { get; set; }
    public int PicturesSkipped { get; set; }
    public int OcrPages { get; set; }
    public int ExcludedEvents { get; set; }
    public bool HasDataSources { get; set; }
    public int EffectsWritten { get; set; }
    public List<string> Notes { get; set; } = new();

    public int PressesTotal => PressesUia + PressesMsaa + PressesMenu + PressesInferred;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string FileName => "collector-status.json";

    public void Save(string appRoot)
    {
        try
        {
            Directory.CreateDirectory(appRoot);
            var p = Path.Combine(appRoot, FileName);
            File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(this, Opts));
            File.Move(p + ".tmp", p, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("collector status not saved: " + ex.Message); }
    }

    public static CollectorStatus? Load(string appRoot)
    {
        var p = Path.Combine(appRoot, FileName);
        // the running watcher replaces this file every few seconds; a read that meets the swap is tried again
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return File.Exists(p) ? JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(p), Opts) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (attempt == 5) { Log.Warn("collector status not read: " + ex.Message); return null; }
                Thread.Sleep(200);
            }
        }
        return null;
    }

    /// <summary>Plain-words problems a reader should know about.</summary>
    public List<string> Problems()
    {
        var list = new List<string>();
        if (WindowsSeen > 0 && DumpsOk == 0) list.Add("no window's control tree could be read (every dump failed or came back empty), so fields and buttons are unknown");
        else if (DumpsFailed + DumpsEmpty > DumpsOk) list.Add($"{DumpsFailed + DumpsEmpty} of {DumpsFailed + DumpsEmpty + DumpsOk} control tree dumps failed or were empty");
        if (DumpsMsaa > 0) list.Add($"{DumpsMsaa} screens were read through the older MSAA interface (names only, no automation ids)");
        if (WindowsSeen > 2 && PressesTotal == 0) list.Add("no button or menu press was seen at all (the app's controls do not report presses); flows have windows but no actions, and saves cannot be tied to data changes");
        else if (PressesUia == 0 && PressesTotal > 0) list.Add($"presses were only seen through fallbacks (MSAA {PressesMsaa}, menus {PressesMenu}, inferred {PressesInferred}); counts are a lower bound");
        if (!HasDataSources) list.Add("no data source is configured, so tables and rules cannot be learned");
        list.AddRange(Notes);
        return list;
    }
}
