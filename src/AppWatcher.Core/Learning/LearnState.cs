using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppWatcher.Core.Learning;

public sealed class WindowAcc { public double Seconds { get; set; } public int Visits { get; set; } }

public sealed class FlowAcc
{
    public int Count { get; set; }
    public List<double> Durations { get; set; } = new();
    public Dictionary<string, int> Paths { get; set; } = new();
    public Dictionary<string, int> Backs { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> Controls { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
}

public sealed class NavAcc { public int Count { get; set; } public List<double> Durations { get; set; } = new(); }

public sealed class StatusAcc
{
    public Dictionary<string, Evidence> Transitions { get; set; } = new();
    public Dictionary<string, int> Initial { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
}

public sealed class SeqAcc
{
    public long LastMax { get; set; } = long.MinValue;
    public string? Prefix { get; set; }
    public Evidence Evidence { get; set; } = new();
}

/// <summary>
/// Everything the learner has accumulated from closed sessions so far. Persisted as memory/learn-state.json.
/// Counters are additive per session, so processing new sessions on top of this state gives the same
/// result as recomputing from day one. Snapshot-based findings are recomputed at every learn.
/// </summary>
public sealed class LearnState
{
    public const string Sep = "\u0001";
    /// <summary>5: calculations from grid cells and pictures need three distinct values, fills follow the typed cell into its document, tab names masked (an older state is relearned from the events).</summary>
    public const int CurrentVersion = 5;
    public int Version { get; set; } = CurrentVersion;
    public DateTime? Watermark { get; set; }
    public HashSet<string> EffectFiles { get; set; } = new();
    public HashSet<string> SqlSources { get; set; } = new();
    public int Sessions { get; set; }
    public int EventCount { get; set; }
    public DateTime? FirstEvent { get; set; }
    public DateTime? LastEvent { get; set; }
    public Dictionary<string, int> EventsPerDay { get; set; } = new();
    public int TriggerInvokes { get; set; }
    public int EffectsTotal { get; set; }
    public int EffectsWithDiff { get; set; }
    public Dictionary<string, WindowAcc> Windows { get; set; } = new();
    public Dictionary<string, FlowAcc> Flows { get; set; } = new();
    public Dictionary<string, NavAcc> Nav { get; set; } = new();
    public Dictionary<string, FieldMapping> Mappings { get; set; } = new();
    public Dictionary<string, ScreenWrites> Writes { get; set; } = new();
    public Dictionary<string, int> ScreenEffects { get; set; } = new();
    public Dictionary<string, ValidationRule> Rules { get; set; } = new();
    public Dictionary<string, StatusAcc> Status { get; set; } = new();
    public Dictionary<string, SeqAcc> Sequences { get; set; } = new();
    public Dictionary<string, Evidence> Untyped { get; set; } = new();
    public Dictionary<string, Evidence> TableWrites { get; set; } = new();
    public Dictionary<string, Evidence> Relations { get; set; } = new();
    public Dictionary<string, ScreenInfo> Screens { get; set; } = new();
    public Dictionary<string, SqlTableWrite> SqlWrites { get; set; } = new();
    /// <summary>win, cause ("typed:X"), field the program filled right after it.</summary>
    public Dictionary<string, Evidence> Fills { get; set; } = new();
    /// <summary>win, cause: how often the user did it (typed, chose, pressed, ticked).</summary>
    public Dictionary<string, int> Causes { get; set; } = new();
    /// <summary>win, field: how often the program filled a number (or a copy) in that field.</summary>
    public Dictionary<string, int> CalcTests { get; set; } = new();
    /// <summary>win, field, formula: how often the formula gave the value shown.</summary>
    public Dictionary<string, Evidence> CalcFits { get; set; } = new();
    /// <summary>win, field: hashes (never the values) of the distinct numbers the program showed there, so a formula seen on one value twice is not "seen 2×".</summary>
    public Dictionary<string, HashSet<string>> CalcValues { get; set; } = new();
    /// <summary>win, field of calculations whose number came from a grid cell or a picture: those need three distinct values and no alternative before they are written.</summary>
    public HashSet<string> CalcWeak { get; set; } = new();
    /// <summary>win, button, window or message it led to, "window" | "message".</summary>
    public Dictionary<string, Evidence> Leads { get; set; } = new();
    /// <summary>menu, item: menu items seen when a menu opened.</summary>
    public Dictionary<string, Evidence> MenuSeen { get; set; } = new();
    /// <summary>win, control, tip text: hover tips the program showed.</summary>
    public Dictionary<string, Evidence> Hints { get; set; } = new();
    public int PressesSeen { get; set; }
    public int PressesInferred { get; set; }
    public int DialogsSeen { get; set; }
    /// <summary>Tab documents selected (select events on a TabItem), to compare with the tab screens the collector recorded.</summary>
    public int TabSelections { get; set; }
    /// <summary>Document titles ("Window › Document") that events named, so the health page can list the ones never recorded as a screen.</summary>
    public HashSet<string> DocumentsSeen { get; set; } = new();
    /// <summary>screen → focus moves between its fields (the user worked there); compared with the typed values recorded on it.</summary>
    public Dictionary<string, int> FieldFocuses { get; set; } = new();
    /// <summary>screen → typed values recorded (by the user) on it.</summary>
    public Dictionary<string, int> TypedValues { get; set; } = new();
    /// <summary>Picture text layers already fed to the calculation learner (screens/shots/.../*.ocr.json).</summary>
    public HashSet<string> OcrFiles { get; set; } = new();
    /// <summary>screen → label → the number last shown on a picture of it (so the next picture can tell what changed).</summary>
    public Dictionary<string, Dictionary<string, string>> OcrLast { get; set; } = new();
    /// <summary>Hashes of Windows user names and machine names seen in start events (never the names).</summary>
    public HashSet<string> Users { get; set; } = new();
    public HashSet<string> Machines { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false, IncludeFields = false };

    public static string Key(params string[] parts) => string.Join(Sep, parts);

    public static LearnState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<LearnState>(File.ReadAllText(path), Opts);
                if (s is not null && s.Version == CurrentVersion) return s;
            }
        }
        catch (Exception ex) { Log.Warn("learn state unreadable, starting fresh: " + ex.Message); }
        return new LearnState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public LearnState Clone() => JsonSerializer.Deserialize<LearnState>(JsonSerializer.Serialize(this, Opts), Opts)!;

    /// <summary>Short hash of a value so distinct observations can be counted without keeping the value.</summary>
    public static string ValueHash(string value)
    {
        var norm = value.Trim().ToLowerInvariant();
        if (Snapshots.Values.TryDecimal(norm, out var d)) norm = d.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)))[..12];
    }
}
