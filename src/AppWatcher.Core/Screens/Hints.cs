using System.Text.RegularExpressions;

namespace AppWatcher.Core.Screens;

/// <summary>Which hover tips are help texts worth keeping. Pure, so the rules are tested.</summary>
public static partial class Hints
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 /&().'-]{0,40}:\s*\S")]
    private static partial Regex LabelColonValue();

    /// <summary>
    /// A tip over a field that reads "Remit-to: Same" is the field's own label and value (the toolkit shows a cut-off field
    /// in full), not a help text: dropped. Tips over buttons, menu items and tabs are kept whatever they say.
    /// </summary>
    public static bool IsFieldValueTip(string controlType, string text)
    {
        if (controlType is not ("Edit" or "ComboBox" or "Spinner" or "Document" or "Text" or "DataItem")) return false;
        var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return LabelColonValue().IsMatch(t) && t.Length <= 80;
    }
}
