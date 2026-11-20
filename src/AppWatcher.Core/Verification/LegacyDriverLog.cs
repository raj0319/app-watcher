using System.Text.RegularExpressions;

namespace AppWatcher.Core.Verification;

/// <summary>
/// Reads the text logs the test PC's PowerShell drivers wrote (`drive-*.log`: "HH:mm:ss  press 'Exit' (mouse)") as a run,
/// so the measured runs of 2026-11-19 score through the same scorer as new runs. Lines it understands: OPEN X (mouse),
/// OPEN menu A > B > C, press 'X' (mouse), nav 'X', hover 'X', prompt 'T' buttons [..] -> B, focused ... -> type,
/// qty N -> ..., type into id=..., typed into 'X' + Enter. Everything else is kept as a note.
/// </summary>
public static partial class LegacyDriverLog
{
    [GeneratedRegex(@"^\s*(\d\d):(\d\d):(\d\d)\s+(.*)$")] private static partial Regex Line();
    [GeneratedRegex(@"^OPEN (.+?) \((mouse|invoke)\)$")] private static partial Regex OpenLine();
    [GeneratedRegex(@"^OPEN menu (.+)$")] private static partial Regex MenuLine();
    [GeneratedRegex(@"^press '([^']*)' \((mouse|invoke|none)\)$")] private static partial Regex PressLine();
    [GeneratedRegex(@"^nav '([^']*)'( not found)?$")] private static partial Regex NavLine();
    [GeneratedRegex(@"^hover '([^']*)'$")] private static partial Regex HoverLine();
    [GeneratedRegex(@"^prompt '([^']*)' buttons \[[^\]]*\] -> (\w+)$")] private static partial Regex PromptLine();
    [GeneratedRegex(@"^(focused .* -> type|type into .*|typed into .*|qty (\d+) -> .*)$")] private static partial Regex TypeLine();
    [GeneratedRegex(@"^button '([^']*)' not found$")] private static partial Regex NotFoundLine();

    public static List<RunStep> Parse(IEnumerable<string> lines, DateOnly date)
    {
        var run = new List<RunStep>();
        var i = 0;
        foreach (var raw in lines)
        {
            var m = Line().Match(raw.TrimStart('﻿'));
            if (!m.Success) continue;
            var t = new DateTime(date.Year, date.Month, date.Day, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var text = m.Groups[4].Value.Trim();
            ScenarioStep? step = null; var status = "done"; string? note = null;
            Match x;
            if ((x = OpenLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Open, Target = x.Groups[1].Value };
            else if ((x = MenuLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Menu, Path = x.Groups[1].Value.Split(" > ").Select(p => p.Trim()).ToList() };
            else if ((x = PressLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Press, Target = x.Groups[1].Value };
            else if ((x = NavLine().Match(text)).Success) { step = new ScenarioStep { Do = StepKind.Nav, Target = x.Groups[1].Value }; if (x.Groups[2].Success) { status = "blocked"; note = "not found"; } }
            else if ((x = HoverLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Hover, Target = x.Groups[1].Value };
            else if ((x = PromptLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Prompt, Title = x.Groups[1].Value, Button = x.Groups[2].Value };
            else if ((x = TypeLine().Match(text)).Success) step = new ScenarioStep { Do = StepKind.Type, Field = "(see log)", Value = x.Groups[2].Success ? x.Groups[2].Value : null, Expect = x.Groups[2].Success ? null : new Expectation { Optional = true } };
            else if ((x = NotFoundLine().Match(text)).Success) { step = new ScenarioStep { Do = StepKind.Press, Target = x.Groups[1].Value }; status = "blocked"; note = "not found"; }
            if (step is null) continue;
            run.Add(new RunStep { T = t, Index = ++i, Step = step, Status = status, Note = note });
        }
        return run;
    }
}
