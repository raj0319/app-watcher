using System.Text.Json;
using AppWatcher.Core.Events;
using AppWatcher.Core.Export;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// The blueprint (milestone M1): the learned memory in the files an AI coding agent rebuilds from. Rendered from the three
/// anonymised real sessions: every record has a stable id, a status and evidence; nothing typed by a user leaks into it;
/// the export bundle carries it.
/// </summary>
public class BlueprintTests
{
    private static string Fixture(string folder) => Path.Combine(AppContext.BaseDirectory, "Fixtures", folder);

    private static (LearnResult r, List<ScreenRecord> records) Learn(string folder)
    {
        var dir = Fixture(folder);
        var records = Directory.GetFiles(dir, "*.md").Where(f => !f.EndsWith("README.md")).Select(f => ScreenRecord.Parse(f)!).ToList();
        var input = new LearnInput { AppName = "inform", Events = EventReader.ReadFile(Path.Combine(dir, "events-2026-11-19.jsonl")).ToList(), Screens = records, HasDataSources = false, Now = new DateTime(2026, 11, 20), BackendKind = "universe" };
        input.Collector = JsonSerializer.Deserialize<CollectorStatus>(File.ReadAllText(Path.Combine(dir, "collector-status.json")));
        input.SessionWindowPatterns.AddRange(new[] { "Splash Screen", "Login*", "DDI System Inform Login", "Enter Register #*" });
        input.Backends.Add(new BackendInfo { Kind = "universe", Host = "192.0.2.10", Database = "DDI", Where = "installedapplication.config" });
        input.Backends.Add(new BackendInfo { Kind = "rest", Host = "shop.example", Where = "App Log.json" });
        return (Learner.Learn(input), records);
    }

    private static readonly string[] JsonFiles = { "screens.json", "fields.json", "navigation_graph.json", "workflows.json", "calculations.json", "business_rules.json", "database_model.json", "permissions.json", "integrations.json", "reports.json", "coverage_report.json" };

