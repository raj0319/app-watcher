using System.Text;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;

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
    /// <summary>Many windows seen, and every press came from a fallback (UIA 0, MSAA 0): clicks are being guessed.</summary>
    public bool PressesFallbackOnly { get; set; }
    /// <summary>Windows named like a prompt that were recorded as screens while no dialog was counted.</summary>
    public int PromptScreens { get; set; }
    /// <summary>Share of control tree reads that failed, in percent (reported from 25%).</summary>
    public int DumpFailPct { get; set; }
    /// <summary>A tab document was selected more often than tab screens were recorded.</summary>
    public bool TabsUnrecorded { get; set; }
    /// <summary>Documents the user opened that were never recorded as a screen, by title.</summary>
    public List<string> DocumentsNeverRecorded { get; set; } = new();
    /// <summary>Screens the user typed on (the focus moved between fields) where no typed value was recorded.</summary>
    public List<string> TypedButUnrecorded { get; set; } = new();
    public bool Recording => Screens > 0 || Actions > 0;
}

/// <summary>
/// One wording, used in four places: memory/health.md ("In plain words"), the first-hour health.md written even when
/// no learn ran, Settings > Simple ("What has been collected so far") and the first-hour tray notice.
/// What happened, what is missing, and one sentence each on what would fix it. No technical terms.
/// It says what the collector could not do, so a page never claims that nothing is missing while clicks are
/// guessed, message boxes were taken for screens, screen reads failed or a tab was never recorded.
/// </summary>
public static class PlainReport
{
    public const int FallbackWindows = 50;
    public const int DumpFailFrom = 25;

    public static PlainFacts FromDisk(AppPaths paths, WatcherConfig cfg, string appName)
    {
        var s = CollectorStatus.Load(paths.Root);
        var f = new PlainFacts { AppName = appName, LastLearn = cfg.LastLearn };
        List<ScreenRecord> records = new();
        try { records = ScreenWriter.ReadAll(paths.Screens); f.Screens = records.Count; } catch { }
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
            Apply(f, s);
            if (s.Dialogs == 0) f.PromptScreens = records.Count(r => r.Kind == ScreenKinds.Screen && (DialogDetector.LooksLikePromptTitle(r.Title) || DialogDetector.Detect("", r.Controls).isDialog));
            f.TabsUnrecorded = s.MdiDocuments == 0 && s.TabSelections > s.TabScreens && s.DocumentsNeverRecorded.Count == 0; // in an MDI workspace the document list is the truth
            f.DocumentsNeverRecorded = s.DocumentsNeverRecorded.ToList();
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
            Apply(f, c);
            if (c.Dialogs == 0) f.PromptScreens = r.Health.PromptScreens;
            f.TabsUnrecorded = c.MdiDocuments == 0 && Math.Max(c.TabSelections, r.Health.TabSelections) > c.TabScreens && r.Health.DocumentsNeverRecorded.Count == 0;
        }
        else
        {
            f.Actions = r.Health.PressesSeen + r.Health.PressesInferred + r.Screens.Sum(sc => sc.FieldTyped.Values.Sum());
        }
        f.DocumentsNeverRecorded = r.Health.DocumentsNeverRecorded.ToList();
        f.TypedButUnrecorded = r.Health.TypedButUnrecorded.ToList();
        return f;
    }

    private static void Apply(PlainFacts f, CollectorStatus s)
    {
        f.TreesFailed = s.WindowsSeen > 0 && s.DumpsOk == 0;
        f.NoPresses = s.WindowsSeen > 2 && s.PressesTotal == 0;
        f.PressesFallbackOnly = s.WindowsSeen > FallbackWindows && s.PressesUia == 0 && s.PressesMsaa == 0 && s.PressesTotal > 0;
        var dumps = s.DumpsOk + s.DumpsFailed;
        var pct = dumps == 0 ? 0 : (int)(100.0 * s.DumpsFailed / dumps);
        f.DumpFailPct = pct > DumpFailFrom ? pct : 0;
    }

    /// <summary>What is missing, and one sentence each on what would fix it; most important first.</summary>
    public static List<(string problem, string fix)> Problems(PlainFacts f)
    {
        var app = f.AppName.Length > 0 ? f.AppName : "the program";
        var list = new List<(string, string)>();
        if (!f.Recording) list.Add(("Nothing has been recorded yet.", $"Open {app} and use it as usual; I only record while it is the window in front."));
        if (f.TreesFailed) list.Add(("I cannot read the fields and buttons of its screens.", "That happens with programs that draw their own screens; I read the text on the pictures instead, so keep \"Read the text on those pictures\" switched on."));
        if (f.NoPresses) list.Add(("I have not seen any button or menu clicks yet.", "Keep using the program as usual; if this stays, I learn from the screens it opens instead of the clicks."));
        if (f.PressesFallbackOnly) list.Add(("Clicks are only counted by fallback: the program reports none of its button presses itself (UIA 0, MSAA 0), so I count a click when the screen changes right after it.", "Some clicks will be missed; the pictures and the screens that open still show what was done."));
        if (f.PromptScreens > 0) list.Add((f.PromptScreens == 1 ? "1 window that looks like a message box was recorded as a screen." : $"{f.PromptScreens} windows that look like message boxes were recorded as screens.", "Their text is still in the pictures; this version tells message boxes apart by their shape, so the next learn files them as messages."));
        if (f.DumpFailPct > 0) list.Add(($"{f.DumpFailPct}% of screen reads failed.", "The program is slow to answer when a screen opens; I try again, and the pictures with their text fill the gap."));
        if (f.TabsUnrecorded) list.Add(("A tab was selected but never recorded as a screen.", "This version reads the selected tab itself instead of waiting for the program to say so."));
        if (f.DocumentsNeverRecorded.Count > 0)
        {
            var names = string.Join(", ", f.DocumentsNeverRecorded.Take(6).Select(d => d.Contains(" › ") ? d[(d.IndexOf(" › ", StringComparison.Ordinal) + 3)..] : d));
            list.Add((f.DocumentsNeverRecorded.Count == 1 ? $"1 document was opened but never recorded as a screen: {names}." : $"{f.DocumentsNeverRecorded.Count} documents were opened but never recorded as screens: {names}.",
                "Their fields and buttons are unknown; the program did not answer when their screens were read. The pictures still show them."));
        }
        if (f.TypedButUnrecorded.Count > 0)
        {
            var names = string.Join(", ", f.TypedButUnrecorded.Take(6).Select(d => d.Contains(" › ") ? d[(d.IndexOf(" › ", StringComparison.Ordinal) + 3)..] : d));
            list.Add((f.TypedButUnrecorded.Count == 1 ? $"On 1 screen people typed but no typed value was recorded: {names}." : $"On {f.TypedButUnrecorded.Count} screens people typed but no typed value was recorded: {names}.",
                "Its fields do not report what is typed; this version re-reads the screen when the cursor moves on, so the next run should catch them."));
        }
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
