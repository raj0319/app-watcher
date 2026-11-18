using System.Text;

namespace AppWatcher.Core.Screens;

public sealed class ControlRow
{
    public int Depth { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string Position { get; set; } = "";
    public bool IsPassword { get; set; }
}

/// <summary>One window's control tree, written once in the twin kit's screen format.</summary>
public sealed class ScreenRecord
{
    public string Title { get; set; } = "";
    public DateTime Captured { get; set; } = DateTime.Now;
    public List<ControlRow> Controls { get; set; } = new();
    public string? ReachedFrom { get; set; }

    private static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "ComboBox", "CheckBox", "RadioButton", "Spinner", "Slider", "Calendar", "DataGrid", "List", "Tree", "Document", "Table" };
    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Button", "MenuItem", "SplitButton", "Hyperlink", "TabItem" };

    public IEnumerable<ControlRow> Fields => Controls.Where(c => FieldTypes.Contains(c.Type));
    public IEnumerable<ControlRow> Actions => Controls.Where(c => ActionTypes.Contains(c.Type) && !string.IsNullOrWhiteSpace(c.Name));

    public string FileName => Paths.SafeName(Title) + ".md";

    /// <summary>Same layout as app-twin-kit/docs/templates/screen.md plus the control tree table of scripts/ui_tree.py.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Screen: {Title}");
        sb.AppendLine();
        sb.AppendLine("**Menu path:** (observed, not probed)");
        sb.AppendLine("**Purpose:** window of the watched app, recorded by App Watcher the first time it opened.");
        sb.AppendLine($"**Reached from:** {ReachedFrom ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("## Fields");
        sb.AppendLine();
        sb.AppendLine("| Label | Type | Required | Default | Validation seen |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var f in Fields)
            sb.AppendLine($"| {Cell(f.Name.Length > 0 ? f.Name : f.AutomationId)} | {f.Type}{(f.IsPassword ? " (password)" : "")} | ? | | |");
        sb.AppendLine();
        sb.AppendLine("## Actions");
        sb.AppendLine();
        sb.AppendLine("| Button / menu item | What it does | Changes data? | Pressed during discovery? |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var a in Actions.GroupBy(a => a.Name).Select(g => g.First()))
            sb.AppendLine($"| {Cell(a.Name)} | ({a.Type}) see memory/screens.md | ? | watched only |");
        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- Recorded passively by App Watcher. Nothing was pressed by the watcher.");
        sb.AppendLine();
        sb.AppendLine("## Control tree (auto-generated)");
        sb.AppendLine();
        sb.AppendLine($"Window: {Title}  ");
        sb.AppendLine($"Captured: {Captured:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine("Screenshot: (not taken; App Watcher never takes screenshots)");
        sb.AppendLine();
        sb.AppendLine("| Depth | Type | Name | AutomationId | Position |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in Controls)
            sb.AppendLine($"| {c.Depth} | {c.Type} | {Cell(c.Name)} | {Cell(c.AutomationId)} | {c.Position} |");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>Reads the control tree table back from a screen file written by ToMarkdown.</summary>
    public static ScreenRecord? Parse(string path)
    {
        if (!File.Exists(path)) return null;
        var rec = new ScreenRecord();
        var inTree = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# Screen: ")) rec.Title = line[10..].Trim();
            else if (line.StartsWith("Window: ")) rec.Title = line[8..].Trim();
            else if (line.StartsWith("## Control tree")) inTree = true;
            else if (inTree && line.StartsWith("| ") && !line.StartsWith("| Depth") && !line.StartsWith("|---"))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 7) continue;
                if (!int.TryParse(cells[1], out var depth)) continue;
                rec.Controls.Add(new ControlRow { Depth = depth, Type = cells[2], Name = cells[3], AutomationId = cells[4], Position = cells[5] });
            }
        }
        return rec;
    }
}

public static class ScreenWriter
{
    /// <summary>Writes the screen once. Returns true when a new file was created.</summary>
    public static bool WriteOnce(string screensDir, ScreenRecord rec)
    {
        Directory.CreateDirectory(screensDir);
        var path = Path.Combine(screensDir, rec.FileName);
        if (File.Exists(path)) return false;
        File.WriteAllText(path, rec.ToMarkdown());
        return true;
    }

    public static List<ScreenRecord> ReadAll(string screensDir)
    {
        var list = new List<ScreenRecord>();
        if (!Directory.Exists(screensDir)) return list;
        foreach (var f in Directory.GetFiles(screensDir, "*.md").OrderBy(f => f))
        {
            var r = ScreenRecord.Parse(f);
            if (r is not null) list.Add(r);
        }
        return list;
    }
}
