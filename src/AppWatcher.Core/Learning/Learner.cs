using System.Text.RegularExpressions;
using AppWatcher.Core.Events;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sql;

namespace AppWatcher.Core.Learning;

/// <summary>
/// Runs every heuristic over the evidence folder. Incremental: sessions older than the session gap are
/// processed once and their counters kept in LearnState; the open session is re-processed on a copy each
/// time. Snapshot-based findings (types, relations, formulas, status distribution) are recomputed every learn.
/// Pure: no UI, no network, nothing written to the watched app.
/// </summary>
public static class Learner
{
    public const string StateFile = "learn-state.json";

    /// <summary>
    /// The title masks of the settings and the profile (the company name in the main title, quoted record names), applied to
    /// every title key: memory text and the file names of screens and pictures alike. The watcher calls it at start too, so a
    /// screen file is never named after the company.
    /// </summary>
    public static void ApplyTitleMasks(WatcherConfig? config)
    {
        if (config is null) return;
        // built aside and published once: collector threads read the list while a learn runs
        var masks = config.Privacy.TitleMasks.ToList();
        var profile = config.Profile is { Length: > 0 } pn ? Discovery.ProfileStore.ByName(pn) : null;
        if (profile is not null) foreach (var m in profile.TitleMasks) if (!masks.Contains(m)) masks.Add(m);
        Titles.MaskQuoted = config.Privacy.MaskTitles;
        Titles.MaskPatterns = masks;
    }

    public static LearnInput LoadInput(AppPaths paths, string appName, WatcherConfig? config = null, DateTime? since = null)
    {
        ApplyTitleMasks(config);
        var profile = config?.Profile is { Length: > 0 } pn ? Discovery.ProfileStore.ByName(pn) : null;
        var input = new LearnInput
        {
            AppName = appName,
            Events = EventReader.ReadAll(paths.Events, since),
            Effects = EffectRecord.ReadAll(paths.Effects),
            Latest = DataSnapshot.Load(paths.BeforeSnapshot),
            Screens = ScreenWriter.ReadAll(paths.Screens),
            SqlStatements = config?.SqlCapture.Enabled == true ? SqlLogReaders.ReadAll(config.SqlCapture.LogPath, config.App?.ProcessName) : new(),
            Triggers = config?.EffectTriggers ?? WatcherConfig.DefaultTriggers.ToList(),
            SqlCaptureEnabled = config?.SqlCapture.Enabled == true,
            HasDataSources = config is null || config.DataSources.Any(d => d.Enabled),
            Backends = config?.Backends ?? new(),
            Toolkit = config?.Toolkit,
            Profile = config?.Profile,
            Collector = CollectorStatus.Load(paths.Root),
            SessionWindowPatterns = config?.Collector.SessionWindows ?? new(),
            DiskUse = DiskUse(paths),
            OcrStates = OcrLogic.Load(paths.Screens),
            BackendKind = profile?.BackendKind,
        };
        // fold titles that share a structure but differ in record ids or names
        var normalizer = TitleNormalizer.FromGroups(ScreenWriter.TitleGroups(paths.Screens));
        if (normalizer.Count > 0) ApplyTitles(input, normalizer);
        return input;
    }

    /// <summary>Rewrites every window title in the input to its normalized pattern (used by LoadInput and tests).</summary>
    public static void ApplyTitles(LearnInput input, TitleNormalizer normalizer)
    {
        foreach (var e in input.Events)
        {
            e.Win = normalizer.Apply(e.Win);
            if (e.Act is Acts.Open or Acts.Close or Acts.Dialog or Acts.Foreground && e.Ct == "Window") e.Name = normalizer.Apply(e.Name);
        }
        foreach (var f in input.Effects) f.Win = normalizer.Apply(f.Win);
        foreach (var s in input.Screens) s.Title = normalizer.Apply(s.Title);
        foreach (var o in input.OcrStates) o.Screen = normalizer.Apply(o.Screen);
    }

