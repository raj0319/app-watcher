using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core.Export;

/// <summary>
/// The blueprint: the learned memory rearranged into the files an AI coding agent (or a person) rebuilds from:
/// screens, fields, navigation, workflows, calculations, business rules, the data model, permissions, integrations,
/// reports, coverage and the rebuild instructions. Every record carries a stable id, a verification status
/// (observed, single_observation, inferred, unknown) and the evidence files behind it; nothing is written as a fact
/// that the evidence does not show, and raw values typed by users never appear (the evidence files hold them, masked).
/// Pure: renders from a LearnResult (plus the screen records for control counts) into file name → text.
/// </summary>
public static class BlueprintWriter
{
    public const string Observed = "observed", Single = "single_observation", Inferred = "inferred", Unknown = "unknown";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>"CALC-3f9a2c1d": a stable id from the record's own key, the same at every learn.</summary>
    public static string Id(string prefix, params string[] parts)
        => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", parts))))[..8].ToLowerInvariant();

    private static string Status(int seen) => seen >= 2 ? Observed : seen == 1 ? Single : Unknown;

    private static string J(object o) => JsonSerializer.Serialize(o, Json) + "\n";

    public static Dictionary<string, string> Render(LearnResult r, IReadOnlyList<ScreenRecord>? records = null)
    {
        records ??= new List<ScreenRecord>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var screens = Screens(r, records);
        var calcs = Calculations(r);
        var rules = BusinessRules(r);
        files["screens.json"] = J(screens);
        files["fields.json"] = J(Fields(r, records));
        files["navigation_graph.json"] = J(Navigation(r));
        files["workflows.json"] = J(Workflows(r));
        files["calculations.json"] = J(calcs);
        files["business_rules.json"] = J(rules);
        files["database_model.json"] = J(DataModel(r));
        files["permissions.json"] = J(Permissions(r));
        files["integrations.json"] = J(Integrations(r));
        files["reports.json"] = J(Reports(r));
        files["coverage_report.json"] = J(Coverage(r, records, calcs, rules));
        files["application_overview.md"] = Overview(r, records);
        files["AI_REBUILD_INSTRUCTIONS.md"] = Instructions(r);
        files["evidence/README.md"] = EvidenceReadme();
        files["screenshots/README.md"] = ScreenshotsReadme(records);
        return files;
    }

    // ---- screens and fields ----

    private static List<object> Screens(LearnResult r, IReadOnlyList<ScreenRecord> records)
    {
        var list = new List<object>();
        foreach (var s in r.Screens.OrderBy(s => s.Title, StringComparer.Ordinal))
        {
            var rec = records.FirstOrDefault(x => Titles.Key(x.Title) == s.Title);
            var evidence = new List<string>();
            if (s.File.Length > 0 && s.File != "(no screen file)") evidence.Add(s.File); else evidence.Add("(events)"); // seen in the events only: its title, visits and actions, no control tree
            list.Add(new
            {
                id = Id("SCR", s.Title),
                title = s.Title,
                kind = s.Kind,
                status = s.File.Length > 0 && s.File != "(no screen file)" ? Observed : Single,
                reached_from = s.ReachedFrom,
                menu_path = s.MenuPath,
                visits = s.Visits,
                controls = rec?.Controls.Count ?? s.ControlCount,
                fields = s.Fields.Count,
                actions = s.Actions.OrderBy(a => a, StringComparer.Ordinal).ToList(),
                screenshot_folder = rec is null ? null : "docs/raw/screens/" + ShotStore.FolderFor(rec.Title),
                evidence,
            });
        }
        return list;
    }

