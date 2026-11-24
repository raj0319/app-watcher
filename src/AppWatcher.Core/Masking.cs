using System.Text.RegularExpressions;

namespace AppWatcher.Core;

public static partial class Masking
{
    public const string Masked = "***";

    [GeneratedRegex(@"password|passwd|\bpin\b|pincode|secret|passcode|token|api.?key", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^\+?[\d\s().-]{7,}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\d{9,}")]
    private static partial Regex LongDigits();

    [GeneratedRegex(@"^(phone|fax|e-?mail|address|address line ?\d?|line ?\d|name|attention|attn|contact|city|street|zip|zip code|postal code|mobile|cell|website|web site|url|writer|customer|sold to|ordered by|buyer|vendor name|customer name|ship to name|bill to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonalField();

    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z .'-]{1,30},\s*[A-Z]{2}(\s+\d{5}(-\d{4})?)?\s*$")]
    private static partial Regex CityStateZip();

    [GeneratedRegex(@"\d{3}[-. ]\d{3}[-. ]\d{4}|\(\d{3}\)\s?\d{3}[-. ]\d{4}")]
    private static partial Regex PhoneInside();

    /// <summary>
    /// A control name that is really data: all digits (a record id), a phone number or a label carrying one ("Phone: 347-..."),
    /// a ZIP, "City, ST 12345", an e-mail, a street line. Such a name must not become a field name in the memory files.
    /// </summary>
    public static bool LooksLikeData(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var t = name.Trim();
        if (t.All(c => char.IsDigit(c) || c is '.' or ',' or '-' or ' ' or '/')) return t.Any(char.IsDigit);
        if (PhoneInside().IsMatch(t) || Email().IsMatch(t) || t.Contains('@')) return true;
        if (CityStateZip().IsMatch(t)) return true;
        if (t.Length > 2 && char.IsDigit(t[0]) && t.Any(char.IsLetter) && t.Contains(' ')) return true; // "64 Main Street"
        return false;
    }

    public static bool IsSensitiveName(string? name) => !string.IsNullOrEmpty(name) && SensitiveName().IsMatch(name);

    /// <summary>A field whose content is personal (a phone, an e-mail, an address, a name): what the program fills in there is kept as a shape, not a value.</summary>
    public static bool IsPersonalField(string? label) => !string.IsNullOrEmpty(label) && PersonalField().IsMatch(label.Trim());

    /// <summary>
    /// A value the program filled in, for the event log: personal fields keep only the shape (text controls only: a tick
    /// box stays On/Off whatever its name); everything else as <see cref="ForEvent"/>.
    /// </summary>
    public static string? ForAppValue(string? value, string? label, string? automationId, bool isPasswordControl, string? controlType = null)
    {
        if (value is null) return null;
        if (isPasswordControl || IsSensitiveName(label) || IsSensitiveName(automationId)) return Masked;
        if (controlType is "CheckBox" or "RadioButton") return value.Length > 40 ? value[..40] : value;
        if (IsPersonalField(label) || IsPersonalField(Clean(automationId))) return Shape(value);
        if (Email().IsMatch(value.Trim()) || value.Contains("mailto:", StringComparison.OrdinalIgnoreCase) || value.Contains('@') && value.Contains("href=", StringComparison.OrdinalIgnoreCase)) return Shape(value);
        return value.Length > 500 ? value[..500] + "..." : value;
    }

    private static string Clean(string? aid) => Screens.Labels.Clean(aid);

    /// <summary>Value recorded in the event log. Only password-like controls are masked here.</summary>
    public static string? ForEvent(string? value, string? controlName, string? automationId, bool isPasswordControl)
    {
        if (value is null) return null;
        if (isPasswordControl || IsSensitiveName(controlName) || IsSensitiveName(automationId)) return Masked;
        return value.Length > 500 ? value[..500] + "..." : value;
    }

    /// <summary>Value shown in an export bundle: personal-looking values are hidden, shape is kept.</summary>
    public static string ForExport(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value == Masked) return Masked;
        if (Email().IsMatch(value)) return "<email>";
        if (LongDigits().IsMatch(value)) return "<number>";
        if (Phone().IsMatch(value) && value.Count(char.IsDigit) >= 7) return "<phone>";
        return value.Length > 40 ? value[..40] + "..." : value;
    }

    /// <summary>Shape of a value for the memory files: digits become #, letters become a/A. Never leaks the value.</summary>
    public static string Shape(string? value)
    {
        if (value is null) return "null";
        if (value.Length == 0) return "(empty)";
        var chars = value.Take(24).Select(c =>
            // every letter, also those with no case (Chinese, Japanese, Arabic), and every number sign are hidden
            char.IsDigit(c) || char.IsNumber(c) ? '#' : char.IsUpper(c) ? 'A' : char.IsLetter(c) ? 'a' : c).ToArray();
        return new string(chars) + (value.Length > 24 ? "..." : "");
    }
}
