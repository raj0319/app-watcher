using AppWatcher.Core.Models;

namespace AppWatcher.Core.Learning;

/// <summary>A message window right after a value change becomes a validation rule (field, value pattern, message).</summary>
public static class ValidationRuleLearner
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    public static List<ValidationRule> Learn(List<UiEvent> events)
    {
        var rules = new Dictionary<(string, string, string, string), ValidationRule>();
        var sorted = events.OrderBy(e => e.T).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var d = sorted[i];
            if (d.Act != Acts.Dialog) continue;
            UiEvent? cause = null;
            for (var j = i - 1; j >= 0 && d.T - sorted[j].T <= Window; j--)
            {
                if (sorted[j].Act == Acts.Value) { cause = sorted[j]; break; }
                if (sorted[j].Act == Acts.Invoke && cause is null) cause = sorted[j];
            }
            if (cause is null) continue;
            var screen = Titles.Key(cause.Win);
            var field = cause.Act == Acts.Invoke ? "on " + cause.FieldKey : cause.FieldKey;
            var pattern = cause.Act == Acts.Invoke ? "(button)" : ValuePattern.Of(cause.Val);
            var message = MessageKey(d);
            var key = (screen, field, pattern, message);
            if (!rules.TryGetValue(key, out var r))
                rules[key] = r = new ValidationRule { Screen = screen, Field = field, Pattern = pattern, Message = message };
            r.Evidence.Add(d.Source);
        }
        return rules.Values.OrderBy(r => r.Screen).ThenByDescending(r => r.Evidence.Seen).ToList();
    }

    public static string MessageKey(UiEvent dialog)
    {
        var text = string.IsNullOrWhiteSpace(dialog.Val) ? dialog.Name : dialog.Val;
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        var collapsed = new string(text.Select(c => char.IsDigit(c) ? '#' : c).ToArray());
        return collapsed.Length > 160 ? collapsed[..160] + "..." : collapsed;
    }
}
