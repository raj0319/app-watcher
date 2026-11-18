using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    /// <summary>Title with numbers collapsed, e.g. "Invoice #"; the same screen under "Invoice 1023" and "Invoice 1024" is stored once.</summary>
    public string TitlePattern => Learning.Titles.Key(Title);
    public DateTime Captured { get; set; } = DateTime.Now;
    public List<ControlRow> Controls { get; set; } = new();
    public string? ReachedFrom { get; set; }
    public List<string> OtherTitles { get; set; } = new();

    private static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "ComboBox", "CheckBox", "RadioButton", "Spinner", "Slider", "Calendar", "DataGrid", "List", "Tree", "Document", "Table" };
    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Button", "MenuItem", "SplitButton", "Hyperlink", "TabItem" };

    public IEnumerable<ControlRow> Fields => Controls.Where(c => FieldTypes.Contains(c.Type));
    public IEnumerable<ControlRow> Actions => Controls.Where(c => ActionTypes.Contains(c.Type) && !string.IsNullOrWhiteSpace(c.Name));

    public string FileName => Paths.SafeName(TitlePattern) + ".md";

    /// <summary>Hash of the tree's structure: depth, type, name and automation id of every control except the window's own title. Values and positions are ignored.</summary>
    public string StructureHash
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var c in Controls)
            {
                sb.Append(c.Depth).Append('|').Append(c.Type).Append('|');
                if (c.Depth > 0) sb.Append(c.Type is "Window" or "Pane" or "Document" or "Edit" or "Text" ? Learning.Titles.Key(c.Name) : c.Name);
                sb.Append('|').Append(c.AutomationId).Append('\n');
            }
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        }
    }

    /// <summary>Same layout as app-twin-kit/docs/templates/screen.md plus the control tree table of scripts/ui_tree.py.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Screen: {TitlePattern}");
        sb.AppendLine();
        sb.AppendLine("**Menu path:** (observed, not probed)");
        sb.AppendLine("**Purpose:** window of the watched app, recorded by App Watcher the first time it opened.");
        sb.AppendLine($"**Reached from:** {ReachedFrom ?? "unknown"}");
        sb.AppendLine($"**Title pattern:** {TitlePattern} (first seen as \"{Title}\"{(OtherTitles.Count > 0 ? $", also: {string.Join(", ", OtherTitles.Take(5).Select(t => $"\"{t}\""))}" : "")})");
        sb.AppendLine($"**Structure hash:** {StructureHash}");
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
            if (line.StartsWith("# Screen: ") && rec.Title.Length == 0) rec.Title = line[10..].Trim();
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

/// <summary>Writes each distinct screen once. Distinct = different structure hash; titles that only differ by numbers map to the same file.</summary>
public static class ScreenWriter
{
    private sealed class IndexEntry { public string File { get; set; } = ""; public List<string> Titles { get; set; } = new(); }
    private static readonly object Gate = new();

    /// <summary>Returns true when a new file was created.</summary>
    public static bool WriteOnce(string screensDir, ScreenRecord rec)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(screensDir);
            var indexPath = Path.Combine(screensDir, "index.json");
            Dictionary<string, IndexEntry> index = new();
            try { if (File.Exists(indexPath)) index = JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(indexPath)) ?? new(); } catch { }

            var hash = rec.StructureHash;
            var path = Path.Combine(screensDir, rec.FileName);
            if (index.TryGetValue(hash, out var entry))
            {
                if (!entry.Titles.Contains(rec.Title) && entry.Titles.Count < 20)
                {
                    entry.Titles.Add(rec.Title);
                    File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
                }
                return false;
            }
            if (File.Exists(path)) { index[hash] = new IndexEntry { File = rec.FileName, Titles = { rec.Title } }; File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true })); return false; }
            File.WriteAllText(path, rec.ToMarkdown());
            index[hash] = new IndexEntry { File = rec.FileName, Titles = { rec.Title } };
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
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