    [Theory]
    [InlineData("inform")]
    [InlineData("inform-v0.8")]
    [InlineData("inform-v0.9")]
    public void Every_record_has_a_stable_id_a_status_and_evidence(string folder)
    {
        var (r, records) = Learn(folder);
        var files = BlueprintWriter.Render(r, records);
        foreach (var name in JsonFiles.Concat(new[] { "application_overview.md", "AI_REBUILD_INSTRUCTIONS.md", "evidence/README.md", "screenshots/README.md" })) Assert.Contains(name, files.Keys);
        foreach (var name in JsonFiles) using (JsonDocument.Parse(files[name])) { }
        var statuses = new HashSet<string> { "observed", "single_observation", "inferred", "unknown", "candidate" };
        foreach (var name in new[] { "screens.json", "fields.json", "workflows.json", "calculations.json", "business_rules.json" })
        {
            using var doc = JsonDocument.Parse(files[name]);
            var items = doc.RootElement.EnumerateArray().ToList();
            if (name != "calculations.json") Assert.True(items.Count > 0, name + " is empty"); // a run without a learned formula has none
            foreach (var item in items)
            {
                var id = item.GetProperty("id").GetString()!;
                Assert.Matches(@"^[A-Z-]+-[0-9a-f]{8}$", id);
                Assert.Contains(item.GetProperty("status").GetString(), statuses);
                var evidence = item.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).ToList();
                var status = item.GetProperty("status").GetString();
                if (status is "observed" or "single_observation" or "inferred") Assert.True(evidence.Count > 0, $"{name} {id} ({status}) has no evidence");
                foreach (var e in evidence) Assert.Matches(@"^(events/|screens/|effects/|snapshots/|sql|\(events\))", e);
            }
        }
        // the same memory rendered twice gives the same blueprint (ids are hashes of the record's key, not positions)
        var again = BlueprintWriter.Render(r, records);
        foreach (var (name, text) in files) Assert.Equal(text, again[name]);
        Assert.Equal(BlueprintWriter.Id("CALC", "A", "B"), BlueprintWriter.Id("CALC", "A", "B"));
        Assert.NotEqual(BlueprintWriter.Id("CALC", "A", "B"), BlueprintWriter.Id("CALC", "B", "A"));
    }

    [Fact]
    public void The_v09_session_blueprint_names_the_documents_the_prompts_and_what_is_unknown_without_a_typed_value()
    {
        var saved = Titles.MaskPatterns;
        try
        {
            Titles.MaskPatterns = new List<string> { "DDI System Inform - *" };
            var (r, records) = Learn("inform-v0.9");
            var files = BlueprintWriter.Render(r, records);
            using var screens = JsonDocument.Parse(files["screens.json"]);
            var titles = screens.RootElement.EnumerateArray().Select(s => s.GetProperty("title").GetString()!).ToList();
            Assert.Contains("DDI System Inform - … › New Sales Order", titles);
            Assert.True(titles.Count(t => t.Contains(" › ")) >= 14, "every document is a screen");
            Assert.DoesNotContain(titles, t => t is "Close?" or "Sales" or "Pricing");
            using var nav = JsonDocument.Parse(files["navigation_graph.json"]);
            Assert.Contains(nav.RootElement.GetProperty("message_windows").EnumerateArray(), m => m.GetProperty("title").GetString() == "Close?");
            using var rules = JsonDocument.Parse(files["business_rules.json"]);
            var kinds = rules.RootElement.EnumerateArray().Select(x => x.GetProperty("kind").GetString()).Distinct().ToList();
            Assert.Contains("message_after_action", kinds); Assert.Contains("button_shows_message", kinds); Assert.Contains("hover_tip", kinds);
            using var perms = JsonDocument.Parse(files["permissions.json"]);
            Assert.Equal("unknown", perms.RootElement.GetProperty("status").GetString());
            Assert.Contains(perms.RootElement.GetProperty("access_messages").EnumerateArray(), m => m.GetProperty("message").GetString()!.Contains("Required Level"));
            using var data = JsonDocument.Parse(files["database_model.json"]);
            Assert.Equal("unknown", data.RootElement.GetProperty("physical_schema").GetString());
            Assert.Contains(data.RootElement.GetProperty("backends").EnumerateArray(), b => b.GetProperty("kind").GetString() == "universe");
            using var integ = JsonDocument.Parse(files["integrations.json"]);
            Assert.Contains(integ.RootElement.GetProperty("services").EnumerateArray(), s => s.GetProperty("host").GetString() == "shop.example");
            using var cov = JsonDocument.Parse(files["coverage_report.json"]);
            Assert.True(cov.RootElement.GetProperty("period").GetProperty("events").GetInt32() == 447);
            Assert.Contains("every press came from a fallback", cov.RootElement.GetProperty("presses").GetProperty("note").GetString());
            // nothing a user typed, no stand-in name, no company name reaches the blueprint
            var text = string.Join("\n", files.Values);
            foreach (var leak in new[] { "Test Customer", "Writer One", "ACME", "Sample City", "Sample Co", "0000001", "00001", "100EX" }) Assert.DoesNotContain(leak, text);
            Assert.Contains("never implement `unknown` as behaviour", files["AI_REBUILD_INSTRUCTIONS.md"]);
            Assert.Contains("What is not in here", files["application_overview.md"]);
        }
        finally { Titles.MaskPatterns = saved; }
    }

    [Fact]
    public void A_calculation_with_evidence_is_observed_and_one_with_alternatives_is_inferred()
    {
        var t = new DateTime(2026, 11, 19, 10, 0, 0);
        var win = "Main › Order";
        var input = new LearnInput { AppName = "x", Now = t.AddDays(1) };
        foreach (var (i, qty, ext) in new[] { (0, "1", "2.5"), (1, "5", "12.5"), (2, "20", "50") })
        {
            var at = t.AddSeconds(10 * i);
            input.Events.Add(Synthetic.Ev(at, Acts.Value, win, "Qty", qty, "DataItem", src: $"events/test.jsonl#L{i * 3 + 1}"));
            var p = Synthetic.Ev(at.AddMilliseconds(300), Acts.Value, win, "Price", "2.5", "DataItem", src: $"events/test.jsonl#L{i * 3 + 2}"); p.By = ValueBy.App; input.Events.Add(p);
            var e = Synthetic.Ev(at.AddMilliseconds(300), Acts.Value, win, "Ext", ext, "DataItem", src: $"events/test.jsonl#L{i * 3 + 3}"); e.By = ValueBy.App; input.Events.Add(e);
        }
        var r = Learner.Learn(input);
        var calcs = BlueprintWriter.Calculations(r);
        var one = Assert.Single(calcs, c => JsonSerializer.Serialize(c).Contains("\"field\":\"Ext\"")); // Price, filled with one constant, is a question, not a formula
        var json = JsonSerializer.Serialize(one);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("observed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Price × Qty", doc.RootElement.GetProperty("formula").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("regression_cases").GetArrayLength());
        Assert.DoesNotContain("12.5", json); // values stay in the evidence files
    }

    [Fact]
    public void The_export_bundle_carries_the_blueprint_next_to_the_twin_kit_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-blueprint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppWatcher.Core.AppPaths(dir);
            Directory.CreateDirectory(paths.Events); Directory.CreateDirectory(paths.Screens); Directory.CreateDirectory(paths.Memory);
            var src = Fixture("inform-v0.8");
            File.Copy(Path.Combine(src, "events-2026-11-19.jsonl"), Path.Combine(paths.Events, "2026-11-19.jsonl"));
            foreach (var f in Directory.GetFiles(src, "*.md").Where(f => !f.EndsWith("README.md"))) File.Copy(f, Path.Combine(paths.Screens, Path.GetFileName(f)));
            var (folder, zip) = Exporter.Export(paths, "inform");
            Assert.True(File.Exists(Path.Combine(folder, "blueprint", "screens.json")));
            Assert.True(File.Exists(Path.Combine(folder, "blueprint", "coverage_report.json")));
            Assert.True(File.Exists(Path.Combine(folder, "blueprint", "evidence", "logic.md")));
            Assert.True(File.Exists(Path.Combine(folder, "blueprint", "AI_REBUILD_INSTRUCTIONS.md")));
            Assert.True(File.Exists(Path.Combine(folder, "docs", "raw", "screens", "vendor-internal-notes.md")));
            Assert.True(File.Exists(zip));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
