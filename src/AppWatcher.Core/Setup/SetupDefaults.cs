using System.Diagnostics;
using System.Text;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Sources;

namespace AppWatcher.Core.Setup;

/// <summary>Everything App Watcher finds out about a program before watching it. Nothing is read from its data.</summary>
public sealed class SetupPlan
{
    public string ExePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public ToolkitInfo Toolkit { get; set; } = new();
    public AppProfile? Profile { get; set; }
    /// <summary>program | toolkit | generic</summary>
    public string ProfileReason { get; set; } = "generic";
    public List<BackendInfo> Backends { get; set; } = new();
    public List<DataCandidate> Candidates { get; set; } = new();
}

/// <summary>
/// The program decides; the user only confirms. One place that turns a chosen program into a complete, maximal
/// configuration, shared by the first-run wizard, Settings (Change program) and the scripted install (--setup),
/// so all three give the same result.
/// </summary>
public static class SetupDefaults
{
    /// <summary>Kinds of backend that mean "the data is in a database server I would need a login for".</summary>
    public static readonly HashSet<string> ServerKinds = new(StringComparer.OrdinalIgnoreCase)
        { "sqlserver", "mysql", "postgresql", "oracle", "pervasive", "universe", "firebird", "db2", "progress", "sqlanywhere", "cache", "mongodb", "h2", "redis", "odbc" };

