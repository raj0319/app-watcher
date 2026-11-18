using System.Text.Json.Serialization;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sql;

namespace AppWatcher.Core.Learning;

public sealed class LearnInput
{
    public string AppName { get; set; } = "";
    public List<UiEvent> Events { get; set; } = new();
    public List<EffectRecord> Effects { get; set; } = new();
    public DataSnapshot? Latest { get; set; }
    public List<ScreenRecord> Screens { get; set; } = new();
    public List<SqlStatement> SqlStatements { get; set; } = new();
    public List<string> Triggers { get; set; } = new() { "save", "post", "ok", "apply", "submit", "print", "delete", "send" };
    public bool SqlCaptureEnabled { get; set; }
    public bool HasDataSources { get; set; } = true;
    /// <summary>"Now" for deciding which sessions are closed; tests set it.</summary>
    public DateTime? Now { get; set; }
}

/// <summary>How sure we are, and where to look. Every line in memory/*.md carries one of these.</summary>
public sealed class Evidence
{
    public int Seen { get; set; }
    public bool Inferred { get; set; }
    public List<string> Files { get; set; } = new();

    public void Add(string file, int n = 1)
    {
        Seen += n;
        if (Files.Count < 3 && !Files.Contains(file)) Files.Add(file);
    }

    [JsonIgnore] public string Confidence => Inferred ? (Seen > 0 ? $"inferred, supported {Seen}x" : "inferred") : $"seen {Seen}x";
    [JsonIgnore] public string Pointer => Files.Count == 0 ? "(no file)" : string.Join(", ", Files);
    public override string ToString() => $"{Confidence}; evidence: {Pointer}";
}

public sealed class FlowStat
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public double MedianMs { get; set; }
    public List<string> CommonPath { get; set; } = new();
    public int CommonPathCount { get; set; }
    public string? BackFrom { get; set; }
    public int BackFromCount { get; set; }
    /// <summary>window -> (control name, count) most used on that step.</summary>
    public Dictionary<string, List<(string name, int count)>> StepControls { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
    public string SafeName => Paths.SafeName(Key);
}

public sealed class NavEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Median time spent on From before moving to To.</summary>
    public double MedianMs { get; set; }
}

public sealed class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public bool Nullable { get; set; }
    public string Shape { get; set; } = "";
    public int Distinct { get; set; }
    public bool IsPk { get; set; }
}

public sealed class TableInfo
{
    public string Db { get; set; } = "";
    public string Table { get; set; } = "";
    public string Qualified => $"{Path.GetFileName(Db.Replace("sqlserver:", ""))} / {Table}";
    public List<string> PrimaryKey { get; set; } = new();
    public long RowCount { get; set; }
    public bool Skipped { get; set; }
    public string? Note { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public Evidence WrittenEvidence { get; set; } = new();
}

public sealed class FieldMapping
{
    public string Screen { get; set; } = "";
    public string Field { get; set; } = "";
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    /// <summary>Value-correlation observations (one per effect where the typed value showed up in the column).</summary>
    public Evidence Evidence { get; set; } = new();
    /// <summary>Hashes of the distinct values that supported the mapping; never the values themselves.</summary>
    public HashSet<string> ValueHashes { get; set; } = new();
    /// <summary>Observations from captured SQL statements (exact; outrank value correlation).</summary>
    public int SqlSeen { get; set; }
    public List<string> SqlFiles { get; set; } = new();

    [JsonIgnore] public bool IsSeen => SqlSeen > 0 || ValueHashes.Count >= 3;
    [JsonIgnore] public string Status => IsSeen ? "seen" : "candidate";
    [JsonIgnore] public string Label
    {
        get
        {
            var parts = new List<string>();
            if (Evidence.Seen > 0) parts.Add($"{Evidence.Seen} value match{(Evidence.Seen == 1 ? "" : "es")}, {ValueHashes.Count} distinct");
            if (SqlSeen > 0) parts.Add($"{SqlSeen} SQL statement{(SqlSeen == 1 ? "" : "s")}");
            return $"{Status} ({string.Join("; ", parts)})";
        }
    }
    [JsonIgnore] public string Pointer => string.Join(", ", SqlFiles.Concat(Evidence.Files).Distinct().Take(3));
}

public sealed class ScreenWrites
{
    public string Screen { get; set; } = "";
    public string Table { get; set; } = "";
    public int Count { get; set; }
    public int ScreenEffects { get; set; }
    public int SqlCount { get; set; }
    public bool Always => ScreenEffects >= 2 && Count == ScreenEffects;
    public Evidence Evidence { get; set; } = new();
}

public sealed class Relation
{
    public string FromTable { get; set; } = "";
    public string FromColumn { get; set; } = "";
    public string ToTable { get; set; } = "";
    public string ToColumn { get; set; } = "";
    public int SnapshotMatches { get; set; }
    public int SnapshotRows { get; set; }
    public bool NameHint { get; set; }
    public Evidence Evidence { get; set; } = new();
}

public sealed class Formula
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string Expression { get; set; } = "";
    /// <summary>identity | sum-of-children | product | percentage | linear</summary>
    public string Method { get; set; } = "";
    public List<string> Alternatives { get; set; } = new();
    public int Explained { get; set; }
    public int Tested { get; set; }
    public int Residual => Tested - Explained;
    public List<string> ResidualKeys { get; set; } = new();
    public bool Rounded2 { get; set; }
    /// <summary>How many effects changed this column without a typed value for it.</summary>
    public int UntypedChanges { get; set; }
    public Evidence Evidence { get; set; } = new();
}

