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
    /// <summary>For Edit and Document controls: true when the collector saw the field is read-only (a message box body), false when it can be typed into, null when unknown.</summary>
    public bool? IsReadOnly { get; set; }
    /// <summary>AcceleratorKey or AccessKey of a button or menu item ("F3", "Ctrl+S", "Alt+S"); not part of the control tree table.</summary>
    public string Shortcut { get; set; } = "";
    /// <summary>UIA LabeledBy name when the collector could read it; else empty.</summary>
    public string LabeledBy { get; set; } = "";
    /// <summary>The control's value at the time of a field read (collector only; never written to a screen file).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Value { get; set; }
}

/// <summary>Kinds of window the collector tells apart.</summary>
public static class ScreenKinds
{
    public const string Screen = "screen";
    public const string Dialog = "dialog";
    public const string Session = "session";
    public const string Tab = "tab";
    /// <summary>A popup menu that is an ordinary window: its items and access keys are kept, it is never a screen.</summary>
    public const string Menu = "menu";
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
    public string? MenuPath { get; set; }
    /// <summary>screen | dialog | session | tab</summary>
    public string Kind { get; set; } = ScreenKinds.Screen;
    public List<string> OtherTitles { get; set; } = new();
    /// <summary>How the tree was read: uia-cached, uia-live, msaa, or empty.</summary>
    public string Source { get; set; } = "";
    /// <summary>File this record was read from (set by ScreenWriter.ReadAll).</summary>
    public string? SourceFile { get; set; }
    /// <summary>Relative path of the one picture of this screen ("shots/&lt;name&gt;.png"), when screenshots are on.</summary>
    public string? Screenshot { get; set; }
    public string ShotFileName => Paths.SafeName(TitlePattern) + ".png";

    private static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Edit", "ComboBox", "CheckBox", "RadioButton", "Spinner", "Slider", "Calendar", "DataGrid", "List", "Tree", "Document", "Table" };
    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Button", "MenuItem", "SplitButton", "Hyperlink", "TabItem" };

    public IEnumerable<ControlRow> Fields => Controls.Where(c => FieldTypes.Contains(c.Type));
    public IEnumerable<ControlRow> Actions => Controls.Where(c => ActionTypes.Contains(c.Type) && !string.IsNullOrWhiteSpace(c.Name));

