using System.Text;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core.Learning;

/// <summary>Writes memory/*.md from a LearnResult and keeps changelog.md. Memory files never contain raw data values.</summary>
public static class MemoryWriter
{
    public static readonly string[] Files = { "data-model.md", "screens.md", "rules.md", "flows.md", "architecture.md", "health.md" };

    public static Dictionary<string, string> Render(LearnResult r) => new()
    {
        ["data-model.md"] = DataModel(r),
        ["screens.md"] = Screens(r),
        ["rules.md"] = Rules(r),
        ["flows.md"] = Flows(r),
        ["architecture.md"] = Architecture(r),
        ["health.md"] = Health(r),
    };

    public static List<string> Write(string memoryDir, LearnResult r)
    {
        Directory.CreateDirectory(memoryDir);
        var rendered = Render(r);
        var changes = new List<string>();
        var log = new StringBuilder();
        foreach (var (name, text) in rendered)
        {
            var path = Path.Combine(memoryDir, name);
            var old = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            var (added, removed) = LineDiff(old, text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray());
            if (old.Length > 0 && added.Count == 0 && removed.Count == 0) continue;
            changes.Add($"{name}: +{added.Count} -{removed.Count}");
            log.AppendLine($"### {name}: +{added.Count} lines, -{removed.Count} lines");
            foreach (var l in added.Take(15)) log.AppendLine($"- added: {l.Trim()}");
            foreach (var l in removed.Take(15)) log.AppendLine($"- removed: {l.Trim()}");
            log.AppendLine();
        }
        foreach (var (name, text) in rendered) File.WriteAllText(Path.Combine(memoryDir, name), text);

        var changelog = Path.Combine(memoryDir, "changelog.md");
        var header = $"## {r.LearnedAt:yyyy-MM-dd HH:mm} learn: {r.EventCount} events, {r.EffectCount} effects, {r.Tables.Count} tables";
        var entry = header + "\n\n" + (log.Length == 0 ? "No changes since the last learn.\n\n" : log.ToString());
        var existing = File.Exists(changelog) ? File.ReadAllText(changelog) : "# Changelog\n\nWhat changed in the memory files at each learn. Newest first.\n\n";
        var idx = existing.IndexOf("\n## ", StringComparison.Ordinal);
        var head = idx < 0 ? existing.TrimEnd() + "\n\n" : existing[..(idx + 1)];
        var tail = idx < 0 ? "" : existing[(idx + 1)..];
        File.WriteAllText(changelog, head + entry + tail);
        return changes;
    }

    private static (List<string> added, List<string> removed) LineDiff(string[] old, string[] cur)
    {
        var skip = (string l) => l.StartsWith("Last learn:") || l.StartsWith("Generated") || l.StartsWith("| Disk use") || l.StartsWith("| Collector status") || l.Length == 0;
        var o = old.Where(l => !skip(l)).ToList();
        var c = cur.Where(l => !skip(l)).ToList();
        var oldSet = new HashSet<string>(o);
        var curSet = new HashSet<string>(c);
        return (c.Where(l => !oldSet.Contains(l)).Distinct().ToList(), o.Where(l => !curSet.Contains(l)).Distinct().ToList());
    }

