using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core.Learning;

/// <summary>
/// memory/logic.md and memory/logic.json: the program's logic the way a trainee would write it down, per screen
/// (how you get there, what you type, what the program fills in and calculates, what each button leads to, the rules,
/// the tables it writes, what nobody used) plus the questions to ask an experienced user. logic.json carries the same
/// for a program or an AI comparing it with another system. No typed values: names, formulas, counts and messages only.
/// </summary>
public static class LogicWriter
{
    public static string Markdown(LearnResult r)
    {
        var L = r.Logic;
        var sb = new StringBuilder();
        sb.AppendLine("# Logic (learned): what a trainee would know");
        sb.AppendLine();
        sb.AppendLine($"App: **{r.AppName}**. Learned by watching, like a trainee sitting next to the people who use it: no program code was read.");
        sb.AppendLine("Every line ends with a confidence (seen N× = observed that many times; of M = how often the cause or the field came up) and the evidence file to open.");
        sb.AppendLine("Use it to fill gaps in another system: compare each screen's connections, calculations and rules with what that system does.");
        sb.AppendLine();

        sb.AppendLine("## How the screens connect");
        sb.AppendLine();
        var nav = r.Navigation.OrderByDescending(n => n.Count).ThenBy(n => n.From, StringComparer.Ordinal).ThenBy(n => n.To, StringComparer.Ordinal).Take(40).ToList();
        if (nav.Count == 0) sb.AppendLine("- Not enough movement between screens seen yet.");
        foreach (var n in nav) sb.AppendLine($"- {n.From} → {n.To} (seen {n.Count}×)");
        sb.AppendLine();

        foreach (var key in ScreenKeys(r))
        {
            var si = r.Screens.FirstOrDefault(s => s.Title == key);
            sb.AppendLine($"## Screen: {key}");
            sb.AppendLine();
            if (si is not null)
            {
                var reached = si.ReachedFrom is null ? null : $"{si.ReachedFrom}{(si.ReachedFromCount > 0 ? $" (seen {si.ReachedFromCount}×)" : "")}";
                var menu = si.MenuPath is null ? null : $"{si.MenuPath} (seen {si.MenuPaths.GetValueOrDefault(si.MenuPath)}×)";
                if (reached is not null || menu is not null)
                    sb.AppendLine($"- **How you get here:** {string.Join("; ", new[] { reached is null ? null : "from " + reached, menu is null ? null : "menu " + menu }.Where(x => x is not null))}");
                var typed = si.FieldTyped.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(15).ToList();
                if (typed.Count > 0)
                    sb.AppendLine("- **What people type here:** " + string.Join(", ", typed.Select(f => $"{f.Key} ({f.Value}×{Kind(si, f.Key)})")));
            }
            var links = L.Links.Where(l => l.Screen == key).ToList();
            if (links.Count > 0)
            {
                sb.AppendLine("- **Filled in by the program:**");
                foreach (var g in links.GroupBy(l => l.When).OrderBy(g => g.Key, StringComparer.Ordinal))
                    sb.AppendLine($"  - {Cap(g.First().WhenText)} fills {string.Join(", ", g.OrderBy(l => l.Field, StringComparer.Ordinal).Select(l => $"{l.Field} (seen {l.Seen}× of {l.CauseCount})"))}{(g.Any(l => l.FromOcr) ? " (from the pictures' text)" : "")}; evidence: {g.First().Evidence.Pointer}");
            }
            var calcs = L.Calculations.Where(c => c.Screen == key).ToList();
            if (calcs.Count > 0)
            {
                sb.AppendLine("- **Calculations:**");
                foreach (var c in calcs)
                {
                    var f = c.Formula.StartsWith("= ", StringComparison.Ordinal) ? $"{c.Field} is copied from {c.Formula[2..]}" : $"{c.Field} = {c.Formula}";
                    var alt = c.Alternatives.Count > 0 ? $" (also fits: {string.Join("; ", c.Alternatives)})" : "";
                    var exc = c.WithExceptions ? $", {c.Exceptions} exceptions: ask why" : "";
                    sb.AppendLine($"  - {f}{alt} (seen {c.Fits}× of {c.Tested}{exc}{(c.FromOcr ? ", from the pictures' text" : "")}); evidence: {c.Evidence.Pointer}");
                }
            }
            var leads = L.Leads.Where(l => l.Screen == key).ToList();
            if (leads.Count > 0)
            {
                sb.AppendLine("- **What the buttons lead to:**");
                foreach (var g in leads.GroupBy(l => l.Button).OrderBy(g => g.Key, StringComparer.Ordinal))
                    sb.AppendLine($"  - {g.Key} opens {string.Join(", ", g.OrderByDescending(l => l.Seen).ThenBy(l => l.Opens, StringComparer.Ordinal).Select(l => $"{l.Opens}{(l.Message ? " (a message)" : "")} (seen {l.Seen}×)"))}; evidence: {g.First().Evidence.Pointer}");
            }
            var hints = L.Hints.Where(h => h.Screen == key).ToList();
            if (hints.Count > 0)
            {
                sb.AppendLine("- **Hover tips (help the program gives):**");
                foreach (var h in hints) sb.AppendLine($"  - {h.Control}: \"{h.Text}\" (seen {h.Seen}×); evidence: {h.Evidence.Pointer}");
            }
            var rules = r.Rules.Where(x => Titles.Key(x.Screen) == key).OrderBy(x => x.Field, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
            if (rules.Count > 0)
            {
                sb.AppendLine("- **Rules and messages:**");
                foreach (var x in rules)
                    sb.AppendLine($"  - {(x.Field.Length > 0 ? $"{x.Field}{(x.Pattern.Length > 0 ? $" ({x.Pattern})" : "")}: " : "")}\"{x.Message}\" ({x.Evidence})");
            }
            var writes = r.ScreenWrites.Where(w => Titles.Key(w.Screen) == key).OrderBy(w => w.Table, StringComparer.Ordinal).ToList();
            if (writes.Count > 0)
                sb.AppendLine("- **Saves to:** " + string.Join(", ", writes.Select(w => $"{w.Table} (seen {w.Count}×{(w.Always ? ", every save" : "")})")));
            var unused = L.NotUsed.Where(n => n.Screen == key).ToList();
            if (unused.Count > 0)
                foreach (var g in unused.GroupBy(n => n.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                    sb.AppendLine($"- **Not used while I watched ({g.Key}s):** {string.Join(", ", g.Select(n => n.Name))}");
            sb.AppendLine();
        }

        if (r.Formulas.Count + r.StatusFlows.Count + r.Sequences.Count + r.Relations.Count > 0)
        {
            sb.AppendLine("## Logic found in the data");
            sb.AppendLine();
            foreach (var f in r.Formulas) sb.AppendLine($"- {f.Table}.{f.Column} = {f.Expression} (fits {f.Explained} of {f.Tested} rows; {f.Evidence})");
            foreach (var st in r.StatusFlows) sb.AppendLine($"- {st.Table}.{st.Column} moves {string.Join(" → ", st.States)} ({st.Evidence})");
            foreach (var q in r.Sequences) sb.AppendLine($"- {q.Table}.{q.Column} is numbered in sequence{(q.Prefix is null ? "" : $" with prefix {q.Prefix}")} ({q.Evidence})");
            foreach (var rel in r.Relations) sb.AppendLine($"- {rel.FromTable}.{rel.FromColumn} points to {rel.ToTable}.{rel.ToColumn} ({rel.Evidence})");
            sb.AppendLine();
        }

        var menus = L.NotUsed.Where(n => n.Kind == "menu item").ToList();
        sb.AppendLine("## Not seen yet");
        sb.AppendLine();
        sb.AppendLine(menus.Count == 0
            ? "- Every menu item I saw was used at least once."
            : $"- Menu items nobody chose ({menus.Count}): {string.Join(", ", menus.Select(m => m.Name))}");
        var screensUnused = L.NotUsed.Where(n => n.Kind != "menu item").Select(n => n.Screen).Distinct().Count();
        if (screensUnused > 0) sb.AppendLine($"- {screensUnused} screen(s) have buttons, tabs or fields nobody used; they are listed under each screen above.");
        sb.AppendLine("- To learn the rest: watch people in other roles, or let the App Twin Kit explore the screens nobody opened.");
        sb.AppendLine();

        sb.AppendLine("## Questions I would ask an experienced user");
        sb.AppendLine();
        if (L.Questions.Count == 0) sb.AppendLine("- None right now.");
        foreach (var q in L.Questions) sb.AppendLine($"- {q.Text}");
        return sb.ToString();
    }

    public static string Json(LearnResult r)
    {
        var L = r.Logic;
        var doc = new
        {
            app = r.AppName,
            about = "Learned by watching the program in use (no code read). Counts are observations; formulas use the field names shown on screen.",
            navigation = r.Navigation.OrderByDescending(n => n.Count).ThenBy(n => n.From, StringComparer.Ordinal).ThenBy(n => n.To, StringComparer.Ordinal)
                .Select(n => new { from = n.From, to = n.To, seen = n.Count }).ToList(),
            screens = ScreenKeys(r).Select(key =>
            {
                var si = r.Screens.FirstOrDefault(s => s.Title == key);
                return new
                {
                    name = key,
                    reachedFrom = si?.ReachedFrom,
                    menuPath = si?.MenuPath,
                    visits = si?.Visits ?? 0,
                    typedFields = si is null ? new List<object>() : si.FieldTyped.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal)
                        .Select(f => (object)new { name = f.Key, typed = f.Value, kind = TopPattern(si, f.Key) }).ToList(),
                    filledByProgram = L.Links.Where(l => l.Screen == key).Select(l => new { when = l.WhenText, field = l.Field, seen = l.Seen, of = l.CauseCount }).ToList(),
                    calculations = L.Calculations.Where(c => c.Screen == key)
                        .Select(c => new { field = c.Field, formula = c.Formula, fits = c.Fits, tested = c.Tested, withExceptions = c.WithExceptions, alternatives = c.Alternatives, source = c.FromOcr ? "ocr" : "tree" }).ToList(),
                    hoverTips = L.Hints.Where(h => h.Screen == key).Select(h => new { control = h.Control, text = h.Text, seen = h.Seen }).ToList(),
                    buttons = L.Leads.Where(l => l.Screen == key).Select(l => new { button = l.Button, opens = l.Opens, message = l.Message, seen = l.Seen }).ToList(),
                    rules = r.Rules.Where(x => Titles.Key(x.Screen) == key).OrderBy(x => x.Field, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal)
                        .Select(x => new { field = x.Field, valueKind = x.Pattern, message = x.Message, kind = x.Kind, seen = x.Evidence.Seen }).ToList(),
                    savesTo = r.ScreenWrites.Where(w => Titles.Key(w.Screen) == key).OrderBy(w => w.Table, StringComparer.Ordinal).Select(w => new { table = w.Table, seen = w.Count, everySave = w.Always }).ToList(),
                    notUsed = L.NotUsed.Where(n => n.Screen == key).Select(n => new { kind = n.Kind, name = n.Name }).ToList(),
                };
            }).ToList(),
            dataLogic = new
            {
                formulas = r.Formulas.Select(f => new { table = f.Table, column = f.Column, expression = f.Expression, fits = f.Explained, tested = f.Tested }).ToList(),
                statusFlows = r.StatusFlows.Select(s => new { table = s.Table, column = s.Column, states = s.States }).ToList(),
                sequences = r.Sequences.Select(s => new { table = s.Table, column = s.Column, prefix = s.Prefix }).ToList(),
                relations = r.Relations.Select(x => new { from = $"{x.FromTable}.{x.FromColumn}", to = $"{x.ToTable}.{x.ToColumn}" }).ToList(),
            },
            menuItemsNeverChosen = L.NotUsed.Where(n => n.Kind == "menu item").Select(n => n.Name).ToList(),
            unexplained = L.Unexplained.Select(c => new { screen = c.Screen, field = c.Field, filledTimes = c.Tested }).ToList(),
            questions = L.Questions.Select(q => new { kind = q.Kind, screen = q.Screen, text = q.Text }).ToList(),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>Screens worth a section: real screens and tabs seen, plus any screen the logic mentions; dialogs and login windows are left out.</summary>
    private static List<string> ScreenKeys(LearnResult r)
    {
        var L = r.Logic;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in r.Screens.Where(s => s.Kind is ScreenKinds.Screen or ScreenKinds.Tab && s.Title.Length > 0)) keys.Add(s.Title);
        foreach (var k in L.Links.Select(l => l.Screen).Concat(L.Hints.Select(h => h.Screen)).Concat(L.Calculations.Select(c => c.Screen)).Concat(L.Leads.Select(l => l.Screen))
                     .Concat(L.NotUsed.Where(n => n.Screen != "(menus)").Select(n => n.Screen)))
            if (k.Length > 0) keys.Add(k);
        foreach (var s in r.Screens.Where(s => s.Kind is ScreenKinds.Dialog or ScreenKinds.Session or ScreenKinds.Menu)) keys.Remove(s.Title);
        return keys.ToList();
    }

    private static string TopPattern(ScreenInfo si, string field)
        => si.FieldPatterns.TryGetValue(field, out var p) && p.Count > 0 ? p.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key : "";

    private static string Kind(ScreenInfo si, string field) { var k = TopPattern(si, field); return k.Length > 0 ? ", " + k : ""; }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