    /// <summary>
    /// Fields with toolkit wrappers collapsed: Infragistics, DevExpress and Telerik expose each editor as two or three
    /// nested UIA elements ("txtName" Pane > "txtName_EmbeddableTextBox" Edit). One row per logical field, the innermost
    /// editor wins and keeps the wrapper's id as evidence.
    /// </summary>
    public List<(ControlRow row, string label)> LogicalFields()
    {
        var result = new List<(ControlRow, string)>();
        var ancestors = new ControlRow[64];
        var kept = new List<ControlRow>();
        foreach (var r in Controls)
        {
            if (r.Depth < ancestors.Length) ancestors[r.Depth] = r;
            if (!FieldTypes.Contains(r.Type)) continue;
            if (r.Type is "DataGrid" or "List" or "Tree" or "Table" && r.Name.Length == 0 && r.AutomationId.Length == 0) continue;
            // a field nested inside another field with the same cleaned id or name is the same logical field
            var dup = false;
            var cleanId = Labels.Clean(r.AutomationId);
            for (var d = r.Depth - 1; d >= Math.Max(0, r.Depth - 3); d--)
            {
                var a = ancestors[d];
                if (a is null) continue;
                var sameId = cleanId.Length > 0 && string.Equals(Labels.Clean(a.AutomationId), cleanId, StringComparison.OrdinalIgnoreCase);
                var sameName = r.Name.Length > 0 && string.Equals(a.Name, r.Name, StringComparison.OrdinalIgnoreCase);
                if (FieldTypes.Contains(a.Type) && (sameId || sameName || Labels.IsWrapperId(r.AutomationId)))
                {
                    // replace the outer one (or whatever already replaced it) with the inner editor
                    var idx = kept.IndexOf(a);
                    if (idx < 0 && cleanId.Length > 0) idx = kept.FindIndex(k => k.Depth > a.Depth && k.Depth < r.Depth && string.Equals(Labels.Clean(k.AutomationId), cleanId, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) { kept[idx] = r; dup = true; }
                    break;
                }
                if (!FieldTypes.Contains(a.Type) && sameId && Labels.IsWrapperId(r.AutomationId)) break;
            }
            if (!dup) kept.Add(r);
        }
        // list items inside a list or grid are rows, not fields
        foreach (var r in kept) result.Add((r, Labels.Resolve(r, Controls)));
        return result;
    }

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
        sb.AppendLine($"**Menu path:** {MenuPath ?? "(observed, not probed)"}");
        sb.AppendLine($"**Purpose:** {Purpose()}");
        sb.AppendLine($"**Reached from:** {ReachedFrom ?? "unknown"}");
        sb.AppendLine($"**Kind:** {Kind}");
        sb.AppendLine($"**Title pattern:** {TitlePattern} (first seen as \"{Title}\"{(OtherTitles.Count > 0 ? $", also: {string.Join(", ", OtherTitles.Take(5).Select(t => $"\"{t}\""))}" : "")})");
        sb.AppendLine($"**Structure hash:** {StructureHash}");
        if (Source.Length > 0) sb.AppendLine($"**Tree source:** {Source}");
        sb.AppendLine();
        sb.AppendLine("## Fields");
        sb.AppendLine();
        sb.AppendLine("| Label | Type | Required | Default | Validation seen | AutomationId |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var (f, label) in LogicalFields())
            sb.AppendLine($"| {Cell(label)} | {f.Type}{(f.IsPassword ? " (password)" : "")} | ? | | | {Cell(f.AutomationId)} |");
        sb.AppendLine();
        sb.AppendLine("## Actions");
        sb.AppendLine();
        sb.AppendLine("| Button / menu item | What it does | Changes data? | Pressed during discovery? | Shortcut |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var a in Actions.GroupBy(a => a.Name).Select(g => g.First()))
            sb.AppendLine($"| {Cell(a.Name)} | ({a.Type}) see memory/screens.md | ? | watched only | {Cell(a.Shortcut)} |");
        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- Recorded passively by App Watcher. Nothing was pressed by the watcher.");
        if (Kind == ScreenKinds.Dialog) sb.AppendLine("- This is a message window; its text becomes a rule in memory/rules.md.");
        if (Kind == ScreenKinds.Session) sb.AppendLine("- Session window (splash, login or station prompt); flows start after it.");
        if (Kind == ScreenKinds.Tab) sb.AppendLine("- A tab document inside its parent window; recorded as a screen of its own.");
        if (Kind == ScreenKinds.Menu) sb.AppendLine("- A popup menu (its items and access keys); not a screen.");
        sb.AppendLine();
        sb.AppendLine("## Control tree (auto-generated)");
        sb.AppendLine();
        sb.AppendLine($"Window: {Title}  ");
        sb.AppendLine($"Captured: {Captured:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"Screenshot: {Screenshot ?? "(not taken)"}");
        sb.AppendLine();
        sb.AppendLine("| Depth | Type | Name | AutomationId | Position |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var c in Controls)
            sb.AppendLine($"| {c.Depth} | {c.Type} | {Cell(c.Name)} | {Cell(c.AutomationId)} | {c.Position} |");
        return sb.ToString();
    }

    private string Purpose() => Kind switch
    {
        ScreenKinds.Dialog => "message window of the watched app (text recorded as a dialog event).",
        ScreenKinds.Session => "session window (splash, login or station prompt) of the watched app.",
        ScreenKinds.Tab => "tab document of the watched app, recorded the first time the tab was selected.",
        ScreenKinds.Menu => "popup menu of the watched app (items and access keys), recorded the first time it opened.",
        _ => "window of the watched app, recorded by App Watcher the first time it opened.",
    };

    private static string Cell(string s) => s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>Reads the control tree table (and the header lines, shortcuts) back from a screen file written by ToMarkdown.</summary>
    public static ScreenRecord? Parse(string path)
    {
        if (!File.Exists(path)) return null;
        var rec = new ScreenRecord { SourceFile = Path.GetFileName(path) };
        var inTree = false; var inActions = false;
        var shortcuts = new Dictionary<string, string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# Screen: ") && rec.Title.Length == 0) rec.Title = line[10..].Trim();
            else if (line.StartsWith("Window: ")) rec.Title = line[8..].Trim();
            else if (line.StartsWith("**Kind:** ")) rec.Kind = line[10..].Trim();
            else if (line.StartsWith("**Menu path:** ")) { var v = line[15..].Trim(); if (!v.StartsWith("(")) rec.MenuPath = v; }
            else if (line.StartsWith("**Reached from:** ")) { var v = line[18..].Trim(); if (v != "unknown") rec.ReachedFrom = v; }
            else if (line.StartsWith("**Tree source:** ")) rec.Source = line[17..].Trim();
            else if (line.StartsWith("Screenshot: ")) { var v = line[12..].Trim(); if (!v.StartsWith("(")) rec.Screenshot = v; }
            else if (line.StartsWith("## Actions")) { inActions = true; inTree = false; }
            else if (line.StartsWith("## Control tree")) { inTree = true; inActions = false; }
            else if (line.StartsWith("## ")) { inActions = false; }
            else if (inActions && line.StartsWith("| ") && !line.StartsWith("| Button") && !line.StartsWith("|---"))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length >= 7 && cells[5].Length > 0) shortcuts[cells[1]] = cells[5];
            }
            else if (inTree && line.StartsWith("| ") && !line.StartsWith("| Depth") && !line.StartsWith("|---"))
            {
                var cells = line.Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 7) continue;
                if (!int.TryParse(cells[1], out var depth)) continue;
                rec.Controls.Add(new ControlRow { Depth = depth, Type = cells[2], Name = cells[3], AutomationId = cells[4], Position = cells[5] });
            }
        }
        foreach (var c in rec.Controls) if (shortcuts.TryGetValue(Cell(c.Name), out var k)) c.Shortcut = k;
        return rec;
    }
}