    private static Dictionary<string, long> DiskUse(AppPaths paths)
    {
        var d = new Dictionary<string, long>();
        foreach (var (name, dir) in new[] { ("events", paths.Events), ("screens", paths.Screens), ("effects", paths.Effects), ("snapshots", paths.Snapshots) })
        {
            try { d[name] = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
            catch { d[name] = 0; }
        }
        return d;
    }

    /// <summary>Full learn from scratch (no persisted state).</summary>
    public static LearnResult Learn(LearnInput input) => Learn(input, new LearnState(), out _);

    /// <summary>
    /// Incremental learn: processes what is newer than the state's watermark, commits closed sessions into
    /// the returned <paramref name="committed"/> state (save it), and returns the full current result.
    /// </summary>
    public static LearnResult Learn(LearnInput input, LearnState state, out LearnState committed)
    {
        var now = input.Now ?? DateTime.Now;
        var cutoff = now - FlowLearner.SessionGap;
        // names on one line, for old event files too ("Net\r\nPrice" from a wrapped grid header is "Net Price")
        foreach (var e in input.Events)
        {
            e.Name = Titles.OneLine(e.Name);
            // a label that is the record's name from its window's title is data: the field goes by its cleaned automation id
            if (e.Act is Acts.Value or Acts.Focus or Acts.Dropdown && Titles.IsQuotedInTitle(e.Name, e.Win)) e.Name = Screens.Labels.Clean(e.Aid);
        }
        var wasIncremental = state.Watermark is not null;
        var keyIndex = RelationFinder.BuildKeyIndex(input.Latest);
        var triggers = BuildTriggerRegex(input.Triggers);
        var sessionWindows = SessionWindows(input);
        var dialogWindows = DialogWindows(input);
        // popup menus recorded as windows are not flow steps either; they cut like prompts
        var flowCuts = new HashSet<string>(dialogWindows); flowCuts.UnionWith(MenuWindows(input));

        var events = input.Events.Where(e => state.Watermark is null || e.T > state.Watermark.Value).OrderBy(e => e.T).ToList();
        AsDialogEvents(events, input.Screens, dialogWindows);
        var knownFields = OcrLogic.KnownFields(input.Screens);
        var ocr = input.OcrStates.Where(o => !state.OcrFiles.Contains(o.File)).OrderBy(o => o.T).ToList();
        var usedOcr = new HashSet<OcrState>();
        var effects = input.Effects.Where(f => !state.EffectFiles.Contains(f.File)).OrderBy(f => f.T).ToList();
        var sqls = input.SqlStatements.Where(s => !state.SqlSources.Contains(s.Source)).OrderBy(s => s.T).ToList();
        var sessions = FlowLearner.Sessions(events);

        LearnState working = state;
        var openSeen = false;
        void Open() { if (!openSeen) { working = state.Clone(); openSeen = true; } }

        var usedEffects = new HashSet<EffectRecord>();
        var usedSql = new HashSet<SqlStatement>();
        foreach (var session in sessions)
        {
            var start = session[0].T; var end = session[^1].T;
            var closed = end <= cutoff;
            if (!closed) Open();
            var fx = effects.Where(f => !usedEffects.Contains(f) && f.T >= start && f.T <= end.AddMinutes(5)).ToList();
            var sq = sqls.Where(s => !usedSql.Contains(s) && s.T >= start && s.T <= end.AddMinutes(5)).ToList();
            foreach (var f in fx) usedEffects.Add(f);
            foreach (var s in sq) usedSql.Add(s);
            var oc = ocr.Where(o => !usedOcr.Contains(o) && o.T >= start.AddSeconds(-1) && o.T <= end.AddSeconds(5)).ToList();
            foreach (var o in oc) usedOcr.Add(o);
            ProcessSession(working, session, fx, sq, keyIndex, triggers, sessionWindows, flowCuts, oc, knownFields: knownFields);
            if (closed && !openSeen) state.Watermark = end;
        }
        // pictures outside any session (the collector pictured a state while no event was written)
        var loose = ocr.Where(o => !usedOcr.Contains(o)).ToList();
        if (loose.Count > 0)
        {
            if (loose.Any(o => o.T > cutoff)) Open();
            OcrLogic.Accumulate(working, new List<UiEvent>(), loose, knownFields);
        }
        // effects and statements outside any session (idle file changes, logs without timestamps)
        foreach (var f in effects.Where(f => !usedEffects.Contains(f)))
        {
            if (f.T > cutoff) Open();
            ProcessSession(working, new List<UiEvent>(), new List<EffectRecord> { f }, new List<SqlStatement>(), keyIndex, triggers, sessionWindows, dialogWindows, null, countSession: false);
        }
        foreach (var s in sqls.Where(s => !usedSql.Contains(s)))
        {
            if (s.T > cutoff) Open();
            ProcessSession(working, new List<UiEvent>(), new List<EffectRecord>(), new List<SqlStatement> { s }, keyIndex, triggers, sessionWindows, dialogWindows, null, countSession: false);
        }

        committed = state;
        var r = Finalize(working, input, sessionWindows, dialogWindows);
        r.Incremental = wasIncremental;
        return r;
    }

    public static Regex BuildTriggerRegex(IEnumerable<string> words)
    {
        var alt = string.Join("|", words.Select(w => w.Trim()).Where(w => w.Length > 0).Select(Regex.Escape));
        return new Regex(alt.Length == 0 ? "(?!)" : $@"(?<![\p{{L}}]){alt}(?![\p{{L}}])", RegexOptions.IgnoreCase);
    }

    /// <summary>Window keys that are session windows (splash, login): from the screen records' kind, their structure, or the profile's patterns.</summary>
    public static HashSet<string> SessionWindows(LearnInput input)
    {
        var set = new HashSet<string>();
        var first = true;
        foreach (var rec in input.Screens.OrderBy(s => s.Captured))
        {
            var key = Titles.Key(rec.Title);
            if (rec.Kind == ScreenKinds.Session || DialogDetector.IsSessionWindow(rec.Title, rec.Controls, first && rec.Kind != ScreenKinds.Dialog, input.SessionWindowPatterns))
                set.Add(key);
            first = false;
        }
        foreach (var e in input.Events)
        {
            var key = Titles.Key(e.Win);
            if (key.Length > 0 && input.SessionWindowPatterns.Any(p => DialogDetector.Wildcard(key, p))) set.Add(key);
        }
        return set;
    }

    /// <summary>
    /// Window keys that are message boxes: records the collector marked as dialogs, records whose shape says so (re-checked
    /// with the current detector, so records made by an older collector count too), and windows of dialog events.
    /// </summary>
    public static HashSet<string> DialogWindows(LearnInput input)
    {
        var set = new HashSet<string>();
        foreach (var rec in input.Screens)
        {
            var key = Titles.Key(rec.Title);
            if (key.Length == 0) continue;
            if (rec.Kind == ScreenKinds.Dialog || DialogDetector.Detect("", rec.Controls).isDialog) set.Add(key);
        }
        foreach (var e in input.Events.Where(e => e.Act == Acts.Dialog))
        {
            var key = Titles.Key(e.Name.Length > 0 ? e.Name : e.Win);
            if (key.Length > 0) set.Add(key);
        }
        return set;
    }

    /// <summary>
    /// An older collector recorded a message box as a screen: its "open" events become dialog events (with the body text the
    /// record holds), so the rules learner sees the message after the action that caused it, as it would with a current collector.
    /// </summary>
    public static void AsDialogEvents(List<UiEvent> events, List<ScreenRecord> screens, HashSet<string> dialogWindows)
    {
        if (dialogWindows.Count == 0) return;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rec in screens)
        {
            var key = Titles.Key(rec.Title);
            if (!dialogWindows.Contains(key) || bodies.ContainsKey(key)) continue;
            var (_, body) = DialogDetector.Detect("", rec.Controls);
            bodies[key] = string.IsNullOrWhiteSpace(body) ? key : body!;
        }
        foreach (var e in events)
        {
            if (e.Act != Acts.Open || e.Ct != "Window") continue;
            var key = Titles.Key(e.Name.Length > 0 ? e.Name : e.Win);
            if (!dialogWindows.Contains(key)) continue;
            e.Act = Acts.Dialog;
            if (string.IsNullOrWhiteSpace(e.Val)) e.Val = bodies.GetValueOrDefault(key, key);
        }
    }

