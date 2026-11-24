using System.Text;
using System.Text.Json;
using AppWatcher.Core;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Tests;

/// <summary>
/// Synthetic office for the scale tests: N users, one PC (data folder) each, working days of ~8 hours, every user opening
/// screens from the main window, typing, looking things up, saving, with the effect files and screen records the collector
/// would have written. Only invented names and values; deterministic for a given seed.
/// </summary>
internal sealed class OfficeGenerator
{
    public sealed record ScreenDef(string Title, string Table, string[] Fields, int[] Kinds, bool HasTotal)
    {
        public string Module => Title.Split(' ')[0];
    }

    public int Users { get; init; } = 5;
    public int Days { get; init; } = 30;
    /// <summary>Target events per user and day.</summary>
    public int EventsPerDay { get; init; } = 3000;
    public int Seed { get; init; } = 17;
    public DateTime FirstDay { get; init; } = new(2026, 9, 1);
    public const string MainTitle = "Harbor ERP";

    private static readonly string[] Adj = { "Net", "Gross", "Unit", "Base", "Open", "Final", "Prior", "Local", "Remote", "Extra", "Mean", "Total", "Early", "Late", "Daily", "Annual", "Primary", "Backup", "Linked", "Shared" };
    private static readonly string[] Noun = { "Price", "Weight", "Region", "Contact", "Reference", "Limit", "Balance", "Channel", "Category", "Remark", "Route", "Batch", "Agent", "Segment", "Margin" };
    private static readonly string[] Modules = { "Sales", "Buying", "Stock", "Ledger", "Payroll", "Assets", "Service", "Planning" };
    private static readonly string[] Objects = { "Quote", "Order", "Return", "Entry", "Register" };
    private static readonly string[] Status = { "open", "paid", "held", "shipped", "closed", "draft", "void", "review", "queued", "done", "late", "new" };

    public ScreenDef[] Screens { get; }
    public string[] Dialogs { get; } = { "Confirm Save", "Validation Problem", "Print Options" };
    public string[] UserHash { get; }
    public string[] MachineHash { get; }

    public OfficeGenerator()
    {
        var shapes = new List<ScreenDef>();
        var f = 0;
        foreach (var m in Modules)
            foreach (var o in Objects)
            {
                var total = shapes.Count % 4 == 0;
                var n = total ? 5 : 7 + shapes.Count % 2;
                var fields = new List<string>();
                for (var i = 0; i < n; i++) { var k = f++ % (Adj.Length * Noun.Length); fields.Add(Adj[k % Adj.Length] + " " + Noun[k / Adj.Length]); }
                if (total) { fields.Add("Quantity"); fields.Add("Unit Cost"); fields.Add("Line Total"); }
                var kinds = fields.Select((x, i) => i % 5).ToArray(); // 0 text, 1 amount, 2 code, 3 date, 4 list
                if (total) { kinds[^1] = 1; kinds[^2] = 1; kinds[^3] = 1; }
                var title = m + " " + o;
                shapes.Add(new ScreenDef(title, Paths.SafeName(title).Replace('-', '_'), fields.ToArray(), kinds, total));
            }
        Screens = shapes.ToArray();
        UserHash = Enumerable.Range(1, 64).Select(i => Merge.Tag("user-" + i)).ToArray();
        MachineHash = Enumerable.Range(1, 64).Select(i => Merge.Tag("pc-" + i)).ToArray();
    }

    public string FolderFor(string root, int user) => Path.Combine(root, "pc" + (user + 1));

