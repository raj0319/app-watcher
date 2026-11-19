using System.Text.Json;
using System.Text.Json.Serialization;
using AppWatcher.Core.Learning;

namespace AppWatcher.Core;

public sealed class WatcherConfig
{
    public const string AppVersion = "0.6";

    /// <summary>Words on a button or menu item that mean "this writes data", in the languages we meet most. Profiles add their own.</summary>
    public static readonly string[] DefaultTriggers =
    {
        "save", "post", "ok", "apply", "submit", "print", "delete", "send", "update", "accept", "finalize", "finalise", "process", "commit", "confirm", "release", "approve", "void", "complete",
        "guardar", "speichern", "enregistrer", "salva", "salvar", "opslaan", "zapisz", "gem", "spara", "tallenna", "сохранить", "保存", "저장", "保存する",
    };

    public int Version { get; set; } = 1;
    public WatchedApp? App { get; set; }
    /// <summary>DPAPI-protected PBKDF2 record ("v1:salt:hash"). Null until the first run completes.</summary>
    public string? PasswordProtected { get; set; }
    public List<DataSourceConfig> DataSources { get; set; } = new();
    public List<string> EffectTriggers { get; set; } = DefaultTriggers.ToList();
    public SnapshotSettings Snapshot { get; set; } = new();
    /// <summary>Local time "HH:mm" at which the nightly learn runs.</summary>
    public string LearnAt { get; set; } = "02:00";
    public ApiSettings Api { get; set; } = new();
    public LocalAnalysisSettings Local { get; set; } = new();
    public SqlCaptureSettings SqlCapture { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public CollectorSettings Collector { get; set; } = new();
    /// <summary>Name of the app profile applied at setup (profiles/*.json), if any.</summary>
    public string? Profile { get; set; }
    /// <summary>UI toolkit detected from the exe (WinForms, WPF, Infragistics, DevExpress, Qt, Delphi, Electron, Java, ...).</summary>
    public string? Toolkit { get; set; }
    /// <summary>Backends found by discovery; never credentials.</summary>
    public List<BackendInfo> Backends { get; set; } = new();
    /// <summary>Days of raw events kept after they were learned; 0 keeps everything.</summary>
    public int RetentionDays { get; set; } = 180;
    public bool Paused { get; set; }
    /// <summary>When set, "Pause for an hour" ends at this time and watching resumes by itself.</summary>
    public DateTime? PausedUntil { get; set; }
    public bool HealthCheckDone { get; set; }
    public DateTime? LastLearn { get; set; }
    /// <summary>When the user pressed "Start watching" (wizard, --setup, or a new program in Settings). The first-hour report counts from here.</summary>
    public DateTime? FirstStart { get; set; }
    /// <summary>The two-minute file-access detection should run once, by itself, as soon as the program is running.</summary>
    public bool AutoDetectPending { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool IsFirstRun => App is null || string.IsNullOrEmpty(PasswordProtected);
}

public sealed class WatchedApp
{
    /// <summary>Process name without ".exe", e.g. "notepad".</summary>
    public string ProcessName { get; set; } = "";
    public string? ExePath { get; set; }
    /// <summary>Name used for the data folder, e.g. "notepad".</summary>
    public string DisplayName { get; set; } = "";
    public DateTime ChosenAt { get; set; } = DateTime.Now;
}

public sealed class DataSourceConfig
{
    /// <summary>file | folder | sqlite | access | sqlserver | odbc | dropfolder</summary>
    public string Kind { get; set; } = "file";
    public string? Path { get; set; }
    /// <summary>For sqlserver and odbc: DPAPI-protected connection string.</summary>
    public string? ConnectionStringProtected { get; set; }
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;

    public string Describe() => Kind switch
    {
        "sqlserver" => $"sqlserver: {Label ?? "connection"}",
        "odbc" => $"odbc: {Label ?? "connection"}",
        "dropfolder" => $"export drop folder: {Path}",
        _ => $"{Kind}: {Path}",
    };
}

public sealed class SnapshotSettings
{
    /// <summary>Databases bigger than this only get their "hot" tables snapshotted.</summary>
    public long MaxDbBytes { get; set; } = 256L * 1024 * 1024;
    public int MaxRowsPerTable { get; set; } = 50_000;
    public int MinSecondsBetweenSnapshots { get; set; } = 5;
    public int AfterDelayMs { get; set; } = 2000;
    /// <summary>A table is "hot" when it changed in one of the last N diffs.</summary>
    public int HotTableHistory { get; set; } = 10;
    public int IdleSecondsBeforeSnapshot { get; set; } = 10;
    public int MaxFileBytesToHash { get; set; } = 64 * 1024 * 1024;
}

public sealed class ApiSettings
{
    public bool Enabled { get; set; }
    public string? ApiKeyProtected { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";
    public string Endpoint { get; set; } = "https://api.anthropic.com/v1/messages";
    public int MaxTokens { get; set; } = 8000;
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>OpenAI-compatible endpoint on the client's own machine (Ollama by default). Off by default.</summary>
public sealed class LocalAnalysisSettings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "llama3.1";
    public string? ApiKeyProtected { get; set; }
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>Opt-in SQL statement evidence: an Extended Events (.xel) folder, a CSV export, a text log, or the Windows ODBC trace.</summary>
public sealed class SqlCaptureSettings
{
    public bool Enabled { get; set; }
    /// <summary>Folder with .xel/.csv/.log files (SQL Server, MySQL general log, PostgreSQL log_statement, ...), or the ODBC SQL.LOG file.</summary>
    public string? LogPath { get; set; }
    /// <summary>True when App Watcher itself switched Windows ODBC tracing on (so it can switch it off again).</summary>
    public bool OdbcTracingOn { get; set; }
}

/// <summary>What stays out of the files. Titles of windows and tabs often carry record ids and names.</summary>
public sealed class PrivacySettings
{
    /// <summary>Replace quoted parts of window titles and tab labels ("ACME INC") with "…" in memory and export files.</summary>
    public bool MaskTitles { get; set; } = true;
    /// <summary>Pictures of the app's window under screens/shots/. Kept for older configs; ScreenshotMode decides.</summary>
    public bool Screenshots { get; set; } = true;
    /// <summary>off | first (one picture per screen, first time seen) | actions (one per distinct state: after each press, tab, typed value, dialog, dropdown; deduped and capped). Never the desktop.</summary>
    public string ScreenshotMode { get; set; } = "actions";
    public int MaxShotsPerScreen { get; set; } = 25;
    public int MaxShotMegabytes { get; set; } = 500;
    /// <summary>Read the text on each state picture with the built-in Windows OCR and store it next to the picture (for apps that draw their own screens). Local, no install.</summary>
    public bool ReadTextFromPictures { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public string EffectiveShotMode => !Screenshots ? "off" : ScreenshotMode is "off" or "first" or "actions" ? ScreenshotMode : "actions";
    /// <summary>Window title patterns (* wildcards) that are never recorded at all (payroll, HR).</summary>
    public List<string> ExcludeWindows { get; set; } = new();
    /// <summary>When not empty, only windows whose title matches one of these patterns are recorded.</summary>
    public List<string> AllowlistWindows { get; set; } = new();
}

/// <summary>How the collector reads the app; the profile sets these, the user can change them.</summary>
public sealed class CollectorSettings
{
    /// <summary>Read the tree through IAccessible when UI Automation shows fewer than ten controls.</summary>
    public bool MsaaFallback { get; set; } = true;
    /// <summary>Count a focus on a button or menu item followed within half a second by a window change as a press.</summary>
    public bool PressInference { get; set; } = true;
    /// <summary>Dump the selected tab's content as a screen of its own ("Window › Tab") and attribute events to it.</summary>
    public bool TabDocuments { get; set; } = true;
    public int DumpDepth { get; set; } = 14;
    public int DumpRowCap { get; set; } = 3000;
    /// <summary>Rows kept per grid or list in a tree dump; the rest becomes one note row with the count and column names.</summary>
    public int GridRowCap { get; set; } = 5;
    /// <summary>Re-dump a window whose first dump had fewer than ten controls, at most this often.</summary>
    public int RedumpSeconds { get; set; } = 60;
    /// <summary>Title patterns (* wildcards) of splash, login and station windows; flows start after them.</summary>
    public List<string> SessionWindows { get; set; } = new();
    /// <summary>Title patterns (* wildcards) of windows to ignore completely (tooltips, progress popups).</summary>
    public List<string> IgnoreWindows { get; set; } = new();
    /// <summary>Title patterns of windows that are always message boxes.</summary>
    public List<string> DialogWindows { get; set; } = new();
}

public static class ConfigStore
{
    private static readonly object Gate = new();

    public static WatcherConfig Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    var json = File.ReadAllText(Paths.ConfigFile);
                    var cfg = JsonSerializer.Deserialize<WatcherConfig>(json, WatcherConfig.JsonOptions);
                    if (cfg is not null) return cfg;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read config, starting fresh", ex);
            }
            return new WatcherConfig();
        }
    }

    public static void Save(WatcherConfig cfg)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Paths.Root);
            var tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, WatcherConfig.JsonOptions));
            File.Move(tmp, Paths.ConfigFile, overwrite: true);
        }
    }
}