    private static List<object> Fields(LearnResult r, IReadOnlyList<ScreenRecord> records)
    {
        var list = new List<object>();
        foreach (var s in r.Screens.OrderBy(s => s.Title, StringComparer.Ordinal))
        {
            var rec = records.FirstOrDefault(x => Titles.Key(x.Title) == s.Title);
            var logical = rec?.LogicalFields() ?? new List<(ControlRow row, string label)>();
            foreach (var f in s.Fields.OrderBy(f => f, StringComparer.Ordinal))
            {
                var row = logical.FirstOrDefault(x => x.label == f || Labels.Clean(x.row.AutomationId) == f).row;
                var typed = s.FieldTyped.GetValueOrDefault(f);
                var mapping = r.FieldMappings.FirstOrDefault(m => Titles.Key(m.Screen) == s.Title && m.Field == f);
                var evidence = new List<string>();
                if (s.File.Length > 0 && s.File != "(no screen file)") evidence.Add(s.File); else if (typed > 0) evidence.Add("(events)");
                if (mapping is not null) evidence.AddRange(mapping.Evidence.Files.Concat(mapping.SqlFiles));
                list.Add(new
                {
                    id = Id("FLD", s.Title, f),
                    screen_id = Id("SCR", s.Title),
                    screen = s.Title,
                    name = f,
                    control_type = row?.Type ?? "unknown",
                    automation_id = row?.AutomationId ?? "",
                    data_type = s.FieldTypes.GetValueOrDefault(f, typed > 0 ? "mixed" : Unknown),
                    typed_count = typed,
                    value_shapes = s.FieldShapes.GetValueOrDefault(f) ?? new List<string>(),
                    lookups = s.FieldLookups.GetValueOrDefault(f),
                    required = Unknown,
                    default_value = Unknown,
                    visibility = Unknown,
                    column = mapping is null ? null : new { table = mapping.Table, column = mapping.Column, status = mapping.IsSeen ? Observed : "candidate" },
                    status = typed > 0 ? Observed : row is not null ? Single : Unknown,
                    evidence = evidence.Distinct().ToList(),
                });
            }
        }
        return list;
    }

    // ---- navigation and workflows ----

