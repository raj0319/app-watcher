using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Sources;

/// <summary>
/// A folder the client fills with exports of the key tables (nightly CSV or Excel from the app or its
/// database). Each file becomes a table named after the file; the first row is the header. The cheapest way
/// to get a data model when the database itself is off limits.
/// </summary>
public sealed class DropFolderSource : ISnapshotSource
{
    public string Path { get; }
    public string Id => Path;
    public IEnumerable<string> WatchPaths => new[] { Path };
    public int MaxFiles { get; set; } = 200;

    public DropFolderSource(string path) => Path = System.IO.Path.GetFullPath(path);

    public bool? ChangedSince(DateTime t)
    {
        try
        {
            if (!Directory.Exists(Path)) return null;
            return Directory.EnumerateFiles(Path).Any(f => File.GetLastWriteTime(f) > t);
        }
        catch { return null; }
    }

    public ChangeSignal Probe(string? token)
    {
        try
        {
            if (!Directory.Exists(Path)) return new ChangeSignal(null, null, "missing");
            long newest = 0, size = 0; var n = 0;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(Path))
            {
                if (!IsData(f)) continue;
                var fi = new FileInfo(f);
                newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks); size += fi.Length; n++;
            }
            var now = $"{newest}:{size}:{n}";
            return new ChangeSignal(token is null ? null : token != now, null, now);
        }
        catch { return new ChangeSignal(null, null, null); }
    }

    private static bool IsData(string f) => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    public void Snapshot(DataSnapshot into, SnapshotSettings settings, ISet<string>? hotTables, ISet<string>? onlyTables = null)
    {
        var tables = new Dictionary<string, TableSnapshot>();
        into.Databases[Id] = tables;
        if (!Directory.Exists(Path)) { into.Warnings.Add($"not found: {Path}"); return; }
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(Path).Where(IsData).OrderBy(x => x))
        {
            if (++n > MaxFiles) { into.Warnings.Add($"{Path}: more than {MaxFiles} files, rest skipped"); break; }
            var name = System.IO.Path.GetFileNameWithoutExtension(f);
            try
            {
                if (f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (sheet, rows) in XlsxReader.Read(f, settings.MaxRowsPerTable + 1))
                        tables[sheet.Equals("Sheet1", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.{sheet}"] = Build(rows, settings, onlyTables, name);
                }
                else tables[name] = Build(CsvReader.Read(f, settings.MaxRowsPerTable + 1), settings, onlyTables, name);
            }
            catch (Exception ex) { into.Warnings.Add($"{f}: {ex.Message}"); }
        }
    }

    private static TableSnapshot Build(List<string[]> rows, SnapshotSettings settings, ISet<string>? onlyTables, string name)
    {
        var ts = new TableSnapshot();
        if (onlyTables is not null && !onlyTables.Contains(name)) { ts.Skipped = true; ts.Note = "unchanged"; return ts; }
        if (rows.Count == 0) return ts;
        ts.Columns = rows[0].Select((c, i) => c.Trim().Length > 0 ? c.Trim() : $"col{i + 1}").ToList();
        // a column named id / <name>id / code / number in first position is the key; else the whole row
        var first = ts.Columns[0].ToLowerInvariant();
        if (first is "id" or "code" or "number" or "no" or "key" || first.EndsWith("id") || first.EndsWith("no") || first.EndsWith("number") || first.EndsWith("code") || first.EndsWith("key")) ts.PrimaryKey.Add(ts.Columns[0]);
        ts.RowCount = rows.Count - 1;
        if (ts.RowCount > settings.MaxRowsPerTable) { ts.Skipped = true; ts.Note = $"{ts.RowCount} rows is over the row cap"; return ts; }
        foreach (var r in rows.Skip(1))
        {
            var row = new Dictionary<string, string?>(ts.Columns.Count);
            for (var i = 0; i < ts.Columns.Count; i++) row[ts.Columns[i]] = i < r.Length && r[i].Length > 0 ? Norm(r[i]) : null;
            ts.Rows[Values.RowKey(row, ts.PrimaryKey)] = row;
        }
        return ts;
    }

    private static string Norm(string v)
    {
        var t = v.Trim();
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && (t.Contains('.') || t.Contains(','))) return d.ToString("0.############", CultureInfo.InvariantCulture);
        return t;
    }
}