    /// <summary>Window keys that are popup menus recorded as windows (their items and access keys are kept; they are not screens).</summary>
    public static HashSet<string> MenuWindows(LearnInput input) =>
        new(input.Screens.Where(r => r.Kind == ScreenKinds.Menu || DialogDetector.IsMenuWindow(r.Controls)).Select(r => Titles.Key(r.Title)).Where(k => k.Length > 0));

    private static void ProcessSession(LearnState s, List<UiEvent> session, List<EffectRecord> effects, List<SqlStatement> sqls,
        Dictionary<string, HashSet<(string table, string col)>> keyIndex, Regex triggers, HashSet<string> sessionWindows, HashSet<string>? dialogWindows = null, List<OcrState>? ocr = null, bool countSession = true,
        Dictionary<string, HashSet<string>>? knownFields = null)
    {
        if (session.Count > 0)
        {
            if (countSession) s.Sessions++;
            s.EventCount += session.Count;
            var first = session[0].T; var last = session[^1].T;
            if (s.FirstEvent is null || first < s.FirstEvent) s.FirstEvent = first;
            if (s.LastEvent is null || last > s.LastEvent) s.LastEvent = last;
            foreach (var g in session.GroupBy(e => e.T.ToString("yyyy-MM-dd"))) s.EventsPerDay[g.Key] = s.EventsPerDay.GetValueOrDefault(g.Key) + g.Count();
            s.TriggerInvokes += session.Count(e => e.Act == Acts.Invoke && (triggers.IsMatch(e.Name) || triggers.IsMatch(e.Aid)));
            s.PressesSeen += session.Count(e => e.Act == Acts.Invoke && e.How != PressHow.Inferred);
            s.PressesInferred += session.Count(e => e.Act == Acts.Invoke && e.How == PressHow.Inferred);
            s.DialogsSeen += session.Count(e => e.Act == Acts.Dialog);
            s.TabSelections += session.Count(e => e.Act == Acts.Select && e.Ct == "TabItem" && Titles.Key(e.Win).Contains(" › ", StringComparison.Ordinal));
            foreach (var e in session)
            {
                var key = Titles.Key(e.Win);
                if (key.Length == 0) continue;
                if (key.Contains(" › ", StringComparison.Ordinal) && !MdiWorkspace.IsClassLikeName(key[(key.LastIndexOf(" › ", StringComparison.Ordinal) + 3)..])) s.DocumentsSeen.Add(key);
                // the user worked in a field (focus moved to it) versus a typed value recorded there: a screen with many of the first and none of the second lost its typing
                if (e.Act == Acts.Focus && e.Ct is "Edit" or "ComboBox" or "DataItem" or "Spinner" or "Document") s.FieldFocuses[key] = s.FieldFocuses.GetValueOrDefault(key) + 1;
                if (e.Act == Acts.Value && e.By != ValueBy.App) s.TypedValues[key] = s.TypedValues.GetValueOrDefault(key) + 1;
            }
            foreach (var st in session.Where(e => e.Act == Acts.Start && !string.IsNullOrEmpty(e.Val)))
            {
                var um = Regex.Match(st.Val!, @"user:(\w+)"); if (um.Success) s.Users.Add(um.Groups[1].Value);
                var mm = Regex.Match(st.Val!, @"machine:(\w+)"); if (mm.Success) s.Machines.Add(mm.Groups[1].Value);
            }
            GridEditors.NameEditors(session); // an editor over a grid cell without a name of its own takes the column's name
            FlowLearner.Accumulate(s, session, sessionWindows, dialogWindows);
            ValidationRuleLearner.Accumulate(s, session);
            AccumulateScreens(s, session);
            LogicLearner.Accumulate(s, session);
        }
        if (ocr is { Count: > 0 }) OcrLogic.Accumulate(s, session, ocr, knownFields);
        foreach (var fx in effects)
        {
            s.EffectFiles.Add(fx.File);
            s.EffectsTotal++;
            if (fx.Diff.IsEmpty) continue;
            s.EffectsWithDiff++;
            FieldMapper.AccumulateEffect(s, session, fx);
            FormulaFitter.AccumulateUntyped(s, session, fx);
            StatusFlowLearner.AccumulateEffect(s, fx);
            SequenceLearner.AccumulateEffect(s, fx);
            RelationFinder.AccumulateEffect(s, fx, keyIndex);
            foreach (var td in fx.Diff.Tables)
            {
                if (!s.TableWrites.TryGetValue(td.QualifiedName, out var ev)) s.TableWrites[td.QualifiedName] = ev = new Evidence();
                ev.Add(fx.Source);
            }
        }
        foreach (var st in sqls)
        {
            s.SqlSources.Add(st.Source);
            FieldMapper.AccumulateSql(s, session, st);
        }
    }

