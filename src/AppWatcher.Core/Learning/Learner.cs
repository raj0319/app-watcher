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

    public static LearnInput LoadInput(AppPaths paths, string appName, WatcherConfig? config = null, DateTime? since = null) => new()
    {
        AppName = appName,
        Events = EventReader.ReadAll(paths.Events, since),
        Effects = EffectRecord.ReadAll(paths.Effects),
        Latest = DataSnapshot.Load(paths.BeforeSnapshot),
        Screens = ScreenWriter.ReadAll(paths.Screens),
        SqlStatements = config?.SqlCapture.Enabled == true ? SqlLogReaders.ReadAll(config.SqlCapture.LogPath, config.App?.ProcessName) : new(),
        Triggers = config?.EffectTriggers ?? new() { "save", "post", "ok", "apply", "submit", "print", "delete", "send" },
        SqlCaptureEnabled = config?.SqlCapture.Enabled == true,
        HasDataSources = config is null || config.DataSources.Any(d => d.Enabled),
    };

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
        var wasIncremental = state.Watermark is not null;
        var keyIndex = RelationFinder.BuildKeyIndex(input.Latest);
        var triggers = BuildTriggerRegex(input.Triggers);

        var events = input.Events.Where(e => state.Watermark is null || e.T > state.Watermark.Value).OrderBy(e => e.T).ToList();
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
            ProcessSession(working, session, fx, sq, keyIndex, triggers);
            if (closed && !openSeen) state.Watermark = end;
        }
        // effects and statements outside any session (idle file changes, logs without timestamps)
        foreach (var f in effects.Where(f => !usedEffects.Contains(f)))
        {
            if (f.T > cutoff) Open();
            ProcessSession(working, new List<UiEvent>(), new List<EffectRecord> { f }, new List<SqlStatement>(), keyIndex, triggers, countSession: false);
        }
        foreach (var s in sqls.Where(s => !usedSql.Contains(s)))
        {
            if (s.T > cutoff) Open();
            ProcessSession(working, new List<UiEvent>(), new List<EffectRecord>(), new List<SqlStatement> { s }, keyIndex, triggers, countSession: false);
        }

        committed = state;
        var r = Finalize(working, input);
        r.Incremental = wasIncremental;
        return r;
    }

    public static Regex BuildTriggerRegex(IEnumerable<string> words)
    {
        var alt = string.Join("|", words.Select(w => w.Trim()).Where(w => w.Length > 0).Select(Regex.Escape));
        return new Regex(alt.Length == 0 ? "(?!)" : $@"(?<![a-z]){alt}(?![a-z])", RegexOptions.IgnoreCase);
    }

    private static void ProcessSession(LearnState s, List<UiEvent> session, List<EffectRecord> effects, List<SqlStatement> sqls,
        Dictionary<string, HashSet<(string table, string col)>> keyIndex, Regex triggers, bool countSession = true)
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
            FlowLearner.Accumulate(s, session);
            ValidationRuleLearner.Accumulate(s, session);
            AccumulateScreens(s, session);
        }
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
        foreach (var e in session)
        {
            var key = Titles.Key(e.Win);
            if (key.Length == 0) continue;
            if (!s.Screens.TryGetValue(key, out var si)) s.Screens[key] = si = new ScreenInfo { Title = key, File = "(no screen file)" };
            if (e.Act == Acts.Open || e.Act == Acts.Foreground) si.Visits++;
            if (e.Act is Acts.Value or Acts.Select && !string.IsNullOrEmpty(e.Val))
            {
                if (!si.FieldValues.TryGetValue(e.FieldKey, out var vals)) si.FieldValues[e.FieldKey] = vals = new List<string>();
                if (vals.Count < 10 && !vals.Contains(e.Val)) vals.Add(e.Val);
                if (!si.Fields.Contains(e.FieldKey)) si.Fields.Add(e.FieldKey);
            }
            if (e.Act == Acts.Invoke && !string.IsNullOrEmpty(e.Name) && !si.Actions.Contains(e.Name)) si.Actions.Add(e.Name);
        }
    }

    private static LearnResult Finalize(LearnState s, LearnInput input)
    {
        var r = new LearnResult { AppName = input.AppName, EventCount = s.EventCount, EffectCount = s.EffectsTotal, SessionCount = s.Sessions, FirstEvent = s.FirstEvent, LastEvent = s.LastEvent };
        if (input.Latest is not null) r.Sources = input.Latest.Databases.Keys.Concat(input.Latest.Files.Keys.Select(Path.GetDirectoryName).Where(d => d is not null).Select(d => d!).Distinct()).OrderBy(x => x).ToList();

        var (flows, nav, main) = FlowLearner.Finalize(s);
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
        r.Screens = DescribeScreens(s, input);
        r.Modules = BuildModules(r);
        r.Health = Health(s, r, input);

        if (input.Latest is null) r.Warnings.Add("No data snapshot yet: nothing is known about tables. Check the data sources in Settings.");
        if (s.EffectsTotal == 0) r.Warnings.Add("No effects captured yet: no save-type action has been seen while a data source was configured.");
        if (s.EventCount == 0) r.Warnings.Add("No events recorded yet.");
        return r;
    }

    private static List<ScreenInfo> DescribeScreens(LearnState s, LearnInput input)
    {
        var byKey = new Dictionary<string, ScreenInfo>();
        foreach (var si in s.Screens.Values)
            byKey[si.Title] = new ScreenInfo { Title = si.Title, File = si.File, Visits = si.Visits, Fields = si.Fields.ToList(), Actions = si.Actions.ToList(), FieldValues = si.FieldValues.ToDictionary(k => k.Key, k => k.Value.ToList()) };
        foreach (var rec in input.Screens)
        {
            var key = Titles.Key(rec.Title);
            if (!byKey.TryGetValue(key, out var si)) byKey[key] = si = new ScreenInfo { Title = key };
            if (si.File == "(no screen file)" || si.File.Length == 0) si.File = "screens/" + rec.FileName;
            si.ControlCount = Math.Max(si.ControlCount, rec.Controls.Count);
            foreach (var f in rec.Fields.Select(f => f.Name.Length > 0 ? f.Name : f.AutomationId).Where(n => n.Length > 0))
                if (!si.Fields.Contains(f)) si.Fields.Add(f);
            foreach (var a in rec.Actions.Select(a => a.Name)) if (!si.Actions.Contains(a)) si.Actions.Add(a);
        }
        return byKey.Values.OrderBy(x => x.Title).ToList();
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
        var noWrites = r.Screens.Select(sc => sc.Title).Where(k => !r.ScreenWrites.Any(w => w.Screen == k)).Distinct().OrderBy(x => x).ToList();
        if (noWrites.Count > 0) modules.Add(new Module { Name = "screens without data writes seen", Screens = noWrites });
        return modules.OrderByDescending(m => m.Screens.Count + m.Tables.Count).ThenBy(m => m.Name).ToList();
    }

    private static HealthMetrics Health(LearnState s, LearnResult r, LearnInput input)
    {
        var h = new HealthMetrics { Days = s.EventsPerDay.Count, TriggerInvokes = s.TriggerInvokes, EffectsTotal = s.EffectsTotal, EffectsWithDiff = s.EffectsWithDiff };
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
        if (!input.HasDataSources) h.Advice.Add("No data source is configured, so nothing the app saves can be learned. Add its database or data folder in Settings > Data.");
        else if (s.EffectsTotal == 0 && s.TriggerInvokes > 0) h.Advice.Add("Save-type buttons were pressed but no data change was captured: the configured data sources may not be the ones the app writes. Use Settings > Data > Detect by watching file access.");
        if (unmapped > 0 && !input.SqlCaptureEnabled) h.Advice.Add($"SQL capture is off; turning it on (Settings > Data > SQL statement log, scripts/sql/xevents-session.sql) would map the {unmapped} unmapped field{(unmapped == 1 ? "" : "s")} exactly, if the app uses SQL Server.");
        if (h.FieldsCandidate > 0) h.Advice.Add($"{h.FieldsCandidate} field mapping{(h.FieldsCandidate == 1 ? " is a candidate" : "s are candidates")} (fewer than 3 distinct values seen); more days of normal use will confirm or drop them.");
        if (h.Days < 5) h.Advice.Add($"Only {h.Days} day{(h.Days == 1 ? "" : "s")} of evidence; flows and rules get reliable after a week of normal work.");
        if (h.Advice.Count == 0) h.Advice.Add("Nothing obvious would improve accuracy right now; keep watching.");
        return h;
    }
}
