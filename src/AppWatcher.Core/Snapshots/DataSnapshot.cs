using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppWatcher.Core.Snapshots;

/// <summary>All rows of one table at one moment. Values are normalized to strings so every engine looks the same.</summary>
public sealed class TableSnapshot
{
    public List<string> Columns { get; set; } = new();
    public List<string> PrimaryKey { get; set; } = new();
    /// <summary>key -> column -> value. Key = PK values joined by "|", or an MD5 of the row when there is no PK.</summary>
    public Dictionary<string, Dictionary<string, string?>> Rows { get; set; } = new();
    public long RowCount { get; set; }
    /// <summary>True when the table was bigger than the row cap and was skipped.</summary>
    public bool Skipped { get; set; }
    public string? Note { get; set; }
}

public sealed class FileEntry
{
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime Modified { get; set; }
}

/// <summary>Same shape as app-twin-kit/scripts/snapshot_diff.py: {"sqlite": {db: {table: ...}}, "files": {path: ...}}.</summary>
public sealed class DataSnapshot
{
    public DateTime Taken { get; set; } = DateTime.Now;
    /// <summary>source id (file path or "sqlserver:label") -> table name -> table.</summary>
    public Dictionary<string, Dictionary<string, TableSnapshot>> Databases { get; set; } = new();
    public Dictionary<string, FileEntry> Files { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            JsonSerializer.Serialize(fs, this, Opts);
        File.Move(tmp, path, overwrite: true);
    }

    public static DataSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<DataSnapshot>(fs, Opts);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load snapshot " + path, ex);
            return null;
        }
    }

    public int TableCount => Databases.Sum(d => d.Value.Count);
    public long RowTotal => Databases.Sum(d => d.Value.Sum(t => (long)t.Value.Rows.Count));
}

public static class Values
{
    /// <summary>Normalizes any ADO value to a comparable string.</summary>
    public static string? Normalize(object? v)
    {
        switch (v)
        {
            case null or DBNull: return null;
            case string s: return s;
            case byte[] b: return "blob:" + b.Length + ":" + Convert.ToHexString(SHA1.HashData(b))[..8];
            case bool bo: return bo ? "true" : "false";
            case DateTime dt: return dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case float f: return f.ToString("0.############", CultureInfo.InvariantCulture);
            case double d: return d.ToString("0.############", CultureInfo.InvariantCulture);
            case decimal m: return m.ToString("0.############", CultureInfo.InvariantCulture);
            case Guid g: return g.ToString();
            case IFormattable fo: return fo.ToString(null, CultureInfo.InvariantCulture);
            default: return v.ToString();
        }
    }

    public static string RowKey(Dictionary<string, string?> row, List<string> pk)
    {
        if (pk.Count > 0) return string.Join("|", pk.Select(c => row.TryGetValue(c, out var v) ? v ?? "None" : "None"));
        var json = JsonSerializer.Serialize(row.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static bool TryDecimal(string? s, out decimal d)
    {
        d = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim().Replace(",", "");
        if (t.StartsWith('$') || t.StartsWith('€') || t.StartsWith('£')) t = t[1..];
        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d);
    }

    /// <summary>True when two strings mean the same value: trimmed, case-insensitive, numeric-aware, date-aware.</summary>
    public static bool SameValue(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var ta = a.Trim(); var tb = b.Trim();
        if (string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase)) return true;
        if (TryDecimal(ta, out var da) && TryDecimal(tb, out var db)) return da == db;
        if (DateTime.TryParse(ta, CultureInfo.InvariantCulture, DateTimeStyles.None, out var xa) &&
            DateTime.TryParse(tb, CultureInfo.InvariantCulture, DateTimeStyles.None, out var xb)) return xa == xb;
        return false;
    }
}
