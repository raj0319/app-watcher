using System.Globalization;
using System.Text;
using Microsoft.SqlServer.XEvent.XELite;

namespace AppWatcher.Core.Sql;

/// <summary>Reads SQL statements from the sources a client can provide. All read-only.</summary>
public static class SqlLogReaders
{
    /// <summary>Reads every file in the folder (or the single file): *.xel, *.csv, SQL.LOG (ODBC trace).</summary>
    public static List<SqlStatement> ReadAll(string? path, string? processName)
    {
        var list = new List<SqlStatement>();
        if (string.IsNullOrWhiteSpace(path)) return list;
        IEnumerable<string> files;
        if (Directory.Exists(path)) files = Directory.EnumerateFiles(path).Where(f => f.EndsWith(".xel", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        else if (File.Exists(path)) files = new[] { path };
        else return list;
        foreach (var f in files.OrderBy(f => f))
        {
            try
            {
                if (f.EndsWith(".xel", StringComparison.OrdinalIgnoreCase)) list.AddRange(ReadXel(f));
                else if (f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) list.AddRange(ReadCsv(f));
                else if (Path.GetFileName(f).Equals("SQL.LOG", StringComparison.OrdinalIgnoreCase) || LooksLikeOdbcTrace(f)) list.AddRange(ReadOdbcTrace(f, processName));
                else list.AddRange(ReadTextLog(f));
            }
            catch (Exception ex) { Log.Warn($"SQL log {f} skipped: {ex.Message}"); }
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    /// <summary>Extended Events file written by scripts/sql/xevents-session.sql.</summary>
    public static List<SqlStatement> ReadXel(string file)
    {
        var list = new List<SqlStatement>();
        var name = Path.GetFileName(file);
        var n = 0;
        var streamer = new XEFileEventStreamer(file);
        streamer.ReadEventStream(evt =>
        {
            n++;
            string? text = null;
            if (evt.Fields.TryGetValue("statement", out var s)) text = s?.ToString();
            if (string.IsNullOrWhiteSpace(text) && evt.Actions.TryGetValue("sql_text", out var a)) text = a?.ToString();
            if (string.IsNullOrWhiteSpace(text) && evt.Fields.TryGetValue("batch_text", out var b)) text = b?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var st = SqlStatementParser.Parse(text, evt.Timestamp.LocalDateTime, $"sql/{name}#{n}");
                if (st is not null) list.Add(st);
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return list;
    }

    /// <summary>CSV with a timestamp column and a statement column (first and last column, header optional).</summary>
    public static List<SqlStatement> ReadCsv(string file)
    {
        var list = new List<SqlStatement>();
        var name = Path.GetFileName(file);
        var n = 0;
        foreach (var row in ParseCsv(File.ReadAllText(file)))
        {
            n++;
            if (row.Count < 2) continue;
            if (!DateTime.TryParse(row[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t))
            {
                if (n == 1) continue; // header
                t = DateTime.MinValue;
            }
            var st = SqlStatementParser.Parse(row[^1], t, $"sql/{name}#{n}");
            if (st is not null) list.Add(st);
        }
        return list;
    }

    /// <summary>
    /// Windows ODBC trace (%TEMP%\SQL.LOG). Every ODBC program on the PC writes here, so only the lines whose
    /// process column equals the watched process name are kept. Statements come from SQLExecDirect and SQLPrepare.
    /// </summary>
    public static List<SqlStatement> ReadOdbcTrace(string file, string? processName)
    {
        var list = new List<SqlStatement>();
        if (string.IsNullOrWhiteSpace(processName)) return list;
        var proc = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        var name = Path.GetFileName(file);
        var n = 0; var inCall = false; var t = File.GetLastWriteTime(file);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            n++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(proc + " ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(proc + "\t", StringComparison.OrdinalIgnoreCase))
            {
                inCall = trimmed.Contains("ENTER SQLExecDirect", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("ENTER SQLPrepare", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (trimmed.Length > 0 && !char.IsWhiteSpace(line[0]) && !trimmed.StartsWith("WCHAR") && !trimmed.StartsWith("UCHAR") && !trimmed.StartsWith("SQL")) inCall = false;
            if (!inCall) continue;
            var q = trimmed.IndexOf('"');
            if (q < 0 || (!trimmed.StartsWith("WCHAR *") && !trimmed.StartsWith("UCHAR *"))) continue;
            var end = trimmed.LastIndexOf('"');
            if (end <= q) continue;
            var st = SqlStatementParser.Parse(trimmed[(q + 1)..end], t, $"sql/{name}#L{n}");
            if (st is not null) list.Add(st);
            inCall = false;
        }
        return list;
    }

    private static bool LooksLikeOdbcTrace(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            for (var i = 0; i < 40 && r.ReadLine() is { } line; i++)
                if (line.Contains("ENTER SQL", StringComparison.OrdinalIgnoreCase) || line.Contains("EXIT  SQL", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Plain text logs of other engines, one statement per line: the MySQL general log
    /// ("2026-01-01T10:00:00.000000Z  12 Query  INSERT ..."), PostgreSQL log_statement
    /// ("2026-01-01 10:00:00.000 UTC [123] LOG:  statement: INSERT ..."), Oracle/UniVerse/other traces
    /// where a line starts with a timestamp and contains INSERT, UPDATE or DELETE. Lines without a timestamp
    /// get the previous line's time.
    /// </summary>
    public static List<SqlStatement> ReadTextLog(string file)
    {
        var list = new List<SqlStatement>();
        var name = Path.GetFileName(file);
        var n = 0; var last = File.GetLastWriteTime(file);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            n++;
            if (line.Length < 12) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)");
            if (m.Success && DateTime.TryParse(m.Groups["ts"].Value.Replace("T", " "), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)) last = t;
            var idx = IndexOfStatement(line);
            if (idx < 0) continue;
            var st = SqlStatementParser.Parse(line[idx..].Trim(), last, $"sql/{name}#L{n}");
            if (st is not null && st.IsWrite) list.Add(st);
        }
        return list;
    }

    private static int IndexOfStatement(string line)
    {
        var best = -1;
        foreach (var kw in new[] { "INSERT ", "UPDATE ", "DELETE ", "insert ", "update ", "delete " })
        {
            var i = line.IndexOf(kw, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best) && (i == 0 || !char.IsLetterOrDigit(line[i - 1]))) best = i;
        }
        return best;
    }

    private static IEnumerable<List<string>> ParseCsv(string text)
    {
        var row = new List<string>(); var sb = new StringBuilder(); var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuote)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else inQuote = false; }
                else sb.Append(ch);
                continue;
            }
            if (ch == '"') { inQuote = true; continue; }
            if (ch == ',') { row.Add(sb.ToString()); sb.Clear(); continue; }
            if (ch == '\r') continue;
            if (ch == '\n') { row.Add(sb.ToString()); sb.Clear(); if (row.Any(c => c.Length > 0)) yield return row; row = new List<string>(); continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); if (row.Any(c => c.Length > 0)) yield return row; }
    }
}