public sealed class ValidationRule
{
    public string Screen { get; set; } = "";
    public string Field { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Message { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
}

public sealed class Transition
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public Evidence Evidence { get; set; } = new();
    [JsonIgnore] public bool Rare => Evidence.Seen < 2;
}

public sealed class StatusFlow
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public List<string> States { get; set; } = new();
    public Dictionary<string, int> Distribution { get; set; } = new();
    public Dictionary<string, int> Initial { get; set; } = new();
    public List<Transition> Transitions { get; set; } = new();
    public List<(string from, string to)> NeverSeen { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
}

public sealed class Sequence
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string? Prefix { get; set; }
    public bool Dense { get; set; }
    public Evidence Evidence { get; set; } = new();
}

public sealed class ScreenInfo
{
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
    public int ControlCount { get; set; }
    public List<string> Fields { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    /// <summary>field -> distinct values seen typed/selected (raw; mask before export; never written to memory).</summary>
    public Dictionary<string, List<string>> FieldValues { get; set; } = new();
    public int Visits { get; set; }
}

public sealed class Module
{
    public string Name { get; set; } = "";
    public List<string> Screens { get; set; } = new();
    public List<string> Tables { get; set; } = new();
}

/// <summary>Tables written according to captured SQL statements (exact evidence).</summary>
public sealed class SqlTableWrite
{
    public string Table { get; set; } = "";
    public string Kind { get; set; } = "";
    public HashSet<string> Columns { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
}

public sealed class HealthMetrics
{
    public int Days { get; set; }
    public double EventsPerDay { get; set; }
    public int TriggerInvokes { get; set; }
    public int EffectsTotal { get; set; }
    public int EffectsWithDiff { get; set; }
    public double SavesWithDiffPct { get; set; }
    public int FieldsSeen { get; set; }
    public int FieldsMapped { get; set; }
    public int FieldsCandidate { get; set; }
    public double FieldsMappedPct { get; set; }
    public List<string> Advice { get; set; } = new();
}

public sealed class LearnResult
{
    public DateTime LearnedAt { get; set; } = DateTime.Now;
    public string AppName { get; set; } = "";
    public int EventCount { get; set; }
    public int EffectCount { get; set; }
    public int SessionCount { get; set; }
    public DateTime? FirstEvent { get; set; }
    public DateTime? LastEvent { get; set; }
    public string? MainWindow { get; set; }
    public bool Incremental { get; set; }
    public List<string> Sources { get; set; } = new();
    public List<FlowStat> Flows { get; set; } = new();
    public List<NavEdge> Navigation { get; set; } = new();
    public List<TableInfo> Tables { get; set; } = new();
    public List<FieldMapping> FieldMappings { get; set; } = new();
    public List<ScreenWrites> ScreenWrites { get; set; } = new();
    public List<Relation> Relations { get; set; } = new();
    public List<Formula> Formulas { get; set; } = new();
    public List<ValidationRule> Rules { get; set; } = new();
    public List<StatusFlow> StatusFlows { get; set; } = new();
    public List<Sequence> Sequences { get; set; } = new();
    public List<ScreenInfo> Screens { get; set; } = new();
    public List<Module> Modules { get; set; } = new();
    public List<SqlTableWrite> SqlWrites { get; set; } = new();
    public HealthMetrics Health { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Window titles vary by document name and counters; keys collapse those so flows line up.</summary>
public static class Titles
{
    public static string Key(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Replace("*", "").Replace("•", "").Trim();
        var sb = new System.Text.StringBuilder();
        var inDigits = false;
        foreach (var ch in t)
        {
            if (char.IsDigit(ch)) { if (!inDigits) sb.Append('#'); inDigits = true; }
            else { sb.Append(ch); inDigits = false; }
        }
        return sb.ToString();
    }
}

public static class ValuePattern
{
    public static string Of(string? v)
    {
        if (v is null) return "null";
        if (v == Masking.Masked) return "masked";
        var t = v.Trim();
        if (t.Length == 0) return "empty";
        if (t.Length > 50) return "long text (>50)";
        if (t.All(char.IsDigit)) return "digits";
        if (Values.TryDecimal(t, out var d)) return d < 0 ? "negative number" : "decimal";
        if (DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) return "date";
        if (t.All(c => char.IsLetter(c) || c == ' ')) return "letters";
        return "mixed";
    }
}
