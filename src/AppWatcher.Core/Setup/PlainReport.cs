using System.Text;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;

namespace AppWatcher.Core.Setup;

/// <summary>The counts and problems a non-technical user needs, from the data folder or from a learn.</summary>
public sealed class PlainFacts
{
    public string AppName { get; set; } = "";
    public int Screens { get; set; }
    public int Actions { get; set; }
    public int Pictures { get; set; }
    public int Days { get; set; }
    public DateTime? LastLearn { get; set; }
    public bool TreesFailed { get; set; }
    public bool NoPresses { get; set; }
    public bool NoData { get; set; }
    public bool PicturesOff { get; set; }
    public bool Recording => Screens > 0 || Actions > 0;
}

/// <summary>
/// One wording, used in four places: memory/health.md ("In plain words"), the first-hour health.md written even when
/// no learn ran, Settings > Simple ("What has been collected so far") and the first-hour tray notice.
/// What happened, what is missing, and one sentence each on what would fix it. No technical terms.
/// </summary>
public static class PlainReport
{
    public static PlainFacts FromDisk(AppPaths paths, WatcherConfig cfg, string appName)
    {
        var s = CollectorStatus.Load(paths.Root);
        var f = new PlainFacts { AppName = appName, LastLearn = cfg.LastLearn };
        try { f.Screens = Directory.Exists(paths.Screens) ? Directory.GetFiles(paths.Screens, "*.md").Length : 0; } catch { }
        try
        {
            var shots = Path.Combine(paths.Screens, "shots");
            f.Pictures = Directory.Exists(shots) ? Directory.EnumerateFiles(shots, "*.png", SearchOption.AllDirectories).Count() : 0;
        }
        catch { }
        try
        {
            f.Days = Directory.Exists(paths.Events)
                ? Directory.GetFiles(paths.Events, "*.jsonl").Select(x => Path.GetFileNameWithoutExtension(x)).Where(x => x.Length >= 10).Select(x => x[..10]).Distinct().Count()
                : 0;
        }
        catch { }
        if (s is not null)
        {
            f.Actions = s.PressesTotal + s.ValuesTyped + s.Toggles;
            f.TreesFailed = s.WindowsSeen > 0 && s.DumpsOk == 0;
            f.NoPresses = s.WindowsSeen > 2 && s.PressesTotal == 0;
        }
        f.NoData = !cfg.DataSources.Any(d => d.Enabled);
        f.PicturesOff = cfg.Privacy.EffectiveShotMode == "off";
        return f;
    }

    /// <summary>From a learn: deterministic (no clock), so two learns of the same evidence write the same lines.</summary>
    public static PlainFacts FromLearn(LearnResult r)
    {
        var f = new PlainFacts { AppName = r.AppName, Screens = r.Screens.Count, Days = r.Health.Days, NoData = !r.HasDataSources };
        if (r.Collector is { } c)
        {
            f.Actions = c.PressesTotal + c.ValuesTyped + c.Toggles;
            f.Pictures = c.Pictures;
            f.TreesFailed = c.WindowsSeen > 0 && c.DumpsOk == 0;
            f.NoPresses = c.WindowsSeen > 2 && c.PressesTotal == 0;
        }
        else
        {
            f.Actions = r.Health.PressesSeen + r.Health.PressesInferred + r.Screens.Sum(sc => sc.FieldTyped.Values.Sum());
        }
        return f;
    }

    /// <summary>What is missing, and one sentence each on what would fix it; most important first.</summary>
    public static List<(string problem, string fix)> Problems(PlainFacts f)
    {
        var app = f.AppName.Length > 0 ? f.AppName : "the program";
        var list = new List<(string, string)>();
        if (!f.Recording) list.Add(("Nothing has been recorded yet.", $"Open {app} and use it as usual; I only record while it is the window in front."));
        if (f.TreesFailed) list.Add(("I cannot read the fields and buttons of its screens.", "That happens with programs that draw their own screens; I read the text on the pictures instead, so keep \"Read the text on those pictures\" switched on."));
        if (f.NoPresses) list.Add(("I have not seen any button or menu clicks yet.", "Keep using the program as usual; if this stays, I learn from the screens it opens instead of the clicks."));
        if (f.NoData) list.Add(("I cannot see this program's data.", "That is fine for documenting how it is used; to learn its tables too, someone from IT can add a read-only connection in Settings, Advanced."));
        if (f.PicturesOff) list.Add(("Pictures of the screens are switched off.", "Switch on \"Keep pictures\" in Settings if you want them for comparing screens later."));
        return list;
    }

    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    public static string Summary(PlainFacts f) =>
        $"So far I recorded {Plural(f.Screens, "screen")}, {Plural(f.Actions, "action")} and {Plural(f.Pictures, "picture")}{(f.Days > 0 ? $" over {Plural(f.Days, "day")}" : "")}.";

    /// <summary>"First hour done: 12 screens and 140 actions recorded. I cannot see this program's data; open Settings for what that means."</summary>
    public static string TrayFirstHour(PlainFacts f)
    {
        var head = $"First hour done: {Plural(f.Screens, "screen")} and {Plural(f.Actions, "action")} recorded.";
        var p = Problems(f);
        return p.Count == 0
            ? head + " Everything is being recorded; there is nothing you need to do."
            : $"{head} {p[0].problem.TrimEnd('.')}; open Settings for what that means.";
    }

    /// <summary>The sentences for Settings > Simple and for health.md. With <paramref name="withLastLearn"/> also when I last updated what I learned (not in memory files: they must not depend on the clock).</summary>
    public static List<string> Lines(PlainFacts f, bool withLastLearn)
    {
        var lines = new List<string> { Summary(f) };
        if (withLastLearn)
            lines.Add(f.LastLearn is { } t
                ? $"I last updated what I learned on {t:d MMM} at {t:HH:mm}."
                : "I have not updated what I learned yet; that happens every night and one hour after the start.");
        var p = Problems(f);
        if (p.Count == 0) lines.Add("Nothing is missing that I know of; keep using the program as usual.");
        foreach (var (problem, fix) in p) lines.Add($"{problem} {fix}");
        return lines;
    }

    public static string Section(PlainFacts f)
    {
        var sb = new StringBuilder("## In plain words\n\n");
        foreach (var l in Lines(f, withLastLearn: false)) sb.Append("- ").Append(l).Append('\n');
        return sb.Append('\n').ToString();
    }

    public static string FirstHourMarkdown(PlainFacts f) =>
        $"# Health (first hour)\n\nApp: **{f.AppName}**. Written one hour after the start, before the first full learn. " +
        "The next learn (nightly, or Update what I learned) replaces this page with the full health report.\n\n" + Section(f);

    /// <summary>One hour after the first start: writes memory/health.md when no learn has written one yet. Returns true when written.</summary>
    public static bool WriteFirstHourHealth(AppPaths paths, WatcherConfig cfg, string appName)
    {
        try
        {
            Directory.CreateDirectory(paths.Memory);
            var p = Path.Combine(paths.Memory, "health.md");
            if (File.Exists(p)) return false;
            File.WriteAllText(p, FirstHourMarkdown(FromDisk(paths, cfg, appName)));
            return true;
        }
        catch (Exception ex) { Log.Warn("first-hour health not written: " + ex.Message); return false; }
    }
}
