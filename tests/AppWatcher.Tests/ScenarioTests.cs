using AppWatcher.Core.Events;
using AppWatcher.Core.Models;
using AppWatcher.Core.Verification;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// Milestone M3: scenarios, the scorer and the legacy driver logs. The anonymised rerun of 2026-11-19 (four flows on the
/// real ERP, driven by the test PC's scripts) must score exactly as it was scored by hand then: opens 4 of 4, Exit 1 of 4,
/// prompts 0 of 2, Add Line Item 0 of 1, sub-tabs 1 of 17, and the five wrong-name presses listed as unexplained.
/// </summary>
public class ScenarioTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "inform-v0.9");

    [Fact]
    public void The_rerun_of_the_v09_run_scores_as_it_was_scored_by_hand()
    {
        var run = LegacyDriverLog.Parse(File.ReadLines(Path.Combine(Fixtures, "driver-rerun.log")), new DateOnly(2026, 11, 19));
        var events = EventReader.ReadFile(Path.Combine(Fixtures, "events-2026-11-19.jsonl")).ToList();
        // the log's times are the test PC's wall clock (-05:00); the events were read into this PC's local time
        foreach (var r in run) r.T = new DateTimeOffset(r.T, TimeSpan.FromHours(-5)).LocalDateTime;
        var card = ScenarioScorer.Score("rerun", run, events);
        Assert.Equal(4, card.ByKind["open"].passed);
        var presses = card.Steps.Where(s => s.Kind == "press").ToList();
        Assert.Equal(1, presses.Count(s => s.Step == "press Exit" && s.Verdict == "passed"));
        Assert.Equal(3, presses.Count(s => s.Step == "press Exit" && s.Verdict == "failed"));
        Assert.Equal(0, presses.Count(s => s.Step == "press Add Line Item" && s.Verdict == "passed"));
        Assert.Equal((0, 2, 0, 0), card.ByKind["prompt"]);
        Assert.Equal(17, card.ByKind["nav"].passed + card.ByKind["nav"].failed);
        Assert.Equal(1, card.ByKind["nav"].passed);
        Assert.Equal(2, card.Steps.Count(s => s.Kind == "type" && s.Verdict == "passed")); // qty 5 and qty 20
        Assert.True(card.UnexplainedPresses.Count >= 5, $"{card.UnexplainedPresses.Count} unexplained presses (Exit, Cancel, Exit, Exit, Edit were the wrong names)");
        Assert.Contains(card.UnexplainedPresses, p => p.name == "Cancel");
        Assert.True(card.ByKind["hover"].untested >= 15, "hovers without a tip are untested, not failed");
        var md = ScenarioScorer.Markdown(card);
        Assert.Contains("| open | 4 | 0 | 0 | 0 |", md);
        Assert.Contains("Presses recorded that no step explains", md);
        var json = ScenarioScorer.Json(card);
        Assert.Contains("\"unexplained_presses\"", json);
    }

    [Fact]
    public void A_scenario_round_trips_through_json_and_its_steps_carry_default_expectations()
    {
        var s = new Scenario
        {
            Name = "sample", App = "Sample ERP", Window = "Sample ERP*",
            Steps =
            {
                new ScenarioStep { Do = StepKind.Press, Target = "Refresh" },
                new ScenarioStep { Do = StepKind.Open, Target = "Workspace", Expect = new Expectation { Press = "Workspace", Screen = "*Workspace › Sales Order Entry", WithinSeconds = 8 } },
                new ScenarioStep { Do = StepKind.Nav, Target = "Notes" },
                new ScenarioStep { Do = StepKind.Type, Field = "txtDocQty", Value = "4", Then = "tab" },
                new ScenarioStep { Do = StepKind.Prompt, Title = "Confirm?", Button = "OK" },
                new ScenarioStep { Do = StepKind.Menu, Path = new() { "Help", "About" } },
                new ScenarioStep { Do = StepKind.Hover, Target = "Refresh" },
                new ScenarioStep { Do = StepKind.Wait, Ms = 500 },
            },
        };
        var json = s.Save();
        var back = System.Text.Json.JsonSerializer.Deserialize<Scenario>(json, Scenario.Json)!;
        Assert.Equal(8, back.Steps.Count);
        Assert.Equal("press", json.Contains("\"do\": \"press\"") ? "press" : "");
        Assert.Equal("Refresh", Expectation.For(back.Steps[0])!.Press);
        Assert.Equal("Notes", Expectation.For(back.Steps[2])!.Select);
        Assert.Equal("4", Expectation.For(back.Steps[3])!.Typed);
        Assert.Equal("Confirm?", Expectation.For(back.Steps[4])!.Dialog); Assert.Equal("OK", Expectation.For(back.Steps[4])!.Press);
        Assert.Equal("About", Expectation.For(back.Steps[5])!.Press);
        Assert.True(Expectation.For(back.Steps[6])!.Optional);
        Assert.Null(Expectation.For(back.Steps[7]));
        // the scorer on synthetic events: a press seen, a press missed, a blocked step, a wait
        var t = new DateTime(2026, 11, 20, 10, 0, 0);
        var run = new List<RunStep>
        {
            new() { T = t, Index = 1, Step = back.Steps[0] },
            new() { T = t.AddSeconds(10), Index = 2, Step = back.Steps[2] },
            new() { T = t.AddSeconds(20), Index = 3, Step = back.Steps[3], Status = "blocked", Note = "field not found" },
            new() { T = t.AddSeconds(30), Index = 4, Step = back.Steps[7] },
            new() { T = t.AddSeconds(40), Index = 5, Step = back.Steps[6] },
        };
        var events = new List<UiEvent>
        {
            Synthetic.Ev(t.AddSeconds(1), Acts.Invoke, "Sample ERP", "Refresh", ct: "Button"),
            Synthetic.Ev(t.AddSeconds(2), Acts.Invoke, "Sample ERP", "Workspace", ct: "Button"), // nothing in the run explains it
        };
        var card = ScenarioScorer.Score("sample", run, events);
        Assert.Equal(new[] { "passed", "failed", "blocked", "untested", "untested" }, card.Steps.Select(s => s.Verdict).ToArray());
        Assert.Single(card.UnexplainedPresses);
        Assert.Equal("Workspace", card.UnexplainedPresses[0].name);
    }

    [Fact]
    public void The_drivers_run_log_reads_back_as_it_was_written()
    {
        var steps = new List<RunStep>
        {
            new() { T = new DateTime(2026, 11, 20, 19, 31, 27), Index = 1, Step = new ScenarioStep { Do = StepKind.Type, Field = "txtUser", Value = "tester", Then = "tab" }, Status = "done", Note = "typed into 'txtUser' in [Sample ERP - Login]" },
            new() { T = new DateTime(2026, 11, 20, 19, 32, 7), Index = 14, Step = new ScenarioStep { Do = StepKind.Prompt, Title = "Confirm?", Button = "OK" }, Status = "done", Note = "clicked 'OK' at 2528,967 in [Confirm?]" },
            new() { T = new DateTime(2026, 11, 20, 19, 32, 10), Index = 15, Step = new ScenarioStep { Do = StepKind.Menu, Path = new() { "Help", "About" } }, Status = "blocked", Note = "menu item 'About' not found" },
        };
        var path = Path.Combine(Path.GetTempPath(), "app-watcher-run-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path, steps.Select(r => System.Text.Json.JsonSerializer.Serialize(r, Scenario.Json).Replace("\r", "").Replace("\n", "")).Append(""));
            var back = ScenarioScorer.ReadRun(path);
            Assert.Equal(3, back.Count);
            Assert.Equal(StepKind.Type, back[0].Step.Do); Assert.Equal("tester", back[0].Step.Value); Assert.Equal(steps[0].T, back[0].T);
            Assert.Equal("Confirm?", back[1].Step.Title); Assert.Equal("OK", back[1].Step.Button);
            Assert.Equal(new[] { "Help", "About" }, back[2].Step.Path!.ToArray()); Assert.Equal("blocked", back[2].Status);
            var card = ScenarioScorer.Score("sample", back, new List<UiEvent>());
            Assert.Equal(new[] { "failed", "failed", "blocked" }, card.Steps.Select(x => x.Verdict).ToArray());
        }
        finally { File.Delete(path); }
    }
}
