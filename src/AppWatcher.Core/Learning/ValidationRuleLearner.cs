using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

/// <summary>
/// A message window right after a user action becomes a rule: after a typed value it is a validation rule
/// (field, value pattern, message); after a button or menu it is a message of that action (confirmations,
/// "record saved", "nothing selected").
/// </summary>
public static class ValidationRuleLearner
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ActionWindow = TimeSpan.FromSeconds(6);

    public static List<ValidationRule> Learn(List<UiEvent> events)
    {
        var state = new LearnState();
        foreach (var s in FlowLearner.Sessions(events)) Accumulate(state, s);
        return Finalize(state);
    }

    public static void Accumulate(LearnState state, List<UiEvent> session)
    {
        var sorted = session;
        for (var i = 0; i < sorted.Count; i++)
        {
            var d = sorted[i];
            if (d.Act != Acts.Dialog) continue;
            UiEvent? value = null; UiEvent? action = null;
            for (var j = i - 1; j >= 0 && d.T - sorted[j].T <= ActionWindow; j--)
            {
                var e = sorted[j];
                if (e.Act is Acts.Dialog or Acts.Close) continue;
                if (e.Act == Acts.Value && e.By == ValueBy.App) continue; // the program's own fill is not something the user did
                if (e.Act == Acts.Value && value is null && d.T - e.T <= Window) { value = e; break; }
                if (e.Act is Acts.Invoke or Acts.Select && action is null && e.Ct != "TabItem") action = e;
                if (e.Act == Acts.Focus && action is null && e.Ct is "Button" or "MenuItem" && d.T - e.T <= Window) action = e;
            }
            // the nearest of the two is the cause: a menu chosen after a value was typed owns the message that follows it
            var cause = value is not null && action is not null ? (action.T >= value.T ? action : value) : value ?? action;
            if (cause is null) continue;
            var screen = Titles.Key(cause.Win);
            var isValue = ReferenceEquals(cause, value);
            var field = isValue ? cause.FieldKey : "on " + Titles.Name(cause.FieldKey); // an action's name masked like a title
            var pattern = isValue ? ValuePattern.Of(cause.Val) : cause.Ct == "MenuItem" ? "(menu)" : "(button)";
            var message = MessageKey(d);
            var key = LearnState.Key(screen, field, pattern, message);
            if (!state.Rules.TryGetValue(key, out var r))
                state.Rules[key] = r = new ValidationRule { Screen = screen, Field = field, Pattern = pattern, Message = message, Kind = isValue ? "validation" : "message" };
            r.Evidence.Add(d.Source);
        }
    }

    public static List<ValidationRule> Finalize(LearnState state) =>
        state.Rules.Values.OrderBy(r => r.Screen).ThenBy(r => r.Kind).ThenByDescending(r => r.Evidence.Seen).ThenBy(r => r.Field).ThenBy(r => r.Message).ToList();

    public static string MessageKey(UiEvent dialog)
    {
        var text = string.IsNullOrWhiteSpace(dialog.Val) ? dialog.Name : dialog.Val;
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        var collapsed = new string(text.Select(c => char.IsDigit(c) ? '#' : c).ToArray());
        return collapsed.Length > 160 ? collapsed[..160] + "..." : collapsed;
    }
}