    private static void AccumulateScreens(LearnState s, List<UiEvent> session)
    {
        (string name, DateTime t)? lastMenu = null;
        var echoes = LogicLearner.Echoes(session); // the program's text in a box a code was just typed in is not typing
        foreach (var e in session)
        {
            var key = Titles.Key(e.Win);
            if (e.Act is Acts.Invoke or Acts.Select && e.Ct == "MenuItem" && !string.IsNullOrEmpty(e.Name)) lastMenu = (Titles.Name(e.Name), e.T);
            if (key.Length == 0) continue;
            if (!s.Screens.TryGetValue(key, out var si)) s.Screens[key] = si = new ScreenInfo { Title = key, File = "(no screen file)" };
            if (e.Act == Acts.Open || e.Act == Acts.Foreground) si.Visits++;
            if (e.Act == Acts.Open) si.Opens++;
            if (e.Act == Acts.Dialog && (Titles.Key(e.Name) == key || e.Ct == "Window")) si.Dialogs++;
            if (e.Act is Acts.Open or Acts.Dialog && lastMenu is not null && (e.T - lastMenu.Value.t).TotalSeconds <= 5 && Titles.Key(e.Name) == key)
                si.MenuPaths[lastMenu.Value.name] = si.MenuPaths.GetValueOrDefault(lastMenu.Value.name) + 1;
            if (e.Act is Acts.Value or Acts.Select && !string.IsNullOrEmpty(e.Val) && e.By != ValueBy.App && !echoes.Contains(e))
            {
                var f = e.FieldKey;
                if (!si.FieldValues.TryGetValue(f, out var vals)) si.FieldValues[f] = vals = new List<string>();
                if (vals.Count < 10 && !vals.Contains(e.Val)) vals.Add(e.Val);
                if (!si.Fields.Contains(f)) si.Fields.Add(f);
                si.FieldTyped[f] = si.FieldTyped.GetValueOrDefault(f) + 1;
                if (!si.FieldShapes.TryGetValue(f, out var shapes)) si.FieldShapes[f] = shapes = new List<string>();
                var shape = Masking.Shape(e.Val);
                if (shapes.Count < 6 && !shapes.Contains(shape)) shapes.Add(shape);
                if (!si.FieldPatterns.TryGetValue(f, out var pats)) si.FieldPatterns[f] = pats = new Dictionary<string, int>();
                var pat = ValuePattern.Of(e.Val);
                pats[pat] = pats.GetValueOrDefault(pat) + 1;
            }
            if (e.Act == Acts.Invoke && !string.IsNullOrEmpty(e.Name))
            {
                // the action's name masked like a title: a tab or button named after a record never reaches screens.md
                var action = Titles.Name(e.FieldKey);
                if (action.Length > 0 && !si.Actions.Contains(action)) si.Actions.Add(action);
            }
            if (e.Act == Acts.Dropdown)
            {
                var f = e.FieldKey;
                si.FieldLookups[f] = si.FieldLookups.GetValueOrDefault(f) + 1;
                var n = e.Items?.Count ?? 0;
                if (n > si.LookupSizes.GetValueOrDefault(f)) si.LookupSizes[f] = n;
                if (!si.LookupShapes.TryGetValue(f, out var shapes)) si.LookupShapes[f] = shapes = new List<string>();
                foreach (var item in (e.Items ?? new()).Take(5)) { var sh = Masking.Shape(item); if (shapes.Count < 6 && !shapes.Contains(sh)) shapes.Add(sh); }
                if (!si.Fields.Contains(f)) si.Fields.Add(f);
            }
            if (e.Act == Acts.Result && !string.IsNullOrEmpty(e.Name))
            {
                var items = e.Items ?? new();
                var columns = items.Count > 0 ? items[0].Split(" | ").Select(c => c.Trim()).Where(c => c.Length > 0).ToList() : new List<string>();
                var rows = Math.Max(0, items.Count - 1);
                var rkey = e.Name + (string.IsNullOrEmpty(e.Val) ? "" : " › " + e.Val);
                if (!si.ActionResults.TryGetValue(rkey, out var ri)) si.ActionResults[rkey] = ri = new ResultInfo { Grid = e.Val ?? "" };
                ri.Seen++;
                if (columns.Count > ri.Columns.Count) ri.Columns = columns;
                ri.MinRows = Math.Min(ri.MinRows, rows); ri.MaxRows = Math.Max(ri.MaxRows, rows);
                if (rows == 0) ri.EmptySeen++;
            }
        }
    }

