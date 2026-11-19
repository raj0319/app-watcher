using System.Globalization;
using System.Text.Json.Serialization;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Learning;

/// <summary>"Typing Customer code fills Customer name": a value the program filled in right after something the user did.</summary>
public sealed class FieldLink
{
    public string Screen { get; set; } = "";
    /// <summary>"typed:Customer code", "pressed:Search", "chose:Type", "ticked:Rush", "chose menu:Refresh".</summary>
    public string When { get; set; } = "";
    public string Field { get; set; } = "";
    public int Seen { get; set; }
    /// <summary>How often the cause happened at all, so a link that follows it only now and then is not reported as a rule.</summary>
    public int CauseCount { get; set; }
    public Evidence Evidence { get; set; } = new();
    [JsonIgnore] public string WhenText => LogicLearner.CauseText(When);
    /// <summary>Some of the evidence is the text read from the pictures, not the control tree.</summary>
    [JsonIgnore] public bool FromOcr => Evidence.Files.Any(f => f.EndsWith(".ocr.json", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A field the program calculates on screen, and the formula over the other fields on that screen that keeps fitting.</summary>
public sealed class ScreenCalc
{
    public string Screen { get; set; } = "";
    public string Field { get; set; } = "";
    public string Formula { get; set; } = "";
    public int Fits { get; set; }
    public int Tested { get; set; }
    public bool WithExceptions { get; set; }
    public List<string> Alternatives { get; set; } = new();
    public Evidence Evidence { get; set; } = new();
    [JsonIgnore] public int Exceptions => Tested - Fits;
    /// <summary>Some of the evidence is the text read from the pictures, not the control tree.</summary>
    [JsonIgnore] public bool FromOcr => Evidence.Files.Any(f => f.EndsWith(".ocr.json", StringComparison.OrdinalIgnoreCase));
}

/// <summary>What pressing a button led to: a window or a message.</summary>
public sealed class ButtonLead
{
    public string Screen { get; set; } = "";
    public string Button { get; set; } = "";
    public string Opens { get; set; } = "";
    public bool Message { get; set; }
    public int Seen { get; set; }
    public Evidence Evidence { get; set; } = new();
}

/// <summary>Something the watcher saw on a screen or in a menu that nobody used while it watched.</summary>
public sealed class NotUsedItem
{
    public string Screen { get; set; } = "";
    /// <summary>button | tab | menu item | field</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A question a trainee would ask an experienced user, from what the watcher could not explain.</summary>
public sealed class TraineeQuestion
{
    /// <summary>calculation | rule | button | menu | screen | link</summary>
    public string Kind { get; set; } = "";
    public string Screen { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A hover tip: the help text the program shows for a control.</summary>
public sealed class HoverHint
{
    public string Screen { get; set; } = "";
    public string Control { get; set; } = "";
    public string Text { get; set; } = "";
    public int Seen { get; set; }
    public Evidence Evidence { get; set; } = new();
}

public sealed class LogicReport
{
    public List<HoverHint> Hints { get; set; } = new();
    public List<FieldLink> Links { get; set; } = new();
    public List<ScreenCalc> Calculations { get; set; } = new();
    public List<ScreenCalc> Unexplained { get; set; } = new();
    public List<ButtonLead> Leads { get; set; } = new();
    public List<NotUsedItem> NotUsed { get; set; } = new();
    public List<TraineeQuestion> Questions { get; set; } = new();
}

/// <summary>
/// Learns what a trainee learns by sitting next to an experienced user, from the screen alone (no database needed):
/// which values the program fills in after which action (field connections), which of those are calculations over
/// the other fields on the screen and which formula keeps fitting, what each button leads to, what nobody used, and
/// the questions worth asking. Every counter is additive per session (incremental learn equals a rebuild); values are
/// only held while a session is processed and never reach the memory files.
/// </summary>
public static class LogicLearner
{
    public const int MaxInputs = 8;
    public const double LinkWindowSeconds = 3;
    private static readonly HashSet<string> WindowButtons = DialogDetector.WindowButtons;

    public static void Accumulate(LearnState s, List<UiEvent> session)
    {
        var values = new Dictionary<string, Dictionary<string, string>>();
        (string kind, string name, string win, DateTime t)? user = null;
        (string win, string name, DateTime t)? press = null;
        foreach (var e in CauseFirst(session))
        {
            var win = Titles.Key(e.Win);
            if (e.Act == Acts.Value && win.Length > 0)
            {
                var f = e.FieldKey;
                var v = e.Val ?? "";
                if (!values.TryGetValue(win, out var cur)) values[win] = cur = new Dictionary<string, string>();
                if (e.By == ValueBy.App)
                {
                    if (v != Masking.Masked && v.Trim().Length > 0)
                    {
                        if (user is { } u && u.win == win && u.name != f && (e.T - u.t).TotalSeconds is >= -0.5 and <= LinkWindowSeconds)
                            Add(s.Fills, LearnState.Key(win, u.kind + ":" + u.name, f), e.Source);
                        LearnCalc(s, win, f, v, cur, e.Source);
                    }
                }
                else
                {
                    user = ("typed", f, win, e.T);
                    Count(s.Causes, LearnState.Key(win, "typed:" + f));
                }
                cur[f] = v;
            }
            else if (e.Act == Acts.Select && win.Length > 0)
            {
                var name = e.FieldKey;
                user = ("chose", name, win, e.T);
                Count(s.Causes, LearnState.Key(win, "chose:" + name));
            }
            else if (e.Act == Acts.Invoke && win.Length > 0)
            {
                var kind = e.How == PressHow.Toggle ? "ticked" : e.Ct == "MenuItem" || e.How == PressHow.Menu ? "chose menu" : "pressed";
                var name = e.FieldKey;
                user = (kind, name, win, e.T);
                Count(s.Causes, LearnState.Key(win, kind + ":" + name));
                if (kind == "pressed") press = (win, name, e.T);
            }
            else if (e.Act is Acts.Open or Acts.Dialog && press is { } p && (e.T - p.t).TotalSeconds is >= 0 and <= LinkWindowSeconds)
            {
                var target = Titles.Key(e.Name.Length > 0 ? e.Name : e.Win);
                if (target.Length > 0 && target != p.win)
                    Add(s.Leads, LearnState.Key(p.win, p.name, target, e.Act == Acts.Dialog ? "message" : "window"), e.Source);
                press = null;
            }
            else if (e.Act == Acts.Close && e.Ct == "Window")
            {
                values.Remove(Titles.Key(e.Name));
            }
            else if (e.Act == Acts.Hint && win.Length > 0 && !string.IsNullOrWhiteSpace(e.Val) && e.Val != Masking.Masked)
            {
                Add(s.Hints, LearnState.Key(win, e.FieldKey, e.Val.Trim()), e.Source);
            }
            else if (e.Act == Acts.Menu)
            {
                if (!string.IsNullOrEmpty(e.Name) && win.Length > 0) Count(s.Causes, LearnState.Key(win, "chose menu:" + e.Name));
                foreach (var item in (e.Items ?? new()).Where(i => i.Trim().Length > 0))
                    Add(s.MenuSeen, LearnState.Key(e.Name ?? "", item.Trim()), e.Source);
            }
        }
    }

    /// <summary>
    /// A program that recalculates while the user types changes its own field before Windows reports the typed value
    /// (the total arrives some milliseconds before the quantity that caused it). A fill that comes within half a second
    /// before a value the user typed in the same window is processed after it, so the cause comes first.
    /// </summary>
    public static List<UiEvent> CauseFirst(List<UiEvent> session)
    {
        var ordered = new List<UiEvent>(session.Count);
        var held = new List<UiEvent>();
        foreach (var e in session)
        {
            if (e.Act == Acts.Value && e.By == ValueBy.App) { held.Add(e); continue; }
            if (held.Count > 0)
            {
                var typedRightAfter = e.Act == Acts.Value && e.By != ValueBy.App
                    && held.All(h => Titles.Key(h.Win) == Titles.Key(e.Win) && (e.T - h.T).TotalMilliseconds <= 500);
                if (typedRightAfter) { ordered.Add(e); ordered.AddRange(held); held.Clear(); continue; }
                ordered.AddRange(held); held.Clear();
            }
            ordered.Add(e);
        }
        ordered.AddRange(held);
        return ordered;
    }

    private static void LearnCalc(LearnState s, string win, string field, string value, Dictionary<string, string> cur, string src)
    {
        var text = value.Trim();
        if (Values.TryDecimal(text, out var t))
        {
            var xs = new List<(string n, decimal v)>();
            foreach (var kv in cur.OrderBy(k => k.Key, StringComparer.Ordinal))
                if (kv.Key != field && Values.TryDecimal(kv.Value.Trim(), out var d) && d != 0) xs.Add((kv.Key, d));
            LearnCalc(s, win, field, t, xs, src);
            return;
        }
        if (text.Length < 2) return;
        var fits = new List<string>();
        foreach (var kv in cur.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (kv.Key != field && string.Equals(kv.Value.Trim(), text, StringComparison.OrdinalIgnoreCase)) fits.Add("= " + kv.Key);
        if (fits.Count == 0) return; // a text the program looked up (a name for a code) is a link, not a calculation
        Count(s.CalcTests, LearnState.Key(win, field));
        foreach (var f in fits.Distinct()) Add(s.CalcFits, LearnState.Key(win, field, f), src);
    }

    /// <summary>
    /// Tests one number the program showed against the given inputs (in the caller's order of relevance; at most
    /// <see cref="MaxInputs"/> are used). Shared by the tree path and the picture-text path (OcrLogic).
    /// </summary>
    public static void LearnCalc(LearnState s, string win, string field, decimal t, List<(string n, decimal v)> inputs, string src)
    {
        if (t == 0) return;
        var xs = inputs.Where(x => x.v != 0 && x.n.Length > 0 && x.n != field).Take(MaxInputs).ToList();
        if (xs.Count == 0) return;
        var fits = Formulas(t, xs);
        if (fits.Count == 0)
        {
            // a constant factor (a tax or unit rate): only counts when the same factor keeps coming back
            foreach (var x in xs)
            {
                var k = Calc(() => t / x.v);
                if (k is not { } kk || kk == 1 || Math.Abs(kk) < 0.0001m || Math.Abs(kk) > 10000m) continue;
                var kr = Math.Round(kk, 4);
                if (Close(t, x.v * kr)) fits.Add($"{x.n} × {kr.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }
        Count(s.CalcTests, LearnState.Key(win, field));
        foreach (var f in fits.Distinct()) Add(s.CalcFits, LearnState.Key(win, field, f), src);
    }

    /// <summary>Every simple formula over the screen's other numbers that gives the target (sums, differences, products, ratios, percentages, discount and tax forms).</summary>
    public static List<string> Formulas(decimal t, List<(string n, decimal v)> xs)
    {
        var r = new List<string>();
        void Try(decimal? v, string f) { if (v is { } d && Close(t, d) && !r.Contains(f)) r.Add(f); }
        var n = xs.Count;
        for (var i = 0; i < n; i++) Try(xs[i].v, "= " + xs[i].n);
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var (a, b) = (xs[i], xs[j]);
                Try(Calc(() => a.v + b.v), $"{a.n} + {b.n}");
                Try(Calc(() => a.v * b.v), $"{a.n} × {b.n}");
                Try(Calc(() => a.v * b.v / 100), $"{a.n} × {b.n} / 100");
            }
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                var (a, b) = (xs[i], xs[j]);
                Try(Calc(() => a.v - b.v), $"{a.n} − {b.n}");
                Try(Calc(() => a.v / b.v), $"{a.n} ÷ {b.n}");
                Try(Calc(() => a.v * (1 - b.v / 100)), $"{a.n} × (1 − {b.n} / 100)");
                Try(Calc(() => a.v * (1 + b.v / 100)), $"{a.n} × (1 + {b.n} / 100)");
            }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                for (var k = 0; k < n; k++)
                {
                    if (k == i || k == j) continue;
                    var (a, b, c) = (xs[i], xs[j], xs[k]);
                    Try(Calc(() => a.v * b.v * (1 - c.v / 100)), $"{a.n} × {b.n} × (1 − {c.n} / 100)");
                    Try(Calc(() => a.v * b.v * (1 + c.v / 100)), $"{a.n} × {b.n} × (1 + {c.n} / 100)");
                    Try(Calc(() => a.v * b.v + c.v), $"{a.n} × {b.n} + {c.n}");
                    Try(Calc(() => a.v * b.v - c.v), $"{a.n} × {b.n} − {c.n}");
                    if (k > j) Try(Calc(() => a.v + b.v + c.v), $"{a.n} + {b.n} + {c.n}");
                }
        return r;
    }

    private static decimal? Calc(Func<decimal> f) { try { return f(); } catch { return null; } }

    /// <summary>Equal as shown on screen: within half a cent, or within a millionth for large numbers.</summary>
    public static bool Close(decimal t, decimal v) => Math.Abs(t - v) <= 0.005m + Math.Abs(t) * 0.000001m;

    private static int Operands(string formula) => formula.StartsWith("= ", StringComparison.Ordinal) ? 1 : 1 + formula.Count(ch => ch is '×' or '+' or '−' or '÷');

    public static string CauseText(string when)
    {
        var i = when.IndexOf(':');
        if (i < 0) return when;
        var (kind, name) = (when[..i], when[(i + 1)..]);
        return kind switch
        {
            "typed" => $"typing {name}",
            "chose" => $"choosing {name}",
            "pressed" => $"pressing {name}",
            "ticked" => $"ticking {name}",
            "chose menu" => $"choosing the menu item {name}",
            _ => $"{kind} {name}",
        };
    }

    public static LogicReport Finalize(LearnState s, LearnInput input, LearnResult r)
    {
        var rep = new LogicReport();
        var sep = LearnState.Sep;

        foreach (var (key, ev) in s.Fills.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var p = key.Split(sep);
            if (p.Length < 3) continue;
            var causes = Math.Max(s.Causes.GetValueOrDefault(LearnState.Key(p[0], p[1])), ev.Seen);
            // a rule, not a coincidence: seen at least twice, and after at least half of the times the cause happened
            if (ev.Seen >= 2 && ev.Seen * 2 >= causes)
                rep.Links.Add(new FieldLink { Screen = p[0], When = p[1], Field = p[2], Seen = ev.Seen, CauseCount = causes, Evidence = ev });
        }

        foreach (var (tkey, tested) in s.CalcTests.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var parts = tkey.Split(sep);
            if (parts.Length < 2) continue;
            var prefix = tkey + sep;
            var cands = s.CalcFits.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => (f: k.Key[prefix.Length..], ev: k.Value))
                .OrderByDescending(c => c.ev.Seen).ThenBy(c => Operands(c.f)).ThenBy(c => c.f, StringComparer.Ordinal).ToList();
            var calc = new ScreenCalc { Screen = parts[0], Field = parts[1], Tested = tested };
            if (cands.Count > 0)
            {
                calc.Formula = cands[0].f; calc.Fits = cands[0].ev.Seen; calc.Evidence = cands[0].ev;
                calc.Alternatives = cands.Skip(1).Where(c => c.ev.Seen == calc.Fits).Select(c => c.f).Take(3).ToList();
            }
            if (calc.Fits >= 2 && calc.Fits * 5 >= tested * 4) rep.Calculations.Add(calc);
            else if (calc.Fits >= 2 && calc.Fits * 2 >= tested) { calc.WithExceptions = true; rep.Calculations.Add(calc); }
            else if (tested >= 2) rep.Unexplained.Add(calc);
        }

        foreach (var (key, ev) in s.Leads.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var p = key.Split(sep);
            if (p.Length < 4) continue;
            rep.Leads.Add(new ButtonLead { Screen = p[0], Button = p[1], Opens = p[2], Message = p[3] == "message", Seen = ev.Seen, Evidence = ev });
        }

        // the same tip on the same control at least twice: a help text, not a value that happened to be under the pointer
        foreach (var (key, ev) in s.Hints.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var p = key.Split(sep);
            if (p.Length >= 3 && ev.Seen >= 2) rep.Hints.Add(new HoverHint { Screen = p[0], Control = p[1], Text = p[2], Seen = ev.Seen, Evidence = ev });
        }

        rep.NotUsed = NotUsed(s, input, rep);
        rep.Questions = Questions(s, r, rep);
        return rep;
    }

    private static List<NotUsedItem> NotUsed(LearnState s, LearnInput input, LogicReport rep)
    {
        // where each cause happened: "pressed:Save" -> windows
        var causeWins = new Dictionary<string, List<string>>();
        foreach (var key in s.Causes.Keys)
        {
            var p = key.Split(LearnState.Sep);
            if (p.Length < 2) continue;
            if (!causeWins.TryGetValue(p[1], out var wins)) causeWins[p[1]] = wins = new List<string>();
            wins.Add(p[0]);
        }
        bool Related(string a, string b) => a == b || a.StartsWith(b + " › ", StringComparison.Ordinal) || b.StartsWith(a + " › ", StringComparison.Ordinal);
        bool UsedOn(string screen, params string[] causes) => causes.Any(c => causeWins.TryGetValue(c, out var wins) && wins.Any(w => Related(w, screen)));
        bool UsedAnywhere(params string[] causes) => causes.Any(causeWins.ContainsKey);
        var menuPaths = new HashSet<string>(s.Screens.Values.SelectMany(si => si.MenuPaths.Keys), StringComparer.Ordinal);
        // a menu whose items were seen was opened by someone
        foreach (var k in s.MenuSeen.Keys) { var p = k.Split(LearnState.Sep); if (p.Length >= 2 && p[0].Length > 0) menuPaths.Add(p[0]); }
        // a window whose tabs are recorded as screens of their own: its buttons and fields are listed on the tab, not twice
        var tabbed = new HashSet<string>(input.Screens.Select(x => Titles.Key(x.Title)).Where(k => k.Contains(" › ")).Select(k => k[..k.IndexOf(" › ", StringComparison.Ordinal)]), StringComparer.Ordinal);
        var filled = new HashSet<string>(s.Fills.Keys.Select(k => k.Split(LearnState.Sep)).Where(p => p.Length >= 3).Select(p => p[0] + LearnState.Sep + p[2])
            .Concat(s.CalcTests.Keys), StringComparer.Ordinal);

        var list = new List<NotUsedItem>();
        var menuItems = new SortedSet<string>(StringComparer.Ordinal);
        var records = input.Screens.OrderBy(x => Titles.Key(x.Title), StringComparer.Ordinal).ToList();
        var dialogs = new HashSet<string>(records.Where(x => x.Kind == ScreenKinds.Dialog || DialogDetector.Detect("", x.Controls).isDialog).Select(x => Titles.Key(x.Title)), StringComparer.Ordinal);
        foreach (var rec in records)
        {
            var key = Titles.Key(rec.Title);
            var controls = rec.Controls;
            // a tab record made before documents were dumped on their own holds the parent window's toolbars too: not this document's
            if (key.Contains(" › ", StringComparison.Ordinal))
            {
                var parentKey = key[..key.IndexOf(" › ", StringComparison.Ordinal)];
                var parent = records.FirstOrDefault(x => Titles.Key(x.Title) == parentKey);
                if (parent is not null) controls = TabDocuments.WithoutParentControls(controls, parent.Controls);
            }
            var content = DialogDetector.Content(controls);
            foreach (var m in content.Where(c => c.Type == "MenuItem" && c.Name.Trim().Length > 0)) menuItems.Add(m.Name.Trim());
            if (rec.Kind is ScreenKinds.Dialog or ScreenKinds.Session or ScreenKinds.Menu || dialogs.Contains(key)) continue;
            s.Screens.TryGetValue(key, out var si);
            var listControls = !tabbed.Contains(key);
            foreach (var b in !listControls ? Enumerable.Empty<string>() : content.Where(c => c.Type is "Button" or "SplitButton" && !PartOfAnotherControl(content, c)).Select(c => c.Name.Trim()).Where(n => n.Length > 0 && !WindowButtons.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                if (!UsedOn(key, "pressed:" + b) && si?.Actions.Contains(b) != true) list.Add(new NotUsedItem { Screen = key, Kind = "button", Name = b });
            foreach (var tab in content.Where(c => c.Type == "TabItem").Select(c => c.Name.Trim()).Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                if (!UsedOn(key, "chose:" + tab, "chose menu:" + tab) && !s.Screens.ContainsKey(key + " › " + tab) && !menuPaths.Contains(tab))
                    list.Add(new NotUsedItem { Screen = key, Kind = "tab", Name = tab });
            foreach (var row in content.Where(c => listControls && c.Type is "Edit" or "ComboBox" && c.IsReadOnly != true && !c.IsPassword))
            {
                var label = Labels.Resolve(row, controls).Trim();
                if (label.Length == 0) continue;
                if (UsedOn(key, "typed:" + label, "chose:" + label) || si?.Fields.Contains(label) == true || filled.Contains(key + LearnState.Sep + label)) continue;
                if (list.Any(x => x.Screen == key && x.Kind == "field" && x.Name == label)) continue;
                list.Add(new NotUsedItem { Screen = key, Kind = "field", Name = label });
            }
        }
        foreach (var k in s.MenuSeen.Keys) { var p = k.Split(LearnState.Sep); if (p.Length >= 2) menuItems.Add(p[1]); }
        foreach (var m in menuItems)
            if (!UsedAnywhere("chose menu:" + m, "pressed:" + m, "chose:" + m) && !menuPaths.Contains(m))
                list.Add(new NotUsedItem { Screen = "(menus)", Kind = "menu item", Name = m });
        return list;
    }

    /// <summary>The arrow of a dropdown, the arrows of a scroll bar or spin box, a grid's header button: part of another control, not a button of the screen.</summary>
    private static bool PartOfAnotherControl(List<ControlRow> rows, ControlRow row)
    {
        var i = rows.IndexOf(row);
        for (var j = i - 1; j >= 0; j--)
            if (rows[j].Depth < row.Depth)
                return rows[j].Type is "ComboBox" or "ScrollBar" or "Spinner" or "DataGrid" or "Table" or "Header" or "HeaderItem" or "List" or "Tree" or "TitleBar";
        return false;
    }

    private static List<TraineeQuestion> Questions(LearnState s, LearnResult r, LogicReport rep)
    {
        var q = new List<TraineeQuestion>();
        foreach (var c in rep.Calculations.Where(c => c.WithExceptions))
            q.Add(new() { Kind = "calculation", Screen = c.Screen, Text = $"On {c.Screen}, {c.Field} matched {c.Formula} {c.Fits} of {c.Tested} times. What decides the other {c.Exceptions}?" });
        foreach (var c in rep.Unexplained)
            q.Add(new() { Kind = "calculation", Screen = c.Screen, Text = $"On {c.Screen}, the program fills {c.Field} itself ({c.Tested}×), but I could not work out how. How is it calculated?" });
        foreach (var rule in r.Rules.Where(x => x.Evidence.Seen == 1).OrderBy(x => x.Screen, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal))
            q.Add(new() { Kind = "rule", Screen = rule.Screen, Text = $"I saw the message \"{rule.Message}\" only once on {rule.Screen}{(rule.Field.Length == 0 ? "" : rule.Field.StartsWith("on ", StringComparison.Ordinal) ? $" ({rule.Field})" : $" (after {rule.Field})")}. When exactly does it appear?" });
        // buttons of the documents people opened, never the window's own toolbar (its buttons belong to no document), at most ten per screen
        var opened = new HashSet<string>(s.Screens.Keys, StringComparer.Ordinal); // a ScreenInfo exists only where events happened
        var tabbedWindows = new HashSet<string>(s.Screens.Keys.Where(k => k.Contains(" › ", StringComparison.Ordinal)).Select(k => k[..k.IndexOf(" › ", StringComparison.Ordinal)]), StringComparer.Ordinal);
        foreach (var g in rep.NotUsed.Where(x => x.Kind == "button" && opened.Contains(x.Screen) && !tabbedWindows.Contains(x.Screen)).GroupBy(x => x.Screen).OrderBy(g => g.Key, StringComparer.Ordinal))
            foreach (var b in g.Take(10))
                q.Add(new() { Kind = "button", Screen = b.Screen, Text = $"What does {b.Name} on {b.Screen} do? Nobody pressed it while I watched." });
        foreach (var m in rep.NotUsed.Where(x => x.Kind == "menu item").Take(10))
            q.Add(new() { Kind = "menu", Screen = "", Text = $"The menu item {m.Name} was never chosen. What is it for, and who uses it?" });
        foreach (var si in r.Screens.Where(x => x.Kind == ScreenKinds.Screen && x.Visits == 1).OrderBy(x => x.Title, StringComparer.Ordinal).Take(5))
            q.Add(new() { Kind = "screen", Screen = si.Title, Text = $"{si.Title} was opened only once. Is it part of a regular task, and who does it?" });
        return q.Take(60).ToList();
    }

    private static void Add(Dictionary<string, Evidence> d, string key, string src)
    {
        if (!d.TryGetValue(key, out var ev)) d[key] = ev = new Evidence();
        ev.Add(src.Length > 0 ? src : "(events)");
    }

    private static void Count(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
}
