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

    public static bool IsSensitiveName(string? name) => !string.IsNullOrEmpty(name) && SensitiveName().IsMatch(name);

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
            char.IsDigit(c) ? '#' : char.IsUpper(c) ? 'A' : char.IsLower(c) ? 'a' : c).ToArray();
        return new string(chars) + (value.Length > 24 ? "..." : "");
    }
}