    private static LearnResult Finalize(LearnState s, LearnInput input, HashSet<string> sessionWindows, HashSet<string>? dialogWindows = null)
    {
        dialogWindows ??= DialogWindows(input);
        var r = new LearnResult { AppName = input.AppName, EventCount = s.EventCount, EffectCount = s.EffectsTotal, SessionCount = s.Sessions, FirstEvent = s.FirstEvent, LastEvent = s.LastEvent };
        r.Backends = input.Backends; r.Toolkit = input.Toolkit; r.Profile = input.Profile; r.Collector = input.Collector;
        r.HasDataSources = input.HasDataSources;
        if (input.Latest is not null) r.Sources = input.Latest.Databases.Keys.Concat(input.Latest.Files.Keys.Select(Path.GetDirectoryName).Where(d => d is not null).Select(d => d!).Distinct()).OrderBy(x => x).ToList();

        var (flows, nav, main) = FlowLearner.Finalize(s, sessionWindows);
        r.Flows = flows; r.Navigation = nav; r.MainWindow = main;
        r.Tables = TypeInference.Describe(input.Latest, s.TableWrites);
        var (mappings, writes) = FieldMapper.Finalize(s, r.Tables.Select(t => t.Qualified));
        r.FieldMappings = mappings; r.ScreenWrites = writes;
        r.Relations = RelationFinder.Finalize(s, input.Latest);
        r.Formulas = FormulaFitter.Fit(input.Latest, s.Untyped, r.Relations);
        r.Rules = ValidationRuleLearner.Finalize(s);
        r.StatusFlows = StatusFlowLearner.Finalize(s, input.Latest);
        r.Sequences = SequenceLearner.Finalize(s, input.Latest);
        r.SqlWrites = s.SqlWrites.Values.OrderBy(w => w.Table).ThenBy(w => w.Kind).ToList();
        r.Screens = DescribeScreens(s, input, sessionWindows, dialogWindows, nav, r);
        r.Modules = BuildModules(r);
        r.Logic = LogicLearner.Finalize(s, input, r);
        r.Health = Health(s, r, input);
        r.Expectations = Expectations(input, r);

        if (input.Latest is null) r.Warnings.Add("No data snapshot yet: nothing is known about tables. Check the data sources in Settings.");
        if (s.EffectsTotal == 0) r.Warnings.Add("No effects captured yet: no save-type action has been seen while a data source was configured.");
        if (s.EventCount == 0) r.Warnings.Add("No events recorded yet.");
        return r;
    }

    private static List<ScreenInfo> DescribeScreens(LearnState s, LearnInput input, HashSet<string> sessionWindows, HashSet<string> dialogWindows, List<NavEdge> nav, LearnResult r)
    {
        var menuWindows = MenuWindows(input);
        var byKey = new Dictionary<string, ScreenInfo>();
        foreach (var si in s.Screens.Values)
            byKey[si.Title] = new ScreenInfo
            {
                Title = si.Title, File = si.File, Visits = si.Visits, Opens = si.Opens, Dialogs = si.Dialogs,
                Fields = si.Fields.ToList(), Actions = si.Actions.ToList(),
                FieldValues = si.FieldValues.ToDictionary(k => k.Key, k => k.Value.ToList()),
                FieldTyped = new Dictionary<string, int>(si.FieldTyped), FieldShapes = si.FieldShapes.ToDictionary(k => k.Key, k => k.Value.ToList()),
                FieldPatterns = si.FieldPatterns.ToDictionary(k => k.Key, k => new Dictionary<string, int>(k.Value)),
                MenuPaths = new Dictionary<string, int>(si.MenuPaths),
                FieldLookups = new Dictionary<string, int>(si.FieldLookups), LookupSizes = new Dictionary<string, int>(si.LookupSizes),
                LookupShapes = si.LookupShapes.ToDictionary(k => k.Key, k => k.Value.ToList()),
                ActionResults = si.ActionResults.ToDictionary(k => k.Key, k => new ResultInfo { Grid = k.Value.Grid, Columns = k.Value.Columns.ToList(), Seen = k.Value.Seen, MinRows = k.Value.MinRows, MaxRows = k.Value.MaxRows, EmptySeen = k.Value.EmptySeen }),
            };
        foreach (var rec in input.Screens)
        {
            var key = Titles.Key(rec.Title);
            if (!byKey.TryGetValue(key, out var si)) byKey[key] = si = new ScreenInfo { Title = key };
            if (si.File == "(no screen file)" || si.File.Length == 0) si.File = "screens/" + (rec.SourceFile ?? rec.FileName);
            si.ControlCount = Math.Max(si.ControlCount, rec.Controls.Count);
            var logical = rec.LogicalFields();
            si.LogicalFieldCount = Math.Max(si.LogicalFieldCount, logical.Count);
            foreach (var (row, label) in logical)
            {
                var name = label.Length > 0 ? label : row.AutomationId;
                if (name.Length > 0 && !si.Fields.Contains(name)) si.Fields.Add(name);
                // events name the field by its Name or cleaned id; make the two spellings one
                var alt = row.Name.Length > 0 ? row.Name : Labels.Clean(row.AutomationId);
                if (alt.Length > 0 && alt != name && si.Fields.Contains(alt)) MergeField(si, alt, name);
            }
            foreach (var a in rec.Actions)
            {
                var action = Titles.Name(a.Name); // a tab item named after a record (Customer - # "…") is masked like its title
                if (action.Length == 0) continue;
                if (!si.Actions.Contains(action)) si.Actions.Add(action);
                if (a.Shortcut.Length > 0) si.Shortcuts[action] = a.Shortcut;
            }
            if (rec.Kind is ScreenKinds.Dialog or ScreenKinds.Session or ScreenKinds.Tab) si.Kind = rec.Kind;
            if (Titles.Name(rec.MenuPath) is { Length: > 0 } menuPath && si.MenuPaths.Count == 0) si.MenuPaths[menuPath] = 1;
            if (rec.ReachedFrom is not null && si.ReachedFrom is null) si.ReachedFrom = Titles.Key(rec.ReachedFrom);
        }
        foreach (var si in byKey.Values)
        {
            if (si.Dialogs > 0 && si.Opens == 0) si.Kind = ScreenKinds.Dialog;
            else if (dialogWindows.Contains(si.Title) && si.Kind != ScreenKinds.Tab) si.Kind = ScreenKinds.Dialog;
            else if (menuWindows.Contains(si.Title)) si.Kind = ScreenKinds.Menu;
            else if (sessionWindows.Contains(si.Title) && si.Kind == ScreenKinds.Screen) si.Kind = ScreenKinds.Session;
            else if (si.Title.Contains(" › ") && si.Kind == ScreenKinds.Screen) si.Kind = ScreenKinds.Tab;
        }
        foreach (var si in byKey.Values)
        {
            var pred = nav.Where(e => e.To == si.Title && e.From != si.Title && byKey.TryGetValue(e.From, out var p) && p.Kind != ScreenKinds.Dialog).OrderByDescending(e => e.Count).ThenBy(e => e.From).FirstOrDefault();
            if (pred is not null) { si.ReachedFrom = pred.From; si.ReachedFromCount = pred.Count; }
            if (si.MenuPaths.Count > 0) si.MenuPath = si.MenuPaths.OrderByDescending(m => m.Value).ThenBy(m => m.Key).First().Key;
            foreach (var (field, pats) in si.FieldPatterns) si.FieldTypes[field] = InferFieldType(pats);
            si.NeverUsed = si.Fields.Where(f => !si.FieldTyped.ContainsKey(f)).ToList();
        }
        foreach (var d in byKey.Values.Where(x => x.Kind == ScreenKinds.Dialog)) r.MessageWindows[d.Title] = Math.Max(d.Dialogs, d.Visits);
        return byKey.Values.Where(x => x.Kind is not (ScreenKinds.Dialog or ScreenKinds.Menu)).OrderBy(x => x.Title).ToList();
    }