/// <summary>Writes each distinct screen once. Distinct = different structure hash; titles that only differ by numbers map to the same file.</summary>
public static class ScreenWriter
{
    private sealed class IndexEntry { public string File { get; set; } = ""; public List<string> Titles { get; set; } = new(); public string? Kind { get; set; } }
    private static readonly object Gate = new();

    /// <summary>Returns true when a new file was created.</summary>
    public static bool WriteOnce(string screensDir, ScreenRecord rec)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(screensDir);
            var indexPath = Path.Combine(screensDir, "index.json");
            var index = ReadIndex(indexPath);

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
            if (File.Exists(path))
            {
                // same title pattern, different structure: keep the richer tree
                var old = ScreenRecord.Parse(path);
                if (old is not null && old.Controls.Count >= rec.Controls.Count)
                {
                    index[hash] = new IndexEntry { File = rec.FileName, Titles = { rec.Title }, Kind = rec.Kind };
                    File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
                    return false;
                }
            }
            File.WriteAllText(path, rec.ToMarkdown());
            index[hash] = new IndexEntry { File = rec.FileName, Titles = { rec.Title }, Kind = rec.Kind };
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
    }

    private static Dictionary<string, IndexEntry> ReadIndex(string indexPath)
    {
        try { if (File.Exists(indexPath)) return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(indexPath)) ?? new(); } catch { }
        return new();
    }

    /// <summary>Every group of titles that share one structure (from index.json): the learner folds their varying parts into one pattern.</summary>
    public static List<List<string>> TitleGroups(string screensDir)
    {
        var index = ReadIndex(Path.Combine(screensDir, "index.json"));
        return index.Values.Where(e => e.Titles.Count > 1).Select(e => e.Titles.ToList()).ToList();
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
