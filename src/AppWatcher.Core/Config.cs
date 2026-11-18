using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppWatcher.Core;

public sealed class WatcherConfig
{
    public int Version { get; set; } = 1;
    public WatchedApp? App { get; set; }
    /// <summary>DPAPI-protected PBKDF2 record ("v1:salt:hash"). Null until the first run completes.</summary>
    public string? PasswordProtected { get; set; }
    public List<DataSourceConfig> DataSources { get; set; } = new();
    public List<string> EffectTriggers { get; set; } = new() { "save", "post", "ok", "apply", "submit", "print", "delete", "send" };
    public SnapshotSettings Snapshot { get; set; } = new();
    /// <summary>Local time "HH:mm" at which the nightly learn runs.</summary>
    public string LearnAt { get; set; } = "02:00";
    public ApiSettings Api { get; set; } = new();
    public LocalAnalysisSettings Local { get; set; } = new();
    public SqlCaptureSettings SqlCapture { get; set; } = new();
    public bool Paused { get; set; }
    public bool HealthCheckDone { get; set; }
    public DateTime? LastLearn { get; set; }

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
    /// <summary>file | folder | sqlite | access | sqlserver</summary>
    public string Kind { get; set; } = "file";
    public string? Path { get; set; }
    /// <summary>For sqlserver: DPAPI-protected connection string.</summary>
    public string? ConnectionStringProtected { get; set; }
    public string? Label { get; set; }
    public bool Enabled { get; set; } = true;

    public string Describe() => Kind == "sqlserver" ? $"sqlserver: {Label ?? "connection"}" : $"{Kind}: {Path}";
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

/// <summary>Opt-in SQL statement evidence: an Extended Events (.xel) folder, a CSV export, or the Windows ODBC trace.</summary>
public sealed class SqlCaptureSettings
{
    public bool Enabled { get; set; }
    /// <summary>Folder with .xel/.csv files from scripts/sql/xevents-session.sql, or the ODBC SQL.LOG file.</summary>
    public string? LogPath { get; set; }
    /// <summary>True when App Watcher itself switched Windows ODBC tracing on (so it can switch it off again).</summary>
    public bool OdbcTracingOn { get; set; }
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