    private static string Header(LearnResult r, string title, string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"App: **{r.AppName}**. {what}");
        sb.AppendLine($"Last learn: {r.LearnedAt:yyyy-MM-dd HH:mm}{(r.Incremental ? " (incremental)" : "")}. Evidence: {r.EventCount} events in {r.SessionCount} sessions, {r.EffectCount} effects, {r.Tables.Count} tables.");
        sb.AppendLine("Every line ends with a confidence (seen N× = observed that many times; candidate = fewer than 3 distinct observations; inferred = guessed from structure) and the evidence file to open.");
        sb.AppendLine();
        return sb.ToString();
    }

    public static string DataModel(LearnResult r)
    {
        var sb = new StringBuilder(Header(r, "Data model (learned)", "Tables, columns, inferred types and relations of the watched app's data."));
        if (r.Sources.Count > 0)
        {
            sb.AppendLine("## Sources");
            foreach (var s in r.Sources) sb.AppendLine($"- `{s}`");
            sb.AppendLine();
        }
        if (r.Backends.Count > 0)
        {
            sb.AppendLine("## Backends found by discovery");
            sb.AppendLine();
            sb.AppendLine("Where the app's data lives according to its config files, ODBC data sources and open connections (no credentials recorded).");
            foreach (var b in r.Backends) sb.AppendLine($"- {b.Describe()} — {(b.Readable ? "readable by App Watcher" : "not readable from this PC yet")}; inferred");
            sb.AppendLine();
        }
        sb.AppendLine("## Tables");
        sb.AppendLine();
        if (r.Tables.Count == 0) sb.AppendLine("_No tables known yet._");
        foreach (var t in r.Tables)
        {
            var written = t.WrittenEvidence.Seen > 0 ? $"written in {t.WrittenEvidence.Seen} effects; evidence: {t.WrittenEvidence.Pointer}" : "never seen written; evidence: snapshots/before.json";
            sb.AppendLine($"### {t.Qualified}");
            sb.AppendLine($"Rows: {t.RowCount}. Key: {(t.PrimaryKey.Count == 0 ? "(none)" : string.Join(", ", t.PrimaryKey))}. {written}{(t.Note is null ? "" : $". Note: {t.Note}")}");
            sb.AppendLine();
            sb.AppendLine("| Column | Type | Null? | Shape | Distinct | Filled from screen field | Confidence |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var c in t.Columns)
            {
                var maps = r.FieldMappings.Where(m => m.Table == t.Qualified && m.Column == c.Name).OrderByDescending(m => m.SqlSeen).ThenByDescending(m => m.ValueHashes.Count).ToList();
                var from = maps.Count == 0 ? "" : string.Join("; ", maps.Take(2).Select(m => $"{m.Screen} › {m.Field} ({m.Status})"));
                var conf = maps.Count == 0 ? "type inferred from values" : $"{maps[0].Label}; {maps[0].Pointer}";
                sb.AppendLine($"| {c.Name}{(c.IsPk ? " (PK)" : "")} | {c.Type} | {(c.Nullable ? "yes" : "no")} | `{c.Shape}` | {c.Distinct} | {from} | {conf} |");
            }
            sb.AppendLine();
        }
        if (r.SqlWrites.Count > 0)
        {
            sb.AppendLine("## Statements captured from SQL (exact)");
            sb.AppendLine();
            foreach (var w in r.SqlWrites)
                sb.AppendLine($"- `{w.Table}` {w.Kind} ({string.Join(", ", w.Columns.OrderBy(x => x).Take(20))}) — seen {w.Evidence.Seen}×; evidence: {w.Evidence.Pointer}");
            sb.AppendLine();
        }
        sb.AppendLine("## Relations (foreign key candidates)");
        sb.AppendLine();
        if (r.Relations.Count == 0) sb.AppendLine("_None found yet._");
        foreach (var rel in r.Relations)
        {
            var detail = rel.SnapshotRows > 0 ? $"{rel.SnapshotMatches} of {rel.SnapshotRows} rows match" : "";
            if (rel.NameHint) detail += (detail.Length > 0 ? ", " : "") + "column name matches table";
            sb.AppendLine($"- `{rel.FromTable}`.{rel.FromColumn} → `{rel.ToTable}`.{rel.ToColumn} ({detail}) — {rel.Evidence}");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static string Screens(LearnResult r)
    {
        var sb = new StringBuilder(Header(r, "Screens (learned)", "Each window of the app, its fields, which columns they fill and which tables the screen writes."));
        if (r.MainWindow is not null) sb.AppendLine($"Main window: **{r.MainWindow}** (the window users spend the most time in).");
        var sessions = r.Screens.Where(s => s.Kind == ScreenKinds.Session).Select(s => s.Title).ToList();
        if (sessions.Count > 0) sb.AppendLine($"Session windows (flows start after them): {string.Join(", ", sessions)}.");
        if (r.MessageWindows.Count > 0) sb.AppendLine($"Message windows (not screens; their text is in rules.md): {string.Join(", ", r.MessageWindows.OrderByDescending(m => m.Value).Select(m => $"{m.Key} ({m.Value}×)"))}.");
        sb.AppendLine();
        foreach (var s in r.Screens)
        {
            var key = s.Title;
            sb.AppendLine($"## {s.Title}");
            var kind = s.Kind == ScreenKinds.Screen ? "" : $" Kind: {s.Kind}.";
            var fields = s.LogicalFieldCount > 0 ? $" Fields: {s.LogicalFieldCount}." : "";
            sb.AppendLine($"Controls: {s.ControlCount}.{fields} Visits: {s.Visits}.{kind} Record: {s.File}");
            var reached = s.ReachedFrom is null ? "unknown" : $"{s.ReachedFrom}{(s.ReachedFromCount > 0 ? $" (seen {s.ReachedFromCount}×)" : " (from the screen record)")}";
            var menu = s.MenuPath is null ? "not seen" : $"{s.MenuPath} (seen {s.MenuPaths.GetValueOrDefault(s.MenuPath)}×)";
            sb.AppendLine($"Reached from: {reached}. Menu item before it opened: {menu}.");
            sb.AppendLine();
            var writes = r.ScreenWrites.Where(w => w.Screen == key).ToList();
            if (writes.Count > 0)
            {
                sb.AppendLine("**Tables written:**");
                foreach (var w in writes)
                {
                    var sql = w.SqlCount > 0 ? $"; {w.SqlCount} SQL statements" : "";
                    var diff = w.ScreenEffects > 0 ? $"{(w.Always ? "always" : "sometimes")} ({w.Count} of {w.ScreenEffects} saves{sql})" : $"from SQL statements ({w.SqlCount})";
                    sb.AppendLine($"- `{w.Table}` — {diff}; evidence: {w.Evidence.Pointer}");
                }
                sb.AppendLine();
            }
            var maps = r.FieldMappings.Where(m => m.Screen == key).ToList();
            sb.AppendLine("| Field | Typed | Type (from values) | Shapes seen | Column | Confidence | Evidence |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            var all = s.Fields.Concat(maps.Select(m => m.Field)).Distinct().ToList();
            if (all.Count == 0) sb.AppendLine("| (no input fields seen) | | | | | | |");
            foreach (var f in all)
            {
                var typed = s.FieldTyped.GetValueOrDefault(f);
                var type = s.FieldTypes.TryGetValue(f, out var ty) ? ty : "";
                var shapes = s.FieldShapes.TryGetValue(f, out var sh) ? string.Join(", ", sh.Take(4).Select(x => $"`{x}`")) : "";
                var fm = maps.Where(m => m.Field == f).ToList();
                if (fm.Count == 0) { sb.AppendLine($"| {Cell(f)} | {typed} | {type} | {shapes} | (not mapped yet) | {(typed > 0 ? $"seen {typed}×" : "inferred")} | {s.File} |"); continue; }
                foreach (var m in fm.Take(3)) sb.AppendLine($"| {Cell(f)} | {typed} | {type} | {shapes} | `{m.Table}`.{m.Column} | {m.Label} | {m.Pointer} |");
            }
            if (s.NeverUsed.Count > 0 && s.NeverUsed.Count < s.Fields.Count) sb.AppendLine().AppendLine($"Never typed into ({s.NeverUsed.Count}): {string.Join(", ", s.NeverUsed.Take(25).Select(Cell))}{(s.NeverUsed.Count > 25 ? ", ..." : "")}");
            if (s.FieldLookups.Count > 0)
            {
                sb.AppendLine().AppendLine("**Lookups (suggestion lists while typing):**");
                foreach (var (f, n) in s.FieldLookups.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
                    sb.AppendLine($"- {Cell(f)}: shows up to {s.LookupSizes.GetValueOrDefault(f)} suggestions{(s.LookupShapes.TryGetValue(f, out var sh) && sh.Count > 0 ? $", shaped like {string.Join(", ", sh.Take(3).Select(x => $"`{x}`"))}" : "")} — seen {n}×; evidence: events (dropdown)");
            }
            if (s.ActionResults.Count > 0)
            {
                sb.AppendLine().AppendLine("**What fills a grid (results after an action):**");
                foreach (var (a, ri) in s.ActionResults.OrderByDescending(x => x.Value.Seen).ThenBy(x => x.Key))
                {
                    var rows = ri.MinRows == ri.MaxRows ? $"{ri.MaxRows} rows" : $"{ri.MinRows} to {ri.MaxRows} rows";
                    sb.AppendLine($"- after {Cell(a)}: columns {string.Join(", ", ri.Columns.Take(15).Select(Cell))}; {rows}{(ri.EmptySeen > 0 ? $", empty {ri.EmptySeen}×" : "")} — seen {ri.Seen}×; evidence: events (result)");
                }
            }
            if (s.Actions.Count > 0)
            {
                sb.AppendLine().AppendLine($"Actions: {string.Join(", ", s.Actions.Take(25).Select(a => s.Shortcuts.TryGetValue(a, out var k) ? $"{Cell(a)} [{k}]" : Cell(a)))}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Rules(LearnResult r)
    {
        var sb = new StringBuilder(Header(r, "Rules (learned)", "Validation messages, messages after actions, calculated columns and numbering sequences."));
        sb.AppendLine("## Validation rules");
        sb.AppendLine();
        var validations = r.Rules.Where(v => v.Kind == "validation").ToList();
        if (validations.Count == 0) sb.AppendLine("_No message window has followed a value change yet._");
        else
        {
            sb.AppendLine("| Screen | Field | Value pattern | Message | Confidence | Evidence |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var v in validations) sb.AppendLine($"| {v.Screen} | {v.Field} | {v.Pattern} | {Cell(v.Message)} | {v.Evidence.Confidence} | {v.Evidence.Pointer} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Messages after an action");
        sb.AppendLine();
        var messages = r.Rules.Where(v => v.Kind == "message").ToList();
        if (messages.Count == 0) sb.AppendLine("_No message window has followed a button or menu press yet._");
        else
        {
            sb.AppendLine("| Screen | Action | Message | Confidence | Evidence |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var v in messages) sb.AppendLine($"| {v.Screen} | {v.Field} {v.Pattern} | {Cell(v.Message)} | {v.Evidence.Confidence} | {v.Evidence.Pointer} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Calculated columns");
        sb.AppendLine();
        if (r.Formulas.Count == 0) sb.AppendLine("_No formula explains at least 95% of the rows of any column yet._");
        foreach (var f in r.Formulas)
        {
            var alt = f.Alternatives.Count == 0 ? "" : $" (also fits: {string.Join("; ", f.Alternatives)})";
            var exc = f.Residual == 0 ? "" : $"; has exceptions: {f.Residual} row{(f.Residual == 1 ? "" : "s")} (keys {string.Join(", ", f.ResidualKeys)})";
            var untyped = f.UntypedChanges > 0 ? $"changed without being typed {f.UntypedChanges}×; " : "";
            sb.AppendLine($"- `{f.Table}`.{f.Column}: **{f.Expression}** [{f.Method}] explains {f.Explained} of {f.Tested} rows{(f.Rounded2 ? ", rounded to 2 places" : "")}{alt}{exc} — {untyped}{f.Evidence}");
        }
        sb.AppendLine();
        sb.AppendLine("## Numbering sequences");
        sb.AppendLine();
        if (r.Sequences.Count == 0) sb.AppendLine("_No sequence found yet._");
        foreach (var s in r.Sequences)
            sb.AppendLine($"- `{s.Table}`.{s.Column}: increases by one per new row{(string.IsNullOrEmpty(s.Prefix) ? "" : $", prefix \"{s.Prefix}\"")}{(s.Dense ? ", existing values form a dense range" : "")} — {s.Evidence}");
        sb.AppendLine();
        return sb.ToString();
    }

    public static string Flows(LearnResult r)
    {
        var sb = new StringBuilder(Header(r, "Flows (learned)", "Status flows of data columns and how users move between windows."));
        sb.AppendLine("## Status flows");
        sb.AppendLine();
        if (r.StatusFlows.Count == 0) sb.AppendLine("_No column with a small set of values has been seen changing yet._");
        foreach (var f in r.StatusFlows)
        {
            sb.AppendLine($"### `{f.Table}`.{f.Column} — {f.Evidence}");
            sb.AppendLine($"States in order: {string.Join(" → ", f.States)}");
            if (f.Distribution.Count > 0) sb.AppendLine($"Rows per state now: {string.Join(", ", f.Distribution.OrderByDescending(d => d.Value).ThenBy(d => d.Key).Select(d => $"{d.Key}={d.Value}"))}");
            if (f.Initial.Count > 0) sb.AppendLine($"New rows start as: {string.Join(", ", f.Initial.OrderByDescending(d => d.Value).ThenBy(d => d.Key).Select(d => $"{d.Key} ({d.Value}×)"))}");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine("stateDiagram-v2");
            foreach (var i in f.Initial.Keys.OrderBy(x => x)) sb.AppendLine($"    [*] --> {Id(i)}");
            foreach (var t in f.Transitions) sb.AppendLine($"    {Id(t.From)} --> {Id(t.To)} : {t.Evidence.Seen}x{(t.Rare ? " (rare)" : "")}");
            sb.AppendLine("```");
            sb.AppendLine();
            foreach (var t in f.Transitions) sb.AppendLine($"- {t.From} → {t.To}: seen {t.Evidence.Seen}×{(t.Rare ? " (rare)" : "")}; evidence: {t.Evidence.Pointer}");
            if (f.NeverSeen.Count > 0) sb.AppendLine($"- not allowed (inferred, never seen): {string.Join(", ", f.NeverSeen.Select(n => $"{n.from}→{n.to}"))}");
            sb.AppendLine();
        }
        sb.AppendLine("## User flows (window sequences)");
        sb.AppendLine();
        if (r.Flows.Count == 0) sb.AppendLine("_No flow away from the main window yet._");
        else
        {
            sb.AppendLine("| Flow (starts at) | Count | Median duration | Most common path | Users most often go back from |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in r.Flows)
                sb.AppendLine($"| {f.Key} | {f.Count} | {Duration(f.MedianMs)} | {string.Join(" → ", f.CommonPath)} ({f.CommonPathCount}×) | {(f.BackFrom is null ? "-" : $"{f.BackFrom} ({f.BackFromCount}×)")} |");
            sb.AppendLine();
            foreach (var f in r.Flows)
            {
                sb.AppendLine($"### Flow: {f.Key} — seen {f.Count}×; evidence: {f.Evidence.Pointer}");
                sb.AppendLine("Steps (screen: the actions seen there, with counts):");
                foreach (var win in f.CommonPath.Distinct())
                {
                    var controls = f.StepControls.TryGetValue(win, out var c) && c.Count > 0 ? string.Join(", ", c.Select(x => $"{x.name} ({x.count})")) : "(no action recorded on this screen)";
                    sb.AppendLine($"- {win}: {controls}");
                }
                foreach (var (win, controls) in f.StepControls.Where(kv => !f.CommonPath.Contains(kv.Key)))
                    sb.AppendLine($"- {win} (on other paths): {string.Join(", ", controls.Select(c => $"{c.name} ({c.count})"))}");
                sb.AppendLine();
            }
        }
        sb.AppendLine("## Navigation graph (directly-follows)");
        sb.AppendLine();
        if (r.Navigation.Count == 0) sb.AppendLine("_Nothing yet._");
        else
        {
            sb.AppendLine("Edges: how often users went from one window to the next, and the median time spent on the first window before moving on.");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine("flowchart LR");
            var ids = new Dictionary<string, string>();
            foreach (var e in r.Navigation.Take(80))
                sb.AppendLine($"    {NodeId(e.From, ids)}[\"{Cell(e.From)}\"] -->|\"{e.Count}× · {Duration(e.MedianMs)}\"| {NodeId(e.To, ids)}[\"{Cell(e.To)}\"]");
            sb.AppendLine("```");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static string Architecture(LearnResult r)
    {
        var sb = new StringBuilder(Header(r, "Architecture (learned)", "Modules grouped by shared tables, and an honest account of what was seen versus guessed."));
        if (r.Toolkit is not null || r.Backends.Count > 0 || r.Profile is not null)
        {
            sb.AppendLine("## Technology seen from the outside");
            sb.AppendLine();
            if (r.Toolkit is not null) sb.AppendLine($"- UI toolkit: {r.Toolkit} (from the program file's imports; inferred)");
            foreach (var b in r.Backends) sb.AppendLine($"- Backend: {b.Describe()}; inferred");
            if (r.Profile is not null) sb.AppendLine($"- Collector profile: {r.Profile}");
            sb.AppendLine();
        }
        sb.AppendLine("## Modules");
        sb.AppendLine();
        foreach (var m in r.Modules)
        {
            sb.AppendLine($"### {m.Name}");
            if (m.Screens.Count > 0) sb.AppendLine($"- Screens: {string.Join(", ", m.Screens)}");
            if (m.Tables.Count > 0) sb.AppendLine($"- Tables: {string.Join(", ", m.Tables.Select(t => $"`{t}`"))}");
            var rels = r.Relations.Where(x => m.Tables.Contains(x.FromTable) || m.Tables.Contains(x.ToTable)).Take(10).ToList();
            foreach (var rel in rels) sb.AppendLine($"- Relation: `{rel.FromTable}`.{rel.FromColumn} → `{rel.ToTable}`.{rel.ToColumn} ({rel.Evidence.Confidence})");
            sb.AppendLine();
        }
        sb.AppendLine("## What is seen vs inferred");
        sb.AppendLine();
        int seen(IEnumerable<Evidence> e) => e.Count(x => !x.Inferred && x.Seen > 0);
        int inf(IEnumerable<Evidence> e) => e.Count(x => x.Inferred || x.Seen == 0);
        sb.AppendLine("| Finding | Seen (observed) | Candidate / inferred |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Screen → table writes | {r.ScreenWrites.Count} | 0 |");
        sb.AppendLine($"| Field → column mappings | {r.FieldMappings.Count(m => m.IsSeen)} | {r.FieldMappings.Count(m => !m.IsSeen)} candidates |");
        sb.AppendLine($"| Relations | {seen(r.Relations.Select(x => x.Evidence))} | {inf(r.Relations.Select(x => x.Evidence))} |");
        sb.AppendLine($"| Calculated columns | {seen(r.Formulas.Select(x => x.Evidence))} | {inf(r.Formulas.Select(x => x.Evidence))} |");
        sb.AppendLine($"| Validation rules | {r.Rules.Count(x => x.Kind == "validation")} | 0 |");
        sb.AppendLine($"| Messages after actions | {r.Rules.Count(x => x.Kind == "message")} | 0 |");
        sb.AppendLine($"| Status flows | {seen(r.StatusFlows.Select(x => x.Evidence))} | {inf(r.StatusFlows.Select(x => x.Evidence))} |");
        sb.AppendLine($"| Sequences | {seen(r.Sequences.Select(x => x.Evidence))} | {inf(r.Sequences.Select(x => x.Evidence))} |");
        sb.AppendLine($"| Column types | 0 | {r.Tables.Sum(t => t.Columns.Count)} (from values) |");
        sb.AppendLine($"| Field types | 0 | {r.Screens.Sum(s => s.FieldTypes.Count)} (from typed values) |");
        sb.AppendLine();
        sb.AppendLine("Seen means the watcher recorded the cause and the effect together at least once (field mappings: at least 3 distinct values, or a captured SQL statement). Inferred means the structure suggests it but no user action proved it. Treat inferred and candidate lines as questions for the architect.");
        sb.AppendLine();
        var unwritten = r.Tables.Where(t => t.WrittenEvidence.Seen == 0 && !t.Skipped).Select(t => t.Qualified).ToList();
        if (unwritten.Count > 0) sb.AppendLine($"Tables never seen written ({unwritten.Count}): {string.Join(", ", unwritten.Take(30))}.");
        var skipped = r.Tables.Where(t => t.Skipped).Select(t => $"{t.Qualified} ({t.Note})").ToList();
        if (skipped.Count > 0) sb.AppendLine($"Tables skipped by size caps: {string.Join("; ", skipped)}.");
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in r.Warnings) sb.AppendLine($"- {w}");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static string Health(LearnResult r)
    {
        var h = r.Health;
        var sb = new StringBuilder(Header(r, "Health (learned)", "How much evidence there is and what would make the memory more accurate."));
        sb.AppendLine("| Measure | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Days with activity | {h.Days} |");
        sb.AppendLine($"| Events per day | {h.EventsPerDay:0} |");
        sb.AppendLine($"| Button and menu presses seen | {h.PressesSeen} (+ {h.PressesInferred} inferred from focus and a window change) |");
        sb.AppendLine($"| Save-type presses seen | {h.TriggerInvokes} |");
        sb.AppendLine($"| Message windows seen | {h.DialogsSeen} |");
        sb.AppendLine($"| Data checks (effects) | {h.EffectsTotal}, {h.EffectsWithDiff} with a change |");
        sb.AppendLine($"| Saves with a data diff | {h.SavesWithDiffPct:0}% |");
        sb.AppendLine($"| Fields typed into | {h.FieldsSeen} |");
        sb.AppendLine($"| Fields mapped to a column | {h.FieldsMapped} seen + {h.FieldsCandidate} candidates ({h.FieldsMappedPct:0}% seen) |");
        sb.AppendLine($"| Users and PCs watched | {Math.Max(1, h.UsersSeen)} user{(h.UsersSeen == 1 ? "" : "s")} on {Math.Max(1, h.MachinesSeen)} PC{(h.MachinesSeen == 1 ? "" : "s")} (names hashed, never stored) |");
        if (h.DiskUse.Count > 0) sb.AppendLine($"| Disk use | {string.Join(", ", h.DiskUse.Select(d => $"{d.Key} {Size(d.Value)}"))} |");
        if (r.Collector is not null) sb.AppendLine($"| Collector status | updated {r.Collector.Updated:yyyy-MM-dd HH:mm}: {r.Collector.WindowsSeen} windows, {r.Collector.DumpsOk} trees read, {r.Collector.DumpsFailed + r.Collector.DumpsEmpty} failed or empty, {r.Collector.PressesTotal} presses (UIA {r.Collector.PressesUia}, MSAA {r.Collector.PressesMsaa}, menu {r.Collector.PressesMenu}, inferred {r.Collector.PressesInferred}), {r.Collector.TabScreens} tab screens, {r.Collector.Dialogs} dialogs |");
        sb.AppendLine();
        if (r.Expectations.Count > 0)
        {
            sb.AppendLine("## What this app lets the watcher learn");
            sb.AppendLine();
            foreach (var e in r.Expectations) sb.AppendLine($"- {e}");
            sb.AppendLine();
        }
        if (h.CollectorProblems.Count > 0)
        {
            sb.AppendLine("## What the collector could not do");
            sb.AppendLine();
            foreach (var p in h.CollectorProblems) sb.AppendLine($"- {p}");
            sb.AppendLine();
        }
        sb.AppendLine("## What would improve accuracy");
        sb.AppendLine();
        foreach (var a in h.Advice) sb.AppendLine($"- {a}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
    private static string Cell(string s) => s.Replace("|", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
    private static string Id(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private static string NodeId(string s, Dictionary<string, string> ids)
    {
        if (!ids.TryGetValue(s, out var id)) ids[s] = id = "n" + ids.Count;
        return id;
    }
    public static string Duration(double ms) => ms < 1000 ? $"{ms:0} ms" : ms < 60_000 ? $"{ms / 1000:0.#} s" : $"{ms / 60_000:0.#} min";
}