    public IEnumerable<DateTime> WorkDays()
    {
        var d = FirstDay; var n = 0;
        while (n < Days) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) { n++; yield return d; } d = d.AddDays(1); }
    }

    public int FieldCount => Screens.Sum(s => s.Fields.Length);

    /// <summary>Writes one PC's folder: screens, snapshot, and for each requested day the events and effects. Returns the event count.</summary>
    public long WritePc(string root, int user, IEnumerable<DateTime>? days = null, bool structure = true)
    {
        var paths = new AppPaths(FolderFor(root, user));
        paths.EnsureDirectories();
        if (structure) WriteStructure(paths);
        long count = 0;
        var jopts = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        foreach (var day in days ?? WorkDays())
        {
            var rnd = new Random(Seed * 1009 + user * 7919 + day.DayOfYear);
            var evFile = Path.Combine(paths.Events, day.ToString("yyyy-MM-dd") + ".jsonl");
            using var w = new StreamWriter(evFile, false, new UTF8Encoding(false));
            DateTime last = day;
            void Emit(UiEvent e) { e.Dt = Math.Max(0, (long)(e.T - last).TotalMilliseconds); last = e.T; w.WriteLine(JsonSerializer.Serialize(e, jopts)); count++; }
            var t = day.AddHours(8).AddMinutes(rnd.Next(0, 25));
            Emit(new UiEvent { T = t, Act = Acts.Start, Ct = "watcher", Name = "App Watcher started", Val = $"user:{UserHash[user]} machine:{MachineHash[user]} version:0.12.2" });
            var end = day.AddHours(17).AddMinutes(rnd.Next(0, 30));
            var lunch = day.AddHours(12).AddMinutes(rnd.Next(0, 30));
            var lunched = false; var breaks = 0;
            // a visit is ~35 events
            var visits = Math.Max(1, (int)(EventsPerDay * (0.85 + rnd.NextDouble() * 0.3) / 35.0));
            var step = (end - t).TotalSeconds / (visits + 2);
            var fxNo = 0;
            for (var v = 0; v < visits; v++)
            {
                if (!lunched && t >= lunch) { t = t.AddMinutes(45 + rnd.Next(0, 20)); lunched = true; }
                else if (breaks < 2 && rnd.Next(visits) < 2) { t = t.AddMinutes(35 + rnd.Next(0, 15)); breaks++; }
                var sd = Screens[(int)(Math.Pow(rnd.NextDouble(), 1.6) * Screens.Length)];
                t = Visit(paths, rnd, user, sd, t, step, Emit, ref fxNo);
                t = t.AddSeconds(Math.Max(1, step * 0.15 * rnd.NextDouble()));
            }
            Emit(new UiEvent { T = t.AddSeconds(5), Act = Acts.Stop, Ct = "watcher", Name = "App Watcher stopped" });
        }
        return count;
    }

    private static void Ev(Action<UiEvent> emit, DateTime t, string act, string win, string name, string? val = null, string ct = "Edit", string? by = null, string? how = null, string[]? items = null)
        => emit(new UiEvent { T = t, Act = act, Win = win, Name = name, Val = val, Ct = ct, By = by, How = how, Items = items?.ToList() });

    private DateTime Visit(AppPaths paths, Random rnd, int user, ScreenDef sd, DateTime t, double step, Action<UiEvent> emit, ref int fxNo)
    {
        var win = MainTitle + " › " + sd.Title;
        var gap = Math.Clamp(step * 0.8 / 35.0, 0.3, 12);
        DateTime Tick(double mul = 1) => t = t.AddSeconds(gap * mul * (0.5 + rnd.NextDouble()));
        Ev(emit, t, Acts.Foreground, MainTitle, MainTitle, ct: "Window");
        Ev(emit, Tick(), Acts.Menu, MainTitle, sd.Module, ct: "Menu", items: new[] { sd.Title, "Other" });
        Ev(emit, Tick(), Acts.Invoke, MainTitle, "Open " + sd.Title, ct: "MenuItem", how: "menu");
        Ev(emit, Tick(), Acts.Open, win, sd.Title, ct: "Window");
        Ev(emit, Tick(), Acts.Foreground, win, sd.Title, ct: "Window");
        Ev(emit, Tick(), Acts.Select, win, "Details", ct: "TabItem", how: "read");
        var vals = new string[sd.Fields.Length];
        double qty = 0, cost = 0;
        for (var i = 0; i < sd.Fields.Length; i++)
        {
            var name = sd.Fields[i];
            var ct = sd.Kinds[i] == 4 ? "ComboBox" : "Edit";
            Ev(emit, Tick(), Acts.Focus, win, name, ct: ct);
            string v;
            switch (sd.Kinds[i])
            {
                case 0: v = "Zq" + Letters(rnd, 6); break;
                case 1: v = (rnd.Next(1, 90) * 10 + rnd.Next(0, 10)).ToString(); break;
                case 2: v = "K" + rnd.Next(100000, 999999); break;
                case 3: v = $"2026-{rnd.Next(1, 12):00}-{rnd.Next(1, 28):00}"; break;
                default: v = Status[rnd.Next(Status.Length)]; break;
            }
            if (sd.HasTotal && i == sd.Fields.Length - 3) { qty = rnd.Next(1, 40); v = qty.ToString(); }
            if (sd.HasTotal && i == sd.Fields.Length - 2) { cost = rnd.Next(2, 90); v = cost.ToString(); }
            if (sd.HasTotal && i == sd.Fields.Length - 1)
            {
                vals[i] = (qty * cost).ToString();
                Ev(emit, Tick(0.3), Acts.Value, win, name, vals[i], by: ValueBy.App);
                continue;
            }
            vals[i] = v;
            if (sd.Kinds[i] == 4 && rnd.Next(3) == 0)
                Ev(emit, Tick(0.5), Acts.Dropdown, win, name, v[..1], ct: "ComboBox", items: Status.Take(5).ToArray());
            Ev(emit, Tick(1.5), Acts.Value, win, name, v, ct: ct);
            // the program fills a neighbour after a code is typed (a lookup)
            if (sd.Kinds[i] == 2 && i + 1 < sd.Fields.Length && sd.Kinds[i + 1] == 0 && !(sd.HasTotal && i + 1 >= sd.Fields.Length - 3))
            {
                vals[i + 1] = "Zq" + Letters(rnd, 6);
                Ev(emit, Tick(0.3), Acts.Value, win, sd.Fields[i + 1], vals[i + 1], by: ValueBy.App);
                i++;
            }
        }
        if (rnd.Next(5) == 0) Ev(emit, Tick(), Acts.Hint, win, sd.Fields[0], "Tip about " + sd.Fields[0].ToLowerInvariant());
        if (rnd.Next(4) == 0) Ev(emit, Tick(), Acts.Result, win, "Lines", "Lines", ct: "DataGrid", items: new[] { "Item | Qty | Cost", "Widget | 3 | 10" });
        if (rnd.Next(3) == 0) Ev(emit, Tick(), Acts.Select, win, "Notes", ct: "TabItem", how: "read");
        if (rnd.Next(10) == 0)
        {
            var dlg = Dialogs[rnd.Next(Dialogs.Length)];
            Ev(emit, Tick(), Acts.Dialog, win, dlg, "Message text for " + dlg.ToLowerInvariant(), ct: "Window");
            Ev(emit, Tick(), Acts.Invoke, dlg, "OK", ct: "Button", how: "uia");
        }
        if (rnd.Next(100) < 60)
        {
            var saveAt = Tick();
            Ev(emit, saveAt, Acts.Invoke, win, "Save", ct: "Button", how: "uia");
            var row = new Dictionary<string, string?> { ["id"] = (user * 1_000_000 + (++fxNo) + saveAt.DayOfYear * 1000).ToString() };
            for (var i = 0; i < sd.Fields.Length; i++) row[Col(sd.Fields[i])] = vals[i];
            row["status"] = "open";
            var td = new TableDiff { Db = "harbor.db", Table = sd.Table };
            td.Added[row["id"]!] = row;
            var diff = new DiffResult();
            diff.Tables.Add(td);
            var fx = new EffectRecord { T = saveAt.AddMilliseconds(400 + user), Win = win, Reason = "invoke", TriggerName = "Save", TriggerType = "Button", Diff = diff, BeforeTaken = saveAt, AfterTaken = saveAt.AddMilliseconds(400 + user) };
            fx.Save(paths.Effects);
        }
        else Ev(emit, Tick(), Acts.Invoke, win, "Cancel", ct: "Button", how: "uia");
        Ev(emit, Tick(), Acts.Close, win, sd.Title, ct: "Window");
        Ev(emit, Tick(0.5), Acts.Foreground, MainTitle, MainTitle, ct: "Window");
        return t;
    }

    public static string Col(string field) => field.ToLowerInvariant().Replace(' ', '_');

    private static string Letters(Random r, int n) { var c = new char[n]; for (var i = 0; i < n; i++) c[i] = (char)('a' + r.Next(26)); return new string(c); }

    public void WriteStructure(AppPaths paths)
    {
        foreach (var sd in Screens)
        {
            var rows = new List<ControlRow> { new() { Depth = 0, Type = "Window", Name = MainTitle + " › " + sd.Title } };
            for (var i = 0; i < sd.Fields.Length; i++)
                rows.Add(new ControlRow { Depth = 2, Type = sd.Kinds[i] == 4 ? "ComboBox" : "Edit", Name = sd.Fields[i], AutomationId = "txt" + sd.Fields[i].Replace(" ", "") });
            rows.Add(new ControlRow { Depth = 2, Type = "TabItem", Name = "Details" });
            rows.Add(new ControlRow { Depth = 2, Type = "TabItem", Name = "Notes" });
            rows.Add(new ControlRow { Depth = 2, Type = "Button", Name = "Save", AutomationId = "btnSave" });
            rows.Add(new ControlRow { Depth = 2, Type = "Button", Name = "Cancel", AutomationId = "btnCancel" });
            ScreenWriter.WriteOnce(paths.Screens, new ScreenRecord { Title = MainTitle + " › " + sd.Title, Kind = ScreenKinds.Tab, Controls = rows, MenuPath = sd.Module + " > " + sd.Title, ReachedFrom = MainTitle, Source = "uia-cached", Captured = FirstDay });
        }
        foreach (var d in Dialogs)
            ScreenWriter.WriteOnce(paths.Screens, new ScreenRecord { Title = d, Kind = ScreenKinds.Dialog, Controls = new() { new ControlRow { Depth = 0, Type = "Window", Name = d }, new ControlRow { Depth = 1, Type = "Button", Name = "OK" } }, Source = "uia-cached", Captured = FirstDay });
        var snap = new DataSnapshot();
        snap.Databases["harbor.db"] = Screens.ToDictionary(s => s.Table, s =>
        {
            var cols = new[] { "id" }.Concat(s.Fields.Select(Col)).Concat(new[] { "status" }).ToArray();
            return Synthetic.Table(cols, new[] { "id" }, cols.Select(c => c == "id" ? "1" : "x").ToArray());
        });
        snap.Save(paths.BeforeSnapshot);
    }
}