    private static void MergeField(ScreenInfo si, string from, string to)
    {
        si.Fields.Remove(from);
        if (si.FieldTyped.Remove(from, out var n)) si.FieldTyped[to] = si.FieldTyped.GetValueOrDefault(to) + n;
        if (si.FieldValues.Remove(from, out var vals)) { if (!si.FieldValues.TryGetValue(to, out var tv)) si.FieldValues[to] = vals; else foreach (var v in vals) if (tv.Count < 10 && !tv.Contains(v)) tv.Add(v); }
        if (si.FieldShapes.Remove(from, out var shapes)) { if (!si.FieldShapes.TryGetValue(to, out var ts)) si.FieldShapes[to] = shapes; else foreach (var v in shapes) if (ts.Count < 6 && !ts.Contains(v)) ts.Add(v); }
        if (si.FieldPatterns.Remove(from, out var pats)) { if (!si.FieldPatterns.TryGetValue(to, out var tp)) si.FieldPatterns[to] = pats; else foreach (var (k, v) in pats) tp[k] = tp.GetValueOrDefault(k) + v; }
    }

    /// <summary>date | money | integer | code | text, from the patterns of the values typed into the field.</summary>
    public static string InferFieldType(Dictionary<string, int> patterns)
    {
        var total = patterns.Values.Sum();
        if (total == 0) return "unknown";
        double Share(params string[] keys) => (double)keys.Sum(k => patterns.GetValueOrDefault(k)) / total;
        if (Share("date") >= 0.8) return "date";
        if (Share("decimal", "negative number") >= 0.8) return "money/decimal";
        if (Share("digits") >= 0.8) return "integer";
        if (Share("digits", "decimal", "negative number") >= 0.8) return "number";
        if (Share("mixed") >= 0.6 && Share("long text (>50)") == 0) return "code";
        if (Share("letters", "mixed", "long text (>50)") >= 0.8) return "text";
        return "mixed";
    }