    private static object Navigation(LearnResult r) => new
    {
        main_window = r.MainWindow,
        nodes = r.Screens.OrderBy(s => s.Title, StringComparer.Ordinal).Select(s => new { id = Id("SCR", s.Title), title = s.Title, kind = s.Kind }).ToList(),
        edges = r.Navigation.Select(n => new { from = Id("SCR", n.From), from_title = n.From, to = Id("SCR", n.To), to_title = n.To, seen = n.Count, median_ms = Math.Round(n.MedianMs), status = Status(n.Count) }).ToList(),
        message_windows = r.MessageWindows.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new { title = m.Key, seen = m.Value }).ToList(),
        note = "Edges are windows that followed one another in use (directly-follows), not proven links; a button's lead is in business_rules.json.",
    };

    private static List<object> Workflows(LearnResult r) =>
        r.Flows.Select(f => (object)new
        {
            id = Id("WF", f.Key),
            name = f.Key,
            seen = f.Count,
            median_ms = Math.Round(f.MedianMs),
            common_path = f.CommonPath,
            common_path_seen = f.CommonPathCount,
            steps = f.CommonPath.Select(w => new { screen = w, actions = f.StepControls.TryGetValue(w, out var c) ? c.Select(x => new { action = x.name, seen = x.count }).ToList() : new() }).ToList(),
            back_from = f.BackFrom,
            status = Status(f.Count),
            evidence = f.Evidence.Files,
        }).ToList();

    // ---- calculations and rules ----

    public static List<object> Calculations(LearnResult r)
    {
        var list = new List<object>();
        foreach (var c in r.Logic.Calculations)
            list.Add(new
            {
                id = Id("CALC", c.Screen, c.Field), screen = c.Screen, field = c.Field, formula = c.Formula, alternatives = c.Alternatives,
                fits = c.Fits, tested = c.Tested, source = c.FromOcr ? "pictures" : "screen",
                status = c.WithExceptions || c.Alternatives.Count > 0 ? Inferred : c.Fits >= 2 ? Observed : Single,
                contradictions = c.Exceptions, evidence = c.Evidence.Files,
                regression_cases = c.Evidence.Files.Select(f => new { inputs_and_output = "the values at " + f, note = "typed values are not copied into the blueprint; read them from the evidence file" }).ToList(),
            });
        foreach (var c in r.Logic.Unexplained)
            list.Add(new { id = Id("CALC", c.Screen, c.Field), screen = c.Screen, field = c.Field, formula = (string?)null, fits = c.Fits, tested = c.Tested, source = "screen", status = Unknown, contradictions = c.Tested - c.Fits, evidence = c.Evidence.Files, note = "the program fills this field; no simple formula over the screen's numbers fits enough of the observations" });
        foreach (var f in r.Formulas)
            list.Add(new
            {
                id = Id("CALC-DATA", f.Table, f.Column), table = f.Table, column = f.Column, formula = f.Expression, method = f.Method, alternatives = f.Alternatives,
                fits = f.Explained, tested = f.Tested, source = "data", status = f.Residual == 0 && f.Tested >= 3 ? Observed : Inferred, contradictions = f.Residual, rounded_to_2 = f.Rounded2, evidence = f.Evidence.Files,
            });
        return list;
    }

    public static List<object> BusinessRules(LearnResult r)
    {
        var list = new List<object>();
        foreach (var v in r.Rules)
            list.Add(new
            {
                id = Id("RULE", v.Screen, v.Field, v.Pattern, v.Message), kind = v.Kind == "validation" ? "validation_message" : "message_after_action",
                screen = v.Screen, trigger = v.Field, value_pattern = v.Pattern, message = v.Message, seen = v.Evidence.Seen, status = Status(v.Evidence.Seen), evidence = v.Evidence.Files,
            });
        foreach (var l in r.Logic.Links)
            list.Add(new { id = Id("LINK", l.Screen, l.When, l.Field), kind = "field_dependency", screen = l.Screen, cause = l.WhenText, field = l.Field, seen = l.Seen, of = l.CauseCount, status = Status(l.Seen), evidence = l.Evidence.Files });
        foreach (var b in r.Logic.Leads)
            list.Add(new { id = Id("LEAD", b.Screen, b.Button, b.Opens), kind = b.Message ? "button_shows_message" : "button_opens_window", screen = b.Screen, button = b.Button, opens = b.Opens, seen = b.Seen, status = Status(b.Seen), evidence = b.Evidence.Files });
        foreach (var h in r.Logic.Hints)
            list.Add(new { id = Id("HINT", h.Screen, h.Control, h.Text), kind = "hover_tip", screen = h.Screen, control = h.Control, text = h.Text, seen = h.Seen, status = Status(h.Seen), evidence = h.Evidence.Files });
        foreach (var s in r.StatusFlows)
            list.Add(new { id = Id("STATUS", s.Table, s.Column), kind = "status_transitions", table = s.Table, column = s.Column, states = s.States, transitions = s.Transitions.Select(t => new { from = t.From, to = t.To, seen = t.Evidence.Seen }).ToList(), status = Status(s.Evidence.Seen), evidence = s.Evidence.Files });
        foreach (var q in r.Sequences)
            list.Add(new { id = Id("SEQ", q.Table, q.Column), kind = "numbering_sequence", table = q.Table, column = q.Column, prefix = q.Prefix, dense = q.Dense, status = Status(q.Evidence.Seen), evidence = q.Evidence.Files });
        foreach (var w in r.ScreenWrites)
            list.Add(new { id = Id("SAVE", w.Screen, w.Table), kind = "screen_writes_table", screen = w.Screen, table = w.Table, seen = w.Count, of = w.ScreenEffects, every_save = w.Always, status = Status(w.Count), evidence = w.Evidence.Files });
        return list;
    }

    // ---- data model, permissions, integrations, reports ----

    private static object DataModel(LearnResult r) => new
    {
        status = r.Tables.Count > 0 ? "read from the configured data sources (read-only snapshots)" : "unknown: no readable data source was configured",
        physical_schema = r.Tables.Count > 0 ? Observed : Unknown,
        sources = r.Sources,
        backends = r.Backends.Where(b => !b.IsService).Select(b => new { kind = b.Kind, host = b.Host, port = b.Port, database = b.Database, found_in = b.Where, readable = b.Readable, status = Inferred }).ToList(),
        tables = r.Tables.Select(t => new
        {
            id = Id("TBL", t.Qualified), name = t.Qualified, primary_key = t.PrimaryKey, rows = t.RowCount, skipped = t.Skipped, note = t.Note,
            columns = t.Columns.Select(c => new { name = c.Name, type = c.Type, nullable = c.Nullable, shape = c.Shape, distinct = c.Distinct, primary_key = c.IsPk }).ToList(),
            written_by_screens = r.ScreenWrites.Where(w => w.Table == t.Qualified).Select(w => w.Screen).ToList(), status = Observed,
        }).ToList(),
        field_mappings = r.FieldMappings.Select(m => new { id = Id("MAP", m.Screen, m.Field, m.Table, m.Column), screen = m.Screen, field = m.Field, table = m.Table, column = m.Column, status = m.IsSeen ? Observed : "candidate", seen = m.Evidence.Seen, distinct_values = m.ValueHashes.Count, sql_statements = m.SqlSeen, evidence = m.Evidence.Files.Concat(m.SqlFiles).Distinct().ToList() }).ToList(),
        relations = r.Relations.Select(x => new { id = Id("REL", x.FromTable, x.FromColumn, x.ToTable, x.ToColumn), from = $"{x.FromTable}.{x.FromColumn}", to = $"{x.ToTable}.{x.ToColumn}", matches = x.SnapshotMatches, rows = x.SnapshotRows, name_hint = x.NameHint, status = x.Evidence.Seen >= 2 ? Observed : Inferred, evidence = x.Evidence.Files }).ToList(),
        note = "Tables come from data sources a person configured; without one the model is unknown and the UI alone never names tables or keys.",
    };

    private static object Permissions(LearnResult r) => new
    {
        status = Unknown,
        roles_tested = new List<string>(),
        note = "Nothing is captured about roles; a permission matrix needs authorized test accounts per role and a driven run per role.",
        access_messages = r.Rules.Where(v => LooksLikeAccess(v.Message)).Select(v => new { screen = v.Screen, after = v.Field, message = v.Message, seen = v.Evidence.Seen, evidence = v.Evidence.Files }).ToList(),
    };

    private static bool LooksLikeAccess(string m) => m.Contains("access", StringComparison.OrdinalIgnoreCase) || m.Contains("permission", StringComparison.OrdinalIgnoreCase) || m.Contains("protected", StringComparison.OrdinalIgnoreCase) || m.Contains("required level", StringComparison.OrdinalIgnoreCase) || m.Contains("not authorized", StringComparison.OrdinalIgnoreCase);

    private static object Integrations(LearnResult r) => new
    {
        status = r.Backends.Count > 0 || r.Sources.Count > 0 ? "discovered, not exercised" : Unknown,
        data_servers = r.Backends.Where(b => !b.IsService).Select(b => new { kind = b.Kind, host = b.Host, port = b.Port, database = b.Database, found_in = b.Where, status = Inferred }).ToList(),
        services = r.Backends.Where(b => b.IsService).Select(b => new { kind = "web service the program talks to", host = b.Host, found_in = b.Where, status = Inferred }).ToList(),
        data_sources_read = r.Sources,
        note = "File exchange, APIs, printing, e-mail and webhooks are not observed by the watcher; adapters need documented contracts and test endpoints.",
    };

    private static object Reports(LearnResult r) => new
    {
        status = Unknown,
        report_screens = r.Screens.Where(s => s.Title.Contains("report", StringComparison.OrdinalIgnoreCase) || s.Title.Contains("print", StringComparison.OrdinalIgnoreCase))
            .Select(s => new { id = Id("SCR", s.Title), title = s.Title, visits = s.Visits, evidence = s.File }).ToList(),
        note = "Report layouts and printouts are not captured; the screens that select report criteria are listed.",
    };

    // ---- coverage ----

    private static object Coverage(LearnResult r, IReadOnlyList<ScreenRecord> records, List<object> calcs, List<object> rules)
    {
        var h = r.Health; var c = r.Collector;
        return new
        {
            period = new { from = r.FirstEvent, to = r.LastEvent, days = h.Days, events = r.EventCount, sessions = r.SessionCount },
            screens = new { recorded = r.Screens.Count(s => s.File.Length > 0 && s.File != "(no screen file)"), seen_in_events = r.Screens.Count, documents_never_recorded = h.DocumentsNeverRecorded, records_on_disk = records.Count },
            presses = c is null ? null : new { total = c.PressesTotal, uia = c.PressesUia, msaa = c.PressesMsaa, menu = c.PressesMenu, inferred = c.PressesInferred, from_pointer = c.PressesPointer, note = c.PressesUia == 0 && c.PressesMsaa == 0 ? "every press came from a fallback: counts are a lower bound" : null },
            messages = h.DialogsSeen,
            typed_fields = h.FieldsSeen,
            typed_but_unrecorded_screens = h.TypedButUnrecorded,
            fields_mapped = new { seen = h.FieldsMapped, candidates = h.FieldsCandidate },
            workflows = r.Flows.Count,
            calculations = new { total = calcs.Count, observed = calcs.Count(x => StatusOf(x) == Observed), inferred = calcs.Count(x => StatusOf(x) == Inferred), unknown = calcs.Count(x => StatusOf(x) == Unknown), single = calcs.Count(x => StatusOf(x) == Single) },
            business_rules = new { total = rules.Count, observed = rules.Count(x => StatusOf(x) == Observed), single = rules.Count(x => StatusOf(x) == Single) },
            data = new { tables = r.Tables.Count, effects = h.EffectsTotal, effects_with_change = h.EffectsWithDiff },
            pictures = c?.Pictures ?? 0,
            questions = r.Logic.Questions.Count,
            not_used = new { buttons = r.Logic.NotUsed.Count(n => n.Kind == "button"), tabs = r.Logic.NotUsed.Count(n => n.Kind == "tab"), fields = r.Logic.NotUsed.Count(n => n.Kind == "field"), menu_items = r.Logic.NotUsed.Count(n => n.Kind == "menu item") },
            collector_problems = h.CollectorProblems,
            unknown = new[] { "permissions and roles", "reports and printouts", "integrations beyond discovery", "screens nobody opened", "rules the program applies on the server" },
        };
    }

    private static string StatusOf(object o)
    {
        var p = o.GetType().GetProperty("status");
        return p?.GetValue(o) as string ?? Unknown;
    }

    // ---- the markdown files ----

    private static string Overview(LearnResult r, IReadOnlyList<ScreenRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.AppName}: application overview (blueprint)");
        sb.AppendLine();
        sb.AppendLine($"Learned by App Watcher from {r.EventCount} events in {r.SessionCount} session(s) between {r.FirstEvent:yyyy-MM-dd} and {r.LastEvent:yyyy-MM-dd}, by watching people use the program. No source code was read.");
        sb.AppendLine();
        sb.AppendLine("## What the files hold");
        sb.AppendLine();
        sb.AppendLine("| File | Content | Status values |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| screens.json | every window and document recorded, with its record file and picture folder | observed, single_observation |");
        sb.AppendLine("| fields.json | the fields of each screen: type, how often typed, value shapes, lookups, the column it maps to | observed, single_observation, unknown |");
        sb.AppendLine("| navigation_graph.json | which window followed which, how often | observed, single_observation |");
        sb.AppendLine("| workflows.json | the flows people run: path, actions per step, duration | observed, single_observation |");
        sb.AppendLine("| calculations.json | numbers the program fills in and the formula that keeps fitting, with the observations | observed, inferred, unknown |");
        sb.AppendLine("| business_rules.json | messages after values and actions, field dependencies, button leads, hover tips, status flows, sequences, what each screen writes | observed, single_observation |");
        sb.AppendLine("| database_model.json | tables read from configured data sources, field-to-column mappings, relations | observed, candidate, inferred, unknown |");
        sb.AppendLine("| permissions.json, integrations.json, reports.json | what discovery found; the rest is marked unknown | inferred, unknown |");
        sb.AppendLine("| coverage_report.json | how much was seen and what the collector could not do | |");
        sb.AppendLine();
        sb.AppendLine("## In numbers");
        sb.AppendLine();
        sb.AppendLine($"- Screens recorded: {r.Screens.Count(s => s.File.Length > 0 && s.File != "(no screen file)")} (records on disk: {records.Count}); main window: {r.MainWindow ?? "unknown"}");
        sb.AppendLine($"- Workflows: {r.Flows.Count}; message windows: {r.MessageWindows.Count}; calculations: {r.Logic.Calculations.Count} found, {r.Logic.Unexplained.Count} unexplained; dependencies: {r.Logic.Links.Count}; button leads: {r.Logic.Leads.Count}");
        sb.AppendLine($"- Tables: {r.Tables.Count}; field mappings: {r.FieldMappings.Count(m => m.IsSeen)} seen, {r.FieldMappings.Count(m => !m.IsSeen)} candidates");
        sb.AppendLine($"- Questions for an experienced user: {r.Logic.Questions.Count} (in logic.md under evidence/)");
        sb.AppendLine();
        sb.AppendLine("## What is not in here");
        sb.AppendLine();
        sb.AppendLine("- Permissions and roles: nothing was captured (one user watched).");
        sb.AppendLine("- Reports and printouts: not captured.");
        sb.AppendLine("- Rules the program applies on the server, screens nobody opened, and the physical database when no data source was configured.");
        sb.AppendLine("- Raw values typed by users: the evidence files hold them (masked where personal); the blueprint holds shapes and counts.");
        return sb.ToString();
    }

    private static string Instructions(LearnResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AI rebuild instructions");
        sb.AppendLine();
        sb.AppendLine("This blueprint is evidence-based and incomplete by construction: it holds what App Watcher saw people do in the program, not the program's code. Build from it like this.");
        sb.AppendLine();
        sb.AppendLine("1. Read application_overview.md, then screens.json and fields.json: recreate every recorded screen with its fields (control_type, data_type) and its actions. A field's `required`, `default_value` and `visibility` are `unknown` unless business_rules.json says otherwise.");
        sb.AppendLine("2. Read navigation_graph.json and workflows.json: wire the windows the way people moved between them; a workflow's steps are the actions seen on each screen.");
        sb.AppendLine("3. Implement every record whose status is `observed`. Treat `single_observation` as probable and `inferred` as a hypothesis to confirm with a test against the original; never implement `unknown` as behaviour.");
        sb.AppendLine("4. For each calculation, the regression cases name the evidence lines that hold the observed inputs and outputs: build a test from each before trusting the formula. A formula with alternatives listed is ambiguous on the evidence.");
        sb.AppendLine("5. database_model.json describes tables only where a data source was read; otherwise design the storage from fields.json and business_rules.json and say so.");
        sb.AppendLine("6. permissions.json, integrations.json and reports.json are mostly `unknown`: obtain test accounts, contracts and sample printouts before implementing them.");
        sb.AppendLine("7. Keep a trace from every implemented requirement to the record id and the evidence file it came from.");
        sb.AppendLine("8. Report coverage against coverage_report.json; never claim equivalence for what it lists as unknown or never recorded.");
        sb.AppendLine();
        sb.AppendLine("## Open questions the watcher would ask an experienced user");
        sb.AppendLine();
        if (r.Logic.Questions.Count == 0) sb.AppendLine("- None recorded.");
        foreach (var q in r.Logic.Questions.Take(60)) sb.AppendLine($"- {q.Text}");
        sb.AppendLine();
        sb.AppendLine("## Where the evidence is");
        sb.AppendLine();
        sb.AppendLine("- `../docs/raw/screens/*.md`: the control tree of each screen; `../docs/raw/screens/shots/<screen>/`: the pictures of its states with their OCR text.");
        sb.AppendLine("- `evidence/`: the learned memory (data-model, screens, rules, flows, architecture, health, logic) with a confidence and an evidence pointer on every line.");
        sb.AppendLine("- `../events.xes`: the event log for process mining; the `events/*.jsonl` lines the records cite live in the watcher's data folder (they hold typed values and are not part of the export).");
        return sb.ToString();
    }

    private static string EvidenceReadme() =>
        "# Evidence\n\nThe learned memory files (data-model.md, screens.md, rules.md, flows.md, architecture.md, health.md, logic.md, logic.json) are copied here; every line ends with `seen N×` or `inferred` and the file to open. The screen records and the pictures are under `../docs/raw/screens/`; the events the records cite stay in the watcher's data folder because they hold typed values.\n";

    private static string ScreenshotsReadme(IReadOnlyList<ScreenRecord> records)
    {
        var sb = new StringBuilder("# Screenshots\n\nOne folder per screen under `../docs/raw/screens/shots/`, each with an `index.md` (which state each picture shows) and the OCR text next to every picture. Pictures show the program's window only; review them for sensitive content before sharing.\n\n");
        foreach (var rec in records.OrderBy(x => x.Title, StringComparer.Ordinal)) sb.AppendLine($"- {Titles.Key(rec.Title)}: `../docs/raw/screens/{ShotStore.FolderFor(rec.Title)}/`");
        return sb.ToString();
    }
}
