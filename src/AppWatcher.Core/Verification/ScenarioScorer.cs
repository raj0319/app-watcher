using System.Text;
using System.Text.Json;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core.Verification;

/// <summary>The verdict of one step: did the watcher record what the step did?</summary>
public sealed class StepResult
{
    public int Index { get; set; }
    public DateTime T { get; set; }
    public string Step { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>passed | failed | blocked | untested</summary>
    public string Verdict { get; set; } = "untested";
    public string Expected { get; set; } = "";
    public string Found { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
}

/// <summary>The scorecard of a run: per step, per kind, and the presses the watcher recorded that match no step (wrong names).</summary>
public sealed class Scorecard
{
    public string Scenario { get; set; } = "";
    public DateTime RunStart { get; set; }
    public DateTime RunEnd { get; set; }
    public List<StepResult> Steps { get; set; } = new();
    public Dictionary<string, (int passed, int failed, int blocked, int untested)> ByKind { get; set; } = new();
    public int Passed => Steps.Count(s => s.Verdict == "passed");
    public int Failed => Steps.Count(s => s.Verdict == "failed");
    public int Blocked => Steps.Count(s => s.Verdict == "blocked");
    public int Untested => Steps.Count(s => s.Verdict == "untested");
    /// <summary>Invoke events during the run that no step explains: a wrong name or a phantom press.</summary>
    public List<(DateTime t, string name, string how, string win)> UnexplainedPresses { get; set; } = new();
    /// <summary>Typed values recorded as the program's (by: app) right after a type step: the collector lost the typing.</summary>
    public int TypedCreditedToProgram { get; set; }
}

/// <summary>
/// Compares a run (what the driver did, when) with the events the watcher wrote, step by step: a press must show as an
/// invoke of that name within a few seconds, a tab click as a select, typing as a typed value, a prompt as a dialog plus
/// the button, an open as the press that opened it. Pure, so the test PC's old runs score the same way as new ones.
/// </summary>
public static class ScenarioScorer
{
    public static Scorecard Score(string scenarioName, IReadOnlyList<RunStep> run, IReadOnlyList<UiEvent> events)
    {
        var card = new Scorecard { Scenario = scenarioName };
        if (run.Count == 0) return card;
        card.RunStart = run.Min(r => r.T); card.RunEnd = run.Max(r => r.T).AddSeconds(20);
        var inWindow = events.Where(e => e.T >= card.RunStart.AddSeconds(-5) && e.T <= card.RunEnd).OrderBy(e => e.T).ToList();
        var matched = new HashSet<UiEvent>();
        foreach (var r in run)
        {
            var res = new StepResult { Index = r.Index, T = r.T, Step = r.Step.Describe(), Kind = r.Step.Do.ToString().ToLowerInvariant() };
            card.Steps.Add(res);
            var x = Expectation.For(r.Step);
            if (r.Status != "done") { res.Verdict = r.Status == "blocked" ? "blocked" : "failed"; res.Found = r.Note ?? r.Status; continue; }
            if (x is null) { res.Verdict = "untested"; res.Expected = "(nothing to check)"; continue; }
            if (x.Press is null && x.Select is null && x.Typed is null && x.Dialog is null && x.Screen is null && x.Hint is null)
            { res.Verdict = "untested"; res.Expected = "(nothing to check)"; res.Found = "the log has no value"; continue; }
            res.Expected = Describe(x);
            var found = new List<string>(); var ok = true;
            // a legacy log holds whole seconds: a step logged at :15 may have happened at :15.9, so an event at :19.3 is still within 4 s
            var grace = r.T.Millisecond == 0 ? 1.0 : 0.0;
            bool Near(UiEvent e) { var d = (e.T - r.T).TotalSeconds; return d >= -x.WithinSeconds && d <= x.WithinSeconds + grace; }
            if (x.Press is not null)
            {
                var hit = inWindow.FirstOrDefault(e => e.Act == Acts.Invoke && Near(e) && Same(e.Name, x.Press));
                if (hit is not null) { matched.Add(hit); found.Add($"press {hit.Name} ({hit.How ?? "uia"})"); res.Evidence.Add(hit.Source); } else ok = false;
            }
            if (x.Select is not null)
            {
                var hit = inWindow.FirstOrDefault(e => e.Act == Acts.Select && e.Ct == "TabItem" && Near(e) && Same(e.Name, x.Select));
                if (hit is not null) { found.Add($"select {hit.Name}"); res.Evidence.Add(hit.Source); } else ok = false;
            }
            if (x.Typed is not null)
            {
                var hit = inWindow.FirstOrDefault(e => e.Act == Acts.Value && e.By is null && Near(e) && (e.Val ?? "") == x.Typed);
                if (hit is not null) { found.Add($"typed {hit.FieldKey}"); res.Evidence.Add(hit.Source); }
                else
                {
                    ok = false;
                    if (inWindow.Any(e => e.Act == Acts.Value && e.By == ValueBy.App && Near(e) && (e.Val ?? "") == x.Typed)) { card.TypedCreditedToProgram++; found.Add("the value was recorded as the program's"); }
                }
            }
            if (x.Dialog is not null)
            {
                var hit = inWindow.FirstOrDefault(e => e.Act == Acts.Dialog && Near(e) && DialogDetector.Wildcard(e.Name.Length > 0 ? e.Name : e.Win, x.Dialog));
                if (hit is not null) { found.Add($"dialog {hit.Name}"); res.Evidence.Add(hit.Source); } else ok = false;
            }
            if (x.Screen is not null)
            {
                var hit = inWindow.FirstOrDefault(e => (e.Act == Acts.Open || e.Act == Acts.Select && e.Ct == "TabItem") && Near(e) && DialogDetector.Wildcard(e.Win, x.Screen));
                if (hit is not null) { found.Add($"screen {Titles.Key(hit.Win)}"); res.Evidence.Add(hit.Source); } else ok = false;
            }
            if (x.Hint is not null)
            {
                var hit = inWindow.FirstOrDefault(e => e.Act == Acts.Hint && Near(e) && Same(e.Name, x.Hint));
                if (hit is not null) { found.Add($"hint \"{hit.Val}\""); res.Evidence.Add(hit.Source); } else if (!x.Optional) ok = false;
                if (hit is null && x.Optional) { res.Verdict = "untested"; res.Found = "no tip was shown"; continue; }
            }
            res.Found = string.Join("; ", found);
            res.Verdict = ok ? "passed" : "failed";
        }
        // presses the watcher recorded that no step explains, within the run
        foreach (var e in inWindow.Where(e => e.Act == Acts.Invoke && e.How != PressHow.Toggle && !matched.Contains(e)))
        {
            var step = run.FirstOrDefault(r => Math.Abs((r.T - e.T).TotalSeconds) <= 4 && Expectation.For(r.Step)?.Press is { } p && Same(e.Name, p));
            if (step is null) card.UnexplainedPresses.Add((e.T, e.Name, e.How ?? "uia", Titles.Key(e.Win)));
        }
        foreach (var g in card.Steps.GroupBy(s => s.Kind))
            card.ByKind[g.Key] = (g.Count(s => s.Verdict == "passed"), g.Count(s => s.Verdict == "failed"), g.Count(s => s.Verdict == "blocked"), g.Count(s => s.Verdict == "untested"));
        return card;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) || DialogDetector.Wildcard(a.Trim(), b.Trim());

    private static string Describe(Expectation x)
    {
        var parts = new List<string>();
        if (x.Press is not null) parts.Add($"press {x.Press}");
        if (x.Select is not null) parts.Add($"select {x.Select}");
        if (x.Typed is not null) parts.Add("typed value");
        if (x.Dialog is not null) parts.Add($"dialog {x.Dialog}");
        if (x.Screen is not null) parts.Add($"screen {x.Screen}");
        if (x.Hint is not null) parts.Add($"hint on {x.Hint}");
        return string.Join(" + ", parts) + $" within {x.WithinSeconds:0} s";
    }

    /// <summary>The scorecard as the dashboard page: counts, per kind, per step, the unexplained presses.</summary>
    public static string Markdown(Scorecard c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Scorecard: {c.Scenario}");
        sb.AppendLine();
        sb.AppendLine($"Run {c.RunStart:yyyy-MM-dd HH:mm:ss} to {c.RunEnd.AddSeconds(-20):HH:mm:ss}. Each step is what the driver did; the verdict says whether the watcher recorded it.");
        sb.AppendLine();
        sb.AppendLine($"**{c.Passed} passed, {c.Failed} failed, {c.Blocked} blocked, {c.Untested} untested** of {c.Steps.Count} steps.");
        sb.AppendLine();
        sb.AppendLine("| Kind | Passed | Failed | Blocked | Untested |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var (kind, v) in c.ByKind.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.AppendLine($"| {kind} | {v.passed} | {v.failed} | {v.blocked} | {v.untested} |");
        sb.AppendLine();
        sb.AppendLine("| # | Time | Step | Expected | Found | Verdict | Evidence |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in c.Steps) sb.AppendLine($"| {s.Index} | {s.T:HH:mm:ss} | {Cell(s.Step)} | {Cell(s.Expected)} | {Cell(s.Found)} | {s.Verdict} | {Cell(string.Join(", ", s.Evidence))} |");
        sb.AppendLine();
        if (c.TypedCreditedToProgram > 0) sb.AppendLine($"Typed values recorded as the program's: {c.TypedCreditedToProgram}.").AppendLine();
        sb.AppendLine(c.UnexplainedPresses.Count == 0 ? "No press was recorded that the steps do not explain." : $"Presses recorded that no step explains ({c.UnexplainedPresses.Count}): wrong names or phantom presses.");
        foreach (var (t, name, how, win) in c.UnexplainedPresses) sb.AppendLine($"- {t:HH:mm:ss} {name} ({how}) on {win}");
        sb.AppendLine();
        sb.AppendLine("Blocked: the driver could not do the step (control or window not found). Untested: nothing to check (a wait), or a hover that showed no tip.");
        return sb.ToString();
    }

    public static string Json(Scorecard c) => JsonSerializer.Serialize(new
    {
        scenario = c.Scenario, run_start = c.RunStart, run_end = c.RunEnd.AddSeconds(-20),
        passed = c.Passed, failed = c.Failed, blocked = c.Blocked, untested = c.Untested,
        by_kind = c.ByKind.ToDictionary(k => k.Key, k => new { k.Value.passed, k.Value.failed, k.Value.blocked, k.Value.untested }),
        steps = c.Steps.Select(s => new { s.Index, t = s.T, s.Step, s.Kind, s.Expected, s.Found, s.Verdict, s.Evidence }).ToList(),
        typed_credited_to_program = c.TypedCreditedToProgram,
        unexplained_presses = c.UnexplainedPresses.Select(p => new { t = p.t, p.name, p.how, window = p.win }).ToList(),
    }, Scenario.Json) + "\n";

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Reads the driver's run log (one JSON object per line: t, index, step, status, note).</summary>
    public static List<RunStep> ReadRun(string path)
    {
        var list = new List<RunStep>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            var r = JsonSerializer.Deserialize<RunStep>(line, Scenario.Json);
            if (r is not null) list.Add(r);
        }
        return list;
    }
}