    /// <summary>Screens that write the same tables belong to one module (union-find over the screen/table graph).</summary>
    private static List<Module> BuildModules(LearnResult r)
    {
        var parent = new Dictionary<string, string>();
        string Find(string x) { if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; } if (p == x) return x; var root = Find(p); parent[x] = root; return root; }
        void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach (var w in r.ScreenWrites) Union("S:" + w.Screen, "T:" + w.Table);
        var groups = parent.Keys.OrderBy(k => k).GroupBy(Find).ToList();
        var modules = new List<Module>();
        foreach (var g in groups)
        {
            var screens = g.Where(x => x.StartsWith("S:")).Select(x => x[2..]).OrderBy(x => x).ToList();
            var tables = g.Where(x => x.StartsWith("T:")).Select(x => x[2..]).OrderBy(x => x).ToList();
            var topTable = r.ScreenWrites.Where(w => tables.Contains(w.Table)).GroupBy(w => w.Table).OrderByDescending(x => x.Sum(w => w.Count + w.SqlCount)).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault();
            var name = topTable is null ? screens.FirstOrDefault() ?? "module" : topTable[(topTable.IndexOf(" / ") + 3)..];
            modules.Add(new Module { Name = name, Screens = screens, Tables = tables });
        }
        var writtenTables = new HashSet<string>(modules.SelectMany(m => m.Tables));
        var rest = r.Tables.Select(t => t.Qualified).Where(q => !writtenTables.Contains(q)).ToList();
        if (rest.Count > 0) modules.Add(new Module { Name = "not yet seen written", Tables = rest });
        var noWrites = r.Screens.Where(sc => sc.Kind != ScreenKinds.Session).Select(sc => sc.Title).Where(k => !r.ScreenWrites.Any(w => w.Screen == k)).Distinct().OrderBy(x => x).ToList();
        if (noWrites.Count > 0) modules.Add(new Module { Name = "screens without data writes seen", Screens = noWrites });
        return modules.OrderByDescending(m => m.Screens.Count + m.Tables.Count).ThenBy(m => m.Name).ToList();
    }

    private static HealthMetrics Health(LearnState s, LearnResult r, LearnInput input)
    {
        var h = new HealthMetrics
        {
            Days = s.EventsPerDay.Count, TriggerInvokes = s.TriggerInvokes, EffectsTotal = s.EffectsTotal, EffectsWithDiff = s.EffectsWithDiff,
            PressesSeen = s.PressesSeen, PressesInferred = s.PressesInferred, DialogsSeen = s.DialogsSeen, UsersSeen = s.Users.Count, MachinesSeen = s.Machines.Count,
            DiskUse = input.DiskUse, TabSelections = s.TabSelections,
        };
        // message boxes the collector recorded as screens (an older collector, or a shape it did not know): by title or by shape, visited 5+ times
        h.PromptScreens = input.Screens.Where(rec => rec.Kind == ScreenKinds.Screen && (DialogDetector.LooksLikePromptTitle(rec.Title) || DialogDetector.Detect("", rec.Controls).isDialog))
                .Select(rec => Titles.Key(rec.Title)).Distinct().Count(k => s.Screens.TryGetValue(k, out var si) && si.Visits >= 5);
        // documents the user opened (named by the events or by the collector) that have no screen record of their own
        var recorded = new HashSet<string>(input.Screens.Select(rec => Titles.Key(rec.Title)), StringComparer.Ordinal);
        var never = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in s.DocumentsSeen) if (!recorded.Contains(d)) never.Add(d);
        if (input.Collector is not null)
            foreach (var d in input.Collector.DocumentsNeverRecorded)
            {
                var key = Titles.Key(d);
                if (key.Length == 0 || MdiWorkspace.IsClassLikeName(d)) continue;
                // the collector names the document alone ("Sales Order Entry"); the screens are named "Main › Sales Order Entry"
                if (!key.Contains(" › ", StringComparison.Ordinal) && r.MainWindow is { Length: > 0 } mw) key = TabDocuments.Title(mw, key);
                if (recorded.Contains(key) || recorded.Any(k => k.EndsWith(" › " + key, StringComparison.Ordinal))) continue;
                // a document renamed when its record loaded ("Vendor Master" became "Vendor - # "…""): the events show the first name as a
                // tab selection and another document of the same window within 15 s; that one's record is this document's
                if (RenamedTo(input.Events, key, recorded)) continue;
                never.Add(key);
            }
        h.DocumentsNeverRecorded = never.ToList();
        // the user moved between fields of a screen (five focus moves or more) and not one typed value was recorded there
        h.TypedButUnrecorded = s.FieldFocuses.Where(f => f.Value >= 5 && s.TypedValues.GetValueOrDefault(f.Key) == 0).Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        h.EventsPerDay = h.Days == 0 ? 0 : (double)s.EventCount / h.Days;
        var saves = Math.Max(s.TriggerInvokes, s.EffectsTotal);
        h.SavesWithDiffPct = saves == 0 ? 0 : 100.0 * s.EffectsWithDiff / saves;
        var fieldsSeen = r.Screens.SelectMany(sc => sc.FieldValues.Keys.Select(f => sc.Title + "\u0001" + f)).Distinct().ToList();
        var mappedKeys = r.FieldMappings.Select(m => m.Screen + "\u0001" + m.Field).Distinct().ToHashSet();
        h.FieldsSeen = fieldsSeen.Count;
        h.FieldsMapped = fieldsSeen.Count(f => mappedKeys.Contains(f) && r.FieldMappings.Any(m => m.Screen + "\u0001" + m.Field == f && m.IsSeen));
        h.FieldsCandidate = fieldsSeen.Count(f => mappedKeys.Contains(f)) - h.FieldsMapped;
        h.FieldsMappedPct = h.FieldsSeen == 0 ? 0 : 100.0 * h.FieldsMapped / h.FieldsSeen;
        var unmapped = h.FieldsSeen - h.FieldsMapped;
        if (input.Collector is not null) h.CollectorProblems = input.Collector.Problems().Where(p => !p.Contains("never recorded as a screen")).ToList(); // the learner's own list of never-recorded documents (below) replaces the collector's, which can name a document under its first title
        if (!input.HasDataSources)
        {
            var server = input.PrimaryBackend();
            var services = input.Backends.Where(b => b.IsService).Select(b => b.Host).Where(x => x is not null).Distinct().ToList();
            h.Advice.Add(server is null
                ? "No data source is configured, so nothing the app saves can be learned. Add its database or data folder in Settings > Data." + (services.Count > 0 ? $" (The hosts in its log files, {string.Join(", ", services)}, are web services it talks to, not its data server.)" : "")
                : $"No data source is configured. Discovery found a {server.Kind} backend{(server.Host is null ? "" : $" at {server.Host}")}{(server.Database is null ? "" : $" (account {server.Database})")}: ask the vendor or DBA for a read-only login (Settings > Data > Add ODBC or SQL Server connection), or point Settings > Data > Export drop folder at nightly CSV exports of the key tables.");
        }
        else if (s.EffectsTotal == 0 && s.TriggerInvokes > 0) h.Advice.Add("Save-type buttons were pressed but no data change was captured: the configured data sources may not be the ones the app writes. Use Settings > Data > Detect by watching file access.");
        if (s.EventCount > 50 && s.PressesSeen + s.PressesInferred == 0) h.Advice.Add("No button or menu press was recorded. The app's controls do not report presses to UI Automation; the MSAA and press-inference fallbacks are on, so check collector-status.json after the next session.");
        if (unmapped > 0 && !input.SqlCaptureEnabled) h.Advice.Add($"SQL capture is off; turning it on (Settings > Data > SQL statement log, scripts/sql/*.sql) would map the {unmapped} unmapped field{(unmapped == 1 ? "" : "s")} exactly, if the app uses a SQL database.");
        if (h.FieldsCandidate > 0) h.Advice.Add($"{h.FieldsCandidate} field mapping{(h.FieldsCandidate == 1 ? " is a candidate" : "s are candidates")} (fewer than 3 distinct values seen); more days of normal use will confirm or drop them.");
        if (h.Days < 5) h.Advice.Add($"Only {h.Days} day{(h.Days == 1 ? "" : "s")} of evidence; flows and rules get reliable after a week of normal work.");
        if (h.UsersSeen <= 1 && h.Days >= 5) h.Advice.Add("Only one user has been watched; install App Watcher on another PC and merge its folder (AppWatcher.exe --merge) to see flows per role.");
        foreach (var d in h.DocumentsNeverRecorded.Take(12)) h.CollectorProblems.Add($"the document {d} was opened but never recorded as a screen (its control tree could not be read)");
        foreach (var scr in h.TypedButUnrecorded.Take(12)) h.CollectorProblems.Add($"on {scr} the user moved between fields (typed there) but no typed value was recorded: its fields report no change");
        if (h.Advice.Count == 0) h.Advice.Add("Nothing obvious would improve accuracy right now; keep watching.");
        return h;
    }

    /// <summary>True when the events show the document <paramref name="key"/> ("Main › Vendor Master") selected and, within 15 s, another document of the same window that has a record.</summary>
    private static bool RenamedTo(List<UiEvent> events, string key, HashSet<string> recorded)
    {
        var sep = key.LastIndexOf(" › ", StringComparison.Ordinal);
        if (sep < 0) return false;
        var main = key[..sep]; var doc = key[(sep + 3)..];
        foreach (var e in events)
        {
            if (!(e.Act == Acts.Select && e.Ct == "TabItem" && Titles.Key(e.Name) == doc) && !(e.Act == Acts.Open && Titles.Key(e.Win) == key)) continue;
            foreach (var later in events)
            {
                if (later.T < e.T || (later.T - e.T).TotalSeconds > 15 || later.Act is not (Acts.Open or Acts.Select)) continue;
                var w = Titles.Key(later.Win);
                if (w.StartsWith(main + " › ", StringComparison.Ordinal) && w != key && recorded.Contains(w)) return true;
            }
        }
        return false;
    }

    /// <summary>Plain-words statement of what can be learned from this app, from the toolkit, the backends and the data sources.</summary>
    public static List<string> Expectations(LearnInput input, LearnResult? r = null)
    {
        var list = new List<string>();
        var screens = r?.Screens.Count ?? 0;
        var fieldsOk = input.Collector is null || input.Collector.DumpsOk > 0 || input.Collector.WindowsSeen == 0;
        list.Add("Screens and flows: yes (window titles, order and timing are always recorded).");
        list.Add(fieldsOk
            ? $"Fields and buttons: yes{(input.Toolkit is null ? "" : $" ({input.Toolkit} controls are exposed to UI Automation)")}."
            : "Fields and buttons: no, this app does not expose its screens to UI Automation; the MSAA fallback gives names only.");
        var readable = input.HasDataSources;
        var server = input.PrimaryBackend();
        var services = input.Backends.Where(b => b.IsService).Select(b => b.Host).Where(x => x is not null).Distinct().ToList();
        // a source counts as readable for tables only once rows were actually read; a plain file (not a database) is only fingerprinted
        var tablesRead = input.Latest is { } snap && snap.Databases.Values.Any(t => t.Count > 0);
        var filesOnly = input.Latest is { } snap2 && !snap2.Databases.Values.Any(t => t.Count > 0) && snap2.Files.Count > 0;
        if (readable && filesOnly) list.Add("Tables and rules: partly. I see when its data files change, but they are not a database I can read row by row, so I cannot tell which fields fill which columns. A read-only database login or an export drop folder (CSV or Excel) would give the tables.");
        else if (readable) list.Add(tablesRead
            ? "Tables and rules: yes, a data source is configured and read before and after each save."
            : "Tables and rules: yes once its data has been read; a data source is configured and is read before and after each save.");
        else if (server is not null) list.Add($"Tables and rules: not yet. The data is on a {server.Kind} server{(server.Host is null ? "" : $" ({server.Host}{(server.Port is null ? "" : ":" + server.Port)}{(server.Database is null ? "" : ", account " + server.Database)})")} this PC cannot read. Options: a read-only login (ODBC or SQL Server), a nightly export drop folder, or SQL statement capture by the DBA.");
        else list.Add("Tables and rules: not yet, no data source was found. Options: Detect by watching file access, add the database or folder by hand, or an export drop folder." + (services.Count > 0 ? $" (The program talks to web services at {string.Join(", ", services)}; those are not its data server.)" : ""));
        if (input.Profile is not null) list.Add($"Profile: {input.Profile}.");
        return list;
    }
}
