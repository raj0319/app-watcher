using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AppWatcher.Core.Sql;

/// <summary>One data-changing statement the watched app sent to its database.</summary>
public sealed class SqlStatement
{
    public DateTime T { get; set; }
    /// <summary>insert | update | delete | other</summary>
    public string Kind { get; set; } = "other";
    public string Table { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    /// <summary>column -> literal value when the statement carried one (parameters are resolved when the call listed them).</summary>
    public Dictionary<string, string?> Values { get; set; } = new();
    public string Text { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsWrite => Kind is "insert" or "update" or "delete";
}

/// <summary>
/// Parses INSERT / UPDATE / DELETE statements (plain, or wrapped in sp_executesql / sp_prepexec) into
/// table, columns and values. Heuristic but careful about quotes, brackets and nested parentheses.
/// </summary>
public static partial class SqlStatementParser
{
    [GeneratedRegex(@"^\s*(?:exec(?:ute)?\s+)?(?:sp_executesql|sp_prepexec\s+@\w+\s+output\s*,)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ExecSqlPrefix();

    [GeneratedRegex(@"^\s*insert\s+(?:into\s+)?(?<t>[\w\[\]\.""]+)\s*(?:\((?<c>[^)]*)\))?\s*(?:values\s*\((?<v>.*)\)|(?<sel>select\b.*))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Insert();

    [GeneratedRegex(@"^\s*update\s+(?<t>[\w\[\]\.""]+)\s+set\s+(?<s>.*?)(?:\s+(?:where|output)\b.*)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Update();

    [GeneratedRegex(@"^\s*delete\s+(?:from\s+)?(?<t>[\w\[\]\.""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Delete();

    public static SqlStatement? Parse(string sql, DateTime t, string source = "")
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        var text = sql.Trim();
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var m = ExecSqlPrefix().Match(text);
        if (m.Success) text = Unwrap(text[m.Length..], parameters);
        text = StripComments(text).Trim().TrimEnd(';').Trim();
        if (text.Length == 0) return null;

        var st = new SqlStatement { T = t, Text = sql.Length > 2000 ? sql[..2000] : sql, Source = source };
        var ins = Insert().Match(text);
        if (ins.Success)
        {
            st.Kind = "insert";
            st.Table = NormalizeTable(ins.Groups["t"].Value);
            if (ins.Groups["c"].Success) st.Columns = SplitTopLevel(ins.Groups["c"].Value).Select(NormalizeIdentifier).Where(c => c.Length > 0).ToList();
            if (ins.Groups["v"].Success)
            {
                var vals = SplitTopLevel(ins.Groups["v"].Value).Select(v => Literal(v, parameters)).ToList();
                for (var i = 0; i < Math.Min(st.Columns.Count, vals.Count); i++) st.Values[st.Columns[i]] = vals[i];
            }
            return st;
        }
        var upd = Update().Match(text);
        if (upd.Success)
        {
            st.Kind = "update";
            st.Table = NormalizeTable(upd.Groups["t"].Value);
            foreach (var pair in SplitTopLevel(upd.Groups["s"].Value))
            {
                var eq = IndexOfTopLevel(pair, '=');
                if (eq <= 0) continue;
                var col = NormalizeIdentifier(pair[..eq]);
                if (col.Length == 0) continue;
                st.Columns.Add(col);
                st.Values[col] = Literal(pair[(eq + 1)..], parameters);
            }
            return st;
        }
        var del = Delete().Match(text);
        if (del.Success)
        {
            st.Kind = "delete";
            st.Table = NormalizeTable(del.Groups["t"].Value);
            return st;
        }
        st.Kind = text.StartsWith("select", StringComparison.OrdinalIgnoreCase) ? "select" : "other";
        return st;
    }

    /// <summary>sp_executesql N'stmt', N'@p1 int', @p1 = 5  -> stmt, with parameters captured.</summary>
    private static string Unwrap(string rest, Dictionary<string, string?> parameters)
    {
        var parts = SplitTopLevel(rest);
        if (parts.Count == 0) return rest;
        var stmt = Literal(parts[0], parameters) ?? "";
        for (var i = 2; i < parts.Count; i++)
        {
            var eq = IndexOfTopLevel(parts[i], '=');
            if (eq <= 0) continue;
            var name = parts[i][..eq].Trim();
            if (!name.StartsWith('@')) continue;
            parameters[name] = Literal(parts[i][(eq + 1)..], parameters);
        }
        return stmt;
    }

    public static string NormalizeTable(string raw)
    {
        var parts = SplitIdentifierPath(raw).Select(NormalizeIdentifier).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return "";
        if (parts.Count >= 2 && !parts[^2].Equals("dbo", StringComparison.OrdinalIgnoreCase)) return parts[^2] + "." + parts[^1];
        return parts[^1];
    }

    private static IEnumerable<string> SplitIdentifierPath(string raw)
    {
        var sb = new StringBuilder(); var inBracket = false; var inQuote = false;
        foreach (var ch in raw)
        {
            if (ch == '[' && !inQuote) { inBracket = true; continue; }
            if (ch == ']' && inBracket) { inBracket = false; continue; }
            if (ch == '"' && !inBracket) { inQuote = !inQuote; continue; }
            if (ch == '.' && !inBracket && !inQuote) { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(ch);
        }
        yield return sb.ToString();
    }

    public static string NormalizeIdentifier(string raw)
    {
        var s = raw.Trim();
        if (s.Contains('.')) s = SplitIdentifierPath(s).Last();
        return s.Trim().Trim('[', ']', '"', '`').Trim();
    }

    /// <summary>Turns a SQL literal into a comparable string: 'abc' -> abc, N'x''y' -> x'y, 12.50 -> 12.50, NULL -> null, @p -> its value.</summary>
    public static string? Literal(string raw, Dictionary<string, string?>? parameters = null)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.StartsWith('@')) return parameters is not null && parameters.TryGetValue(s, out var v) ? v : s;
        if (s.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return "blob:" + ((s.Length - 2) / 2);
        if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)) return d.ToString("0.############", CultureInfo.InvariantCulture);
        return s; // expression, function call, column reference
    }

    /// <summary>Splits on commas that are not inside quotes, brackets or parentheses.</summary>
    public static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder(); var depth = 0; var inQuote = false; var inBracket = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuote)
            {
                sb.Append(ch);
                if (ch == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; } else inQuote = false; }
                continue;
            }
            if (ch == '\'') { inQuote = true; sb.Append(ch); continue; }
            if (ch == '[') inBracket = true; else if (ch == ']') inBracket = false;
            if (!inBracket)
            {
                if (ch == '(') depth++; else if (ch == ')') depth--;
                if (ch == ',' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    private static int IndexOfTopLevel(string s, char target)
    {
        var depth = 0; var inQuote = false; var inBracket = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuote) { if (ch == '\'') inQuote = false; continue; }
            if (ch == '\'') { inQuote = true; continue; }
            if (ch == '[') inBracket = true; else if (ch == ']') inBracket = false;
            if (inBracket) continue;
            if (ch == '(') depth++; else if (ch == ')') depth--;
            else if (ch == target && depth == 0) return i;
        }
        return -1;
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder(); var inQuote = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuote) { sb.Append(ch); if (ch == '\'') inQuote = false; continue; }
            if (ch == '\'') { inQuote = true; sb.Append(ch); continue; }
            if (ch == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; sb.Append(' '); continue; }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*') { var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? s.Length : end + 1; sb.Append(' '); continue; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