    private static readonly HashSet<string> NoiseExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".log", ".tmp", ".etl", ".pf", ".lock", ".bak", ".old", ".exe", ".dll", ".ldf", ".mdf" };

    /// <summary>Looks at the program file, its config files, the data sources on this PC and its open connections.</summary>
    public static SetupPlan Prepare(string? exePath, string processName, string? friendly = null, int? pid = null)
    {
        var p = new SetupPlan
        {
            ExePath = exePath ?? "",
            ProcessName = processName,
            FriendlyName = string.IsNullOrWhiteSpace(friendly) ? FriendlyName(exePath, null, processName) : friendly.Trim(),
        };
        try { p.Toolkit = ToolkitDetector.Detect(exePath); } catch { }
        string? product = null;
        try { if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) product = FileVersionInfo.GetVersionInfo(exePath).ProductName; } catch { }
        try { (p.Profile, p.ProfileReason) = ProfileStore.MatchWithReason(exePath, processName, product, p.Toolkit.Name); } catch { }
        if (pid is null)
        {
            try
            {
                var procs = Process.GetProcessesByName(processName);
                pid = procs.FirstOrDefault()?.Id;
                foreach (var pr in procs) pr.Dispose();
            }
            catch { }
        }
        try { p.Backends = BackendDetector.Detect(exePath, processName, pid, p.FriendlyName); } catch { }
        try { p.Candidates = DataSourceScanner.Scan(exePath, processName, p.FriendlyName); } catch { }
        return p;
    }

    /// <summary>
    /// The name a person knows the program by: its product name or file description (not "Microsoft Windows
    /// Operating System"), with a short company acronym in front when the product name alone is short ("DDI inForm"),
    /// else the window title, else the process name.
    /// </summary>
    public static string FriendlyName(string? exePath, string? windowTitle, string processName)
    {
        try
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                var product = (vi.ProductName ?? "").Trim();
                var desc = (vi.FileDescription ?? "").Trim();
                var company = (vi.CompanyName ?? "").Trim();
                var systemProduct = product.Contains("Operating System", StringComparison.OrdinalIgnoreCase);
                var name = !systemProduct && product.Length is > 1 and <= 40 ? product : desc.Length > 1 ? desc : product;
                if (name.Length > 1)
                {
                    var first = company.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    var acronym = first.Length is >= 2 and <= 5 && first.All(char.IsUpper);
                    if (acronym && name.Length <= 14 && !name.StartsWith(first, StringComparison.OrdinalIgnoreCase) && !systemProduct) name = first + " " + name;
                    return name;
                }
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var t = windowTitle.Trim();
            var dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 && t.Length - dash - 3 is > 1 and < 40 ? t[(dash + 3)..] : (t.Length > 40 ? t[..40] : t);
        }
        return processName;
    }

    /// <summary>The recommended (maximal, safe, local) values for everything a user could change.</summary>
    public static void ApplyRecommended(WatcherConfig cfg)
    {
        cfg.EffectTriggers = WatcherConfig.DefaultTriggers.ToList();
        cfg.Snapshot = new SnapshotSettings();
        cfg.LearnAt = "02:00";
        cfg.RetentionDays = 180;
        cfg.Collector = new CollectorSettings();
        cfg.Privacy = new PrivacySettings();
        cfg.Profile = null;
    }

    /// <summary>Turns a plan into the configuration: the program, recommended values, its profile, and every safe data source ticked.</summary>
    public static void Apply(WatcherConfig cfg, SetupPlan plan, AppProfile? profileOverride = null)
    {
        cfg.App = new WatchedApp { ProcessName = plan.ProcessName, ExePath = plan.ExePath, DisplayName = plan.FriendlyName, ChosenAt = DateTime.Now };
        cfg.Toolkit = plan.Toolkit.Name;
        cfg.Backends = plan.Backends;
        ApplyRecommended(cfg);
        (profileOverride ?? plan.Profile)?.ApplyTo(cfg);
        cfg.DataSources = DefaultSources(plan.Candidates);
        cfg.AutoDetectPending = true;
        cfg.HealthCheckDone = false;
        cfg.FirstStart = DateTime.Now;
        cfg.Paused = false;
        cfg.PausedUntil = null;
    }

    /// <summary>Every candidate the scanner found is ticked, except files that need a database server login to read (.mdf).</summary>
    public static List<DataSourceConfig> DefaultSources(IEnumerable<DataCandidate> candidates)
    {
        var list = new List<DataSourceConfig>();
        foreach (var c in candidates)
        {
            if (list.Any(s => string.Equals(s.Path, c.Path, StringComparison.OrdinalIgnoreCase))) continue;
            var cfg = DataSourceScanner.ToConfig(c);
            cfg.Enabled = c.Kind != "sqlserver-file";
            list.Add(cfg);
        }
        return list;
    }

    /// <summary>
    /// Adds what the file-access detection saw the program read or write: data files and anything it writes,
    /// minus logs, temp files and program files. Returns how many were added (all ticked: read only, safe).
    /// </summary>
    public static int AddTracedSources(WatcherConfig cfg, IEnumerable<(string path, int reads, int writes)> traced)
    {
        var added = 0;
        foreach (var (path, reads, writes) in traced.OrderByDescending(t => t.writes))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var ext = Path.GetExtension(path);
                if (NoiseExtensions.Contains(ext)) continue;
                if (!DataSourceScanner.DataExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && writes == 0) continue;
                if (cfg.DataSources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var kind = SqliteSource.IsSqlite(path) ? "sqlite" : ext.Equals(".mdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file";
                cfg.DataSources.Add(new DataSourceConfig { Kind = kind, Path = path, Label = Path.GetFileName(path), Enabled = true });
                added++;
            }
            catch { }
        }
        return added;
    }

    private static bool AppSpecific(BackendInfo b) => !b.Where.Contains("ODBC DSN", StringComparison.OrdinalIgnoreCase);

    /// <summary>The green box: what I found and what I will learn, in plain words, no technical terms.</summary>
    public static string WhatIWillLearn(SetupPlan p)
    {
        var sb = new StringBuilder();
        sb.Append(p.FriendlyName.TrimEnd('.')).Append(". ");
        if (p.ProfileReason == "program" && p.Profile is not null) sb.Append($"I recognise this program (profile: {p.Profile.Name}). ");
        else if (p.ProfileReason == "toolkit") sb.Append("I know how programs like this one are built. ");
        var drawsOwn = ToolkitDetector.ProfileFor(p.Toolkit.Name) is "custom-drawn" or "java";
        sb.Append(drawsOwn
            ? "This program draws its own screens, so I will record its windows, the pictures of each screen, the text on them and the messages it shows. "
            : "I will record its screens, buttons, fields, the pictures of each screen and the messages it shows. ");
        var localDb = p.Candidates.Count(c => c.Kind is "sqlite" or "access");
        var files = p.Candidates.Count;
        var server = p.Backends.Where(AppSpecific).FirstOrDefault(b => ServerKinds.Contains(b.Kind));
        if (localDb > 0)
            sb.Append($"I found where it keeps its data on this PC, so I will also learn which screens change which records.");
        else if (server is not null)
            sb.Append("Its data lives on a server I cannot read, so I will not learn its tables. That is fine for documenting how the program is used." +
                      (files > 0 ? $" I will still watch the {files} file{(files == 1 ? "" : "s")} it keeps on this PC." : ""));
        else if (files > 0)
            sb.Append($"I found {files} file{(files == 1 ? "" : "s")} it uses on this PC and will watch {(files == 1 ? "it" : "them")} for changes. If its real data is somewhere else, I will learn less about its tables; that is fine for documenting how it is used.");
        else
            sb.Append("I have not found where it keeps its data yet. I will look once more while it runs; either way that is fine for documenting how the program is used.");
        return sb.ToString().Trim();
    }

    /// <summary>The "Details" expander: the technical facts behind the green box.</summary>
    public static string Details(SetupPlan p)
    {
        var lines = new List<string>
        {
            $"Program file: {(p.ExePath.Length > 0 ? p.ExePath : "(not found; watching by process name " + p.ProcessName + ")")}",
            $"Process name: {p.ProcessName}",
            $"Built with: {p.Toolkit.Name}{(p.Toolkit.Hints.Count > 0 ? " (" + string.Join("; ", p.Toolkit.Hints.Take(4)) + ")" : "")}",
            $"Profile: {p.Profile?.Name ?? "none"} ({p.ProfileReason}){(p.Profile is null ? "" : " - " + p.Profile.Description)}",
            $"Data places found on this PC ({p.Candidates.Count}): {(p.Candidates.Count == 0 ? "none" : string.Join("; ", p.Candidates.Take(6).Select(c => $"{c.Kind} {c.Path}")))}",
            $"Servers and connections: {(p.Backends.Count == 0 ? "none found" : string.Join("; ", p.Backends.Take(6).Select(b => b.Describe())))}",
        };
        return string.Join("\r\n", lines);
    }
}
