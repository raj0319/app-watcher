using AppWatcher.Core.Events;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>Runs every heuristic over the evidence folder. Pure: no UI, no network, nothing written to the watched app.</summary>
public static class Learner
{
    public static LearnInput LoadInput(AppPaths paths, string appName) => new()
    {
        AppName = appName,
        Events = EventReader.ReadAll(paths.Events),
        Effects = EffectRecord.ReadAll(paths.Effects),
        Latest = DataSnapshot.Load(paths.BeforeSnapshot),
        Screens = ScreenWriter.ReadAll(paths.Screens),
    };

    public static LearnResult Learn(LearnInput input)
    {
        var r = new LearnResult { AppName = input.AppName, EventCount = input.Events.Count, EffectCount = input.Effects.Count };
        if (input.Events.Count > 0) { r.FirstEvent = input.Events.Min(e => e.T); r.LastEvent = input.Events.Max(e => e.T); }
        if (input.Latest is not null) r.Sources = input.Latest.Databases.Keys.Concat(input.Latest.Files.Keys.Select(Path.GetDirectoryName).Where(d => d is not null).Select(d => d!).Distinct()).ToList();

        var (flows, nav, main, sessions) = FlowLearner.Learn(input.Events);
        r.Flows = flows; r.Navigation = nav; r.MainWindow = main; r.SessionCount = sessions;

        var (mappings, writes) = FieldMapper.Learn(input.Events, input.Effects);
        r.FieldMappings = mappings; r.ScreenWrites = writes;

        r.Relations = RelationFinder.Learn(input.Latest, input.Effects);
        r.Formulas = FormulaFitter.Learn(input.Latest, input.Effects, mappings, r.Relations);
        r.Rules = ValidationRuleLearner.Learn(input.Events);
        r.StatusFlows = StatusFlowLearner.Learn(input.Latest, input.Effects);
        r.Sequences = SequenceLearner.Learn(input.Latest, input.Effects);
        r.Tables = TypeInference.Describe(input.Latest, input.Effects);
        r.Screens = DescribeScreens(input);
        r.Modules = BuildModules(r);

        if (input.Latest is null) r.Warnings.Add("No data snapshot yet: nothing is known about tables. Check the data sources in Settings.");
        if (input.Effects.Count == 0) r.Warnings.Add("No effects captured yet: no save-type action has been seen while a data source was configured.");
        if (input.Events.Count == 0) r.Warnings.Add("No events recorded yet.");
        return r;
    }

    private static List<ScreenInfo> DescribeScreens(LearnInput input)
    {
        var byKey = new Dictionary<string, ScreenInfo>();
        foreach (var s in input.Screens)
        {
            var key = Titles.Key(s.Title);
            if (byKey.ContainsKey(key)) continue;
            byKey[key] = new ScreenInfo
            {
                Title = s.Title, File = "screens/" + s.FileName, ControlCount = s.Controls.Count,
                Fields = s.Fields.Select(f => f.Name.Length > 0 ? f.Name : f.AutomationId).Where(n => n.Length > 0).Distinct().ToList(),
                Actions = s.Actions.Select(a => a.Name).Distinct().ToList(),
            };
        }
        var visits = new HashSet<(string, DateTime)>();
        foreach (var e in input.Events)
        {
            var key = Titles.Key(e.Win);
            if (key.Length == 0) continue;
            if (!byKey.TryGetValue(key, out var si)) byKey[key] = si = new ScreenInfo { Title = e.Win, File = "(no screen file)" };
            if (e.Act == Acts.Open || e.Act == Acts.Foreground) si.Visits++;
            if (e.Act is Acts.Value or Acts.Select && !string.IsNullOrEmpty(e.Val))
            {
                if (!si.FieldValues.TryGetValue(e.FieldKey, out var vals)) si.FieldValues[e.FieldKey] = vals = new List<string>();
                if (vals.Count < 10 && !vals.Contains(e.Val)) vals.Add(e.Val);
                if (!si.Fields.Contains(e.FieldKey)) si.Fields.Add(e.FieldKey);
            }
            if (e.Act == Acts.Invoke && !string.IsNullOrEmpty(e.Name) && !si.Actions.Contains(e.Name)) si.Actions.Add(e.Name);
        }
        return byKey.Values.OrderBy(s => s.Title).ToList();
    }

    /// <summary>Screens that write the same tables belong to one module (union-find over the screen/table graph).</summary>
    private static List<Module> BuildModules(LearnResult r)
    {
        var parent = new Dictionary<string, string>();
        string Find(string x) { if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; } if (p == x) return x; var root = Find(p); parent[x] = root; return root; }
        void Union(string a, string b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach (var w in r.ScreenWrites) Union("S:" + w.Screen, "T:" + w.Table);
        var groups = parent.Keys.GroupBy(Find).ToList();
        var modules = new List<Module>();
        foreach (var g in groups)
        {
            var screens = g.Where(x => x.StartsWith("S:")).Select(x => x[2..]).OrderBy(x => x).ToList();
            var tables = g.Where(x => x.StartsWith("T:")).Select(x => x[2..]).OrderBy(x => x).ToList();
            var topTable = r.ScreenWrites.Where(w => tables.Contains(w.Table)).GroupBy(w => w.Table).OrderByDescending(x => x.Sum(w => w.Count)).Select(x => x.Key).FirstOrDefault();
            var name = topTable is null ? screens.FirstOrDefault() ?? "module" : topTable[(topTable.IndexOf(" / ") + 3)..];
            modules.Add(new Module { Name = name, Screens = screens, Tables = tables });
        }
        var writtenTables = new HashSet<string>(modules.SelectMany(m => m.Tables));
        var rest = r.Tables.Select(t => t.Qualified).Where(q => !writtenTables.Contains(q)).ToList();
        if (rest.Count > 0) modules.Add(new Module { Name = "not yet seen written", Tables = rest });
        var noWrites = r.Screens.Select(s => Titles.Key(s.Title)).Where(k => !r.ScreenWrites.Any(w => w.Screen == k)).Distinct().ToList();
        if (noWrites.Count > 0) modules.Add(new Module { Name = "screens without data writes seen", Screens = noWrites });
        return modules.OrderByDescending(m => m.Screens.Count + m.Tables.Count).ToList();
    }
}
