using System.IO.Compression;
using System.Text;
using AppWatcher.Core.Learning;

namespace AppWatcher.Core.Export;

/// <summary>Writes data/&lt;app&gt;/export/&lt;date&gt;/docs/raw/ in the app-twin-kit formats and zips it.</summary>
public static class Exporter
{
    public static (string folder, string zip) Export(AppPaths paths, string appName, LearnResult? result = null)
    {
        result ??= Learner.Learn(Learner.LoadInput(paths, appName));
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        var root = Path.Combine(paths.Export, stamp);
        var raw = Path.Combine(root, "docs", "raw");
        foreach (var d in new[] { "screens", "tasks", "probes", "memory" }) Directory.CreateDirectory(Path.Combine(raw, d));

        // screens: copy as recorded (already in the screen template format)
        if (Directory.Exists(paths.Screens))
            foreach (var f in Directory.GetFiles(paths.Screens, "*.md"))
                File.Copy(f, Path.Combine(raw, "screens", Path.GetFileName(f)), true);

        // tasks: one per learned flow
        foreach (var flow in result.Flows)
            File.WriteAllText(Path.Combine(raw, "tasks", flow.SafeName + ".md"), Task(flow, result));

        // probes: one per screen
        foreach (var s in result.Screens)
            File.WriteAllText(Path.Combine(raw, "probes", Paths.SafeName(s.Title) + ".md"), Probe(s, result));

        // data model and memory
        var memory = MemoryWriter.Render(result);
        File.WriteAllText(Path.Combine(raw, "data-model.md"), DataModelForTwin(result, memory["data-model.md"]));
        foreach (var (name, text) in memory) File.WriteAllText(Path.Combine(raw, "memory", name), text);
        foreach (var extra in new[] { "changelog.md", "architecture-reviewed.md" })
        {
            var p = Path.Combine(paths.Memory, extra);
            if (File.Exists(p)) File.Copy(p, Path.Combine(raw, "memory", extra), true);
        }
        File.WriteAllText(Path.Combine(root, "README.md"), ExportReadme(appName, stamp));

        var zip = Path.Combine(paths.Export, $"{Paths.SafeName(appName)}-{stamp}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(root, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        return (root, zip);
    }

    public static string Task(FlowStat flow, LearnResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Task: {flow.Key}");
        sb.AppendLine();
        sb.AppendLine("**Who does it:** a real user of the app (observed by App Watcher)");
        sb.AppendLine($"**How often:** seen {flow.Count} times between {r.FirstEvent:yyyy-MM-dd} and {r.LastEvent:yyyy-MM-dd}; median duration {MemoryWriter.Duration(flow.MedianMs)}");
        sb.AppendLine($"**Goal:** work that starts in the window \"{flow.Key}\" and returns to {r.MainWindow ?? "the main window"}");
        sb.AppendLine();
        sb.AppendLine("## Steps");
        sb.AppendLine();
        sb.AppendLine("| # | Screen | Control used | Value entered | What appeared |");
        sb.AppendLine("|---|---|---|---|---|");
        var n = 0;
        foreach (var win in flow.CommonPath)
        {
            n++;
            var controls = flow.StepControls.TryGetValue(win, out var c) ? string.Join(", ", c.Select(x => $"{x.name} ({x.count}×)")) : "";
            var next = n < flow.CommonPath.Count ? flow.CommonPath[n] : r.MainWindow ?? "";
            sb.AppendLine($"| {n} | {win} | {controls} | (values not exported) | next window: {next} |");
        }
        if (flow.BackFrom is not null) sb.AppendLine().AppendLine($"Users most often go back from step \"{flow.BackFrom}\" ({flow.BackFromCount}×).");
        sb.AppendLine();
        sb.AppendLine("## Outputs");
        sb.AppendLine("- Files created: see data changes");
        sb.AppendLine("- Reports / printouts: not observed");
        var msgs = r.Rules.Where(v => flow.CommonPath.Contains(v.Screen)).Select(v => v.Message).Distinct().Take(5).ToList();
        sb.AppendLine($"- Messages shown: {(msgs.Count == 0 ? "none recorded" : string.Join("; ", msgs))}");
        sb.AppendLine();
        sb.AppendLine("## Data changes");
        var writes = r.ScreenWrites.Where(w => flow.CommonPath.Contains(w.Screen)).ToList();
        if (writes.Count == 0) sb.AppendLine("(no data change was captured for these screens yet)");
        foreach (var w in writes) sb.AppendLine($"- {w.Screen} writes `{w.Table}` ({w.Count} of {w.ScreenEffects} saves; evidence {w.Evidence.Pointer})");
        sb.AppendLine();
        sb.AppendLine("## Open questions");
        sb.AppendLine("- ASK USER: what is this task called in your words, and who does it?");
        return sb.ToString();
    }

    public static string Probe(ScreenInfo s, LearnResult r)
    {
        var key = Titles.Key(s.Title);
        var sb = new StringBuilder();
        sb.AppendLine($"# Probes: {s.Title}");
        sb.AppendLine();
        sb.AppendLine("Values below were typed by real users and recorded by App Watcher (not probed by a script). Personal-looking values are masked.");
        sb.AppendLine();
        foreach (var f in s.Fields)
        {
            sb.AppendLine($"## Field: {f}");
            var vals = s.FieldValues.TryGetValue(f, out var v) ? v : new List<string>();
            sb.AppendLine($"- Distinct values seen: {(vals.Count == 0 ? "(none typed)" : string.Join(", ", vals.Select(x => $"`{Masking.ForExport(x)}`")))}");
            var maps = r.FieldMappings.Where(m => m.Screen == key && m.Field == f).ToList();
            foreach (var m in maps) sb.AppendLine($"- Writes column `{m.Table}`.{m.Column} (seen {m.Evidence.Seen}×)");
            sb.AppendLine();
        }
        sb.AppendLine("## Rules learned");
        var rules = r.Rules.Where(v => v.Screen == key).ToList();
        if (rules.Count == 0) sb.AppendLine("- (no validation message observed on this screen yet)");
        foreach (var v in rules) sb.AppendLine($"- Validation: field `{v.Field}` rejects {v.Pattern} with message \"{v.Message}\" (seen {v.Evidence.Seen}×).");
        foreach (var w in r.ScreenWrites.Where(w => w.Screen == key)) sb.AppendLine($"- Saving on this screen writes `{w.Table}` ({(w.Always ? "always" : "sometimes")}).");
        return sb.ToString();
    }

    private static string DataModelForTwin(LearnResult r, string memoryDataModel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Data model");
        sb.AppendLine();
        sb.AppendLine("Written by App Watcher from read-only snapshots of the app's data. Engine and location per source:");
        foreach (var s in r.Sources) sb.AppendLine($"- `{s}`");
        sb.AppendLine();
        sb.AppendLine("## Snapshot paths");
        sb.AppendLine("Pass these to `scripts/snapshot_diff.py snapshot <name> --paths ...`:");
        foreach (var s in r.Sources.Where(s => !s.StartsWith("sqlserver:"))) sb.AppendLine($"- \"{s}\"");
        sb.AppendLine();
        sb.AppendLine(memoryDataModel[(memoryDataModel.IndexOf("## Tables", StringComparison.Ordinal) is var i && i >= 0 ? i : 0)..]);
        return sb.ToString();
    }

    private static string ExportReadme(string app, string stamp) => $@"# App Watcher export: {app} ({stamp})

This folder is in the App Twin Kit format.

How to use it:
1. Copy the `docs/raw` folder into your `app-twin-kit` folder, replacing its `docs/raw`.
2. Open `app-twin-kit` in Claude Code and type `/mode spec`.
3. The kit continues from the SPEC step using these screens, tasks, probes and the data model.

Any AI can also read `docs/raw/memory/*.md` directly: they are plain text with a confidence tag on every line.
Nothing in here contains raw values typed by users except the masked examples in `probes/`.
";
}