/// <summary>Small CSV/TSV reader: quotes, doubled quotes, comma or semicolon or tab, BOM.</summary>
public static class CsvReader
{
    public static List<string[]> Read(string file, int maxRows = int.MaxValue)
    {
        var text = File.ReadAllText(file, Encoding.UTF8);
        var sep = Separator(text);
        return Parse(text, sep, maxRows);
    }

    public static char Separator(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        var line = head.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var counts = new[] { (',', line.Count(c => c == ',')), (';', line.Count(c => c == ';')), ('\t', line.Count(c => c == '\t')), ('|', line.Count(c => c == '|')) };
        return counts.OrderByDescending(c => c.Item2).First().Item1;
    }

    public static List<string[]> Parse(string text, char sep, int maxRows = int.MaxValue)
    {
        var rows = new List<string[]>();
        var row = new List<string>(); var sb = new StringBuilder(); var inQuote = false;
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        for (var i = 0; i < text.Length && rows.Count < maxRows; i++)
        {
            var ch = text[i];
            if (inQuote)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else inQuote = false; }
                else sb.Append(ch);
                continue;
            }
            if (ch == '"') { inQuote = true; continue; }
            if (ch == sep) { row.Add(sb.ToString()); sb.Clear(); continue; }
            if (ch == '\r') continue;
            if (ch == '\n') { row.Add(sb.ToString()); sb.Clear(); if (row.Any(c => c.Length > 0)) rows.Add(row.ToArray()); row.Clear(); continue; }
            sb.Append(ch);
        }
        if ((sb.Length > 0 || row.Count > 0) && rows.Count < maxRows) { row.Add(sb.ToString()); if (row.Any(c => c.Length > 0)) rows.Add(row.ToArray()); }
        return rows;
    }
}

/// <summary>Minimal .xlsx reader (shared strings + sheet cells), enough for exported tables. No formulas evaluated; cached values are used.</summary>
public static class XlsxReader
{
    public static List<(string sheet, List<string[]> rows)> Read(string file, int maxRows = int.MaxValue)
    {
        var result = new List<(string, List<string[]>)>();
        using var zip = ZipFile.OpenRead(file);
        var shared = new List<string>();
        var ss = zip.GetEntry("xl/sharedStrings.xml");
        if (ss is not null)
        {
            using var s = ss.Open();
            var doc = new XmlDocument(); doc.Load(s);
            var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            foreach (XmlNode si in doc.SelectNodes("//m:si", ns)!) shared.Add(string.Concat(si.SelectNodes(".//m:t", ns)!.Cast<XmlNode>().Select(t => t.InnerText)));
        }
        var names = new Dictionary<string, string>();
        var wb = zip.GetEntry("xl/workbook.xml");
        if (wb is not null)
        {
            using var s = wb.Open();
            var doc = new XmlDocument(); doc.Load(s);
            var ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("m", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            var i = 0;
            foreach (XmlNode sh in doc.SelectNodes("//m:sheet", ns)!) names[$"xl/worksheets/sheet{++i}.xml"] = sh.Attributes?["name"]?.Value ?? $"Sheet{i}";
        }
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName))
        {
            var rows = new List<string[]>();
            using var s = entry.Open();
            using var reader = XmlReader.Create(s);
            List<(int col, string val)>? cur = null;
            string? type = null; var col = 0;
            while (reader.Read() && rows.Count < maxRows)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row") cur = new();
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                {
                    type = reader.GetAttribute("t");
                    col = ColumnIndex(reader.GetAttribute("r") ?? "");
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v" && cur is not null)
                {
                    var v = reader.ReadElementContentAsString();
                    if (type == "s" && int.TryParse(v, out var idx) && idx < shared.Count) v = shared[idx];
                    cur.Add((col, v));
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && type == "inlineStr" && cur is not null) cur.Add((col, reader.ReadElementContentAsString()));
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && cur is not null)
                {
                    if (cur.Count > 0)
                    {
                        var width = cur.Max(c => c.col) + 1;
                        var arr = new string[width];
                        Array.Fill(arr, "");
                        foreach (var (c, v) in cur) if (c >= 0 && c < width) arr[c] = v;
                        rows.Add(arr);
                    }
                    cur = null;
                }
            }
            result.Add((names.GetValueOrDefault(entry.FullName, System.IO.Path.GetFileNameWithoutExtension(entry.FullName)), rows));
        }
        return result;
    }

    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef) { if (char.IsLetter(ch)) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); else break; }
        return Math.Max(0, n - 1);
    }
}
