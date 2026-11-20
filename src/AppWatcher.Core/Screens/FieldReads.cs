namespace AppWatcher.Core.Screens;

/// <summary>One on-screen field with its value at one moment: an editor, a tick box or a grid cell.</summary>
public sealed class FieldRead
{
    /// <summary>Stable key within the document: automation id, else label plus position; grid cells add the row.</summary>
    public string Key { get; set; } = "";
    /// <summary>The name a person uses: the control's Name, its LabeledBy, the nearest Text, or the column header for a cell.</summary>
    public string Label { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsPassword { get; set; }
    public bool IsCell { get; set; }
    /// <summary>The control's screen rectangle ("x,y wxh"), so the focused cell can be matched by place when it has no name.</summary>
    public string Position { get; set; } = "";
    /// <summary>For a grid cell: its row ("line 2" by the line number cell, else "row 0" by order); empty for a field.</summary>
    public string Row { get; set; } = "";
}

/// <summary>
/// Values the program fills in without telling UI Automation (Infragistics grid cells and bound editors raise no
/// ValueProperty change): the collector reads the on-screen fields of the focused document once after each user value
/// and press, and the difference with the previous read is what the program filled in. Pure functions over control rows.
/// </summary>
public static class FieldReads
{
    public const int MaxControls = 300;
    private static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase) { "Edit", "ComboBox", "CheckBox", "Spinner", "Document", "Text" };
    private static readonly HashSet<string> GridTypes = new(StringComparer.OrdinalIgnoreCase) { "DataGrid", "Table", "List", "Tree" };

    /// <summary>
    /// The fields of a tree whose rows carry values (<see cref="ControlRow.Value"/>): editors with their labels, and grid cells
    /// named by their column header (a row index keeps cells of different rows apart). Wrapper editors fold into their outer
    /// control. At most <see cref="MaxControls"/> fields. Text rows are only kept when they carry a value (a bound label).
    /// </summary>
    public static List<FieldRead> FromRows(IList<ControlRow> rows)
    {
        var result = new List<FieldRead>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new ControlRow?[64];
        var gridDepth = -1; string gridName = ""; var rowIndex = -1; var rowDepth = -1;
        List<(string name, int x, int w)> headers = new();
        // the cells of the grid row being read: keyed once the row is complete, by its line number when it has one (rows shift when a line is added)
        var rowCells = new List<FieldRead>();
        void FlushRow()
        {
            if (rowCells.Count == 0) return;
            var line = rowCells.FirstOrDefault(c => LineHeaders.Contains(c.Label) && c.Value.Trim().Length > 0 && c.Value.Trim().All(char.IsDigit))?.Value.Trim();
            var rowKey = line is not null ? "line " + line : "row " + Math.Max(0, rowIndex);
            foreach (var c in rowCells)
            {
                c.Key = $"{gridName}\u0001{c.Label}\u0001{rowKey}";
                c.Row = rowKey;
                if (seenKeys.Add(c.Key) && result.Count < MaxControls) result.Add(c);
            }
            rowCells.Clear();
        }
        for (var i = 0; i < rows.Count && result.Count < MaxControls; i++)
        {
            var r = rows[i];
            if (r.Depth < ancestors.Length) ancestors[r.Depth] = r;
            if (gridDepth >= 0 && r.Depth <= gridDepth) { FlushRow(); gridDepth = -1; headers.Clear(); rowIndex = -1; }
            if (GridTypes.Contains(r.Type) || r.Type == "Custom" && r.AutomationId.StartsWith("grid", StringComparison.OrdinalIgnoreCase))
            {
                gridDepth = r.Depth; gridName = r.Name.Length > 0 ? r.Name : Labels.Clean(r.AutomationId); rowIndex = -1; headers.Clear();
                continue;
            }
            if (gridDepth >= 0)
            {
                if (r.Type is "HeaderItem" or "Header" && r.Name.Length > 0 && Labels.TryRect(r.Position, out var hx, out _, out var hw, out _)) { headers.Add((r.Name.Trim(), hx, hw)); continue; }
                if (r.Type is "DataItem" or "ListItem" or "TreeItem" or "Custom" && r.Depth == gridDepth + 1 || r.Type is "DataItem" && rowIndex < 0) { FlushRow(); rowIndex++; rowDepth = r.Depth; }
                if (r.Value is null || !(r.Type is "Edit" or "Text" or "ComboBox" or "CheckBox" or "DataItem" or "Custom")) continue;
                if (r.Type is "Custom" or "DataItem" && r.Value.Length == 0) continue;
                var header = CellHeader(r, headers);
                if (header.Length == 0) continue;
                if (rowCells.Any(c => c.Label == header)) continue;
                rowCells.Add(new FieldRead { Label = header, AutomationId = Labels.IsWrapperId(r.AutomationId) ? "" : r.AutomationId, Type = r.Type, Value = r.Value, IsPassword = r.IsPassword, IsCell = true, Position = r.Position });
                continue;
            }
            if (r.Value is null || !FieldTypes.Contains(r.Type)) continue;
            if (r.Type == "Text" && r.Name.Length > 0 && r.Value == r.Name) continue; // a plain label
            // an inner editor of a wrapper: the outer control carries the identity
            ControlRow? outer = null;
            if (Labels.IsWrapperId(r.AutomationId) || r.AutomationId.Length == 0)
                for (var d = r.Depth - 1; d >= Math.Max(0, r.Depth - 2); d--)
                    if (ancestors[d] is { } a && FieldTypes.Contains(a.Type) && a.AutomationId.Length > 0 && !Labels.IsWrapperId(a.AutomationId)) { outer = a; break; }
            var id = outer?.AutomationId ?? r.AutomationId;
            var labelRow = outer ?? r;
            var label = labelRow.LabeledBy.Length > 0 ? labelRow.LabeledBy : Labels.Resolve(labelRow, rows);
            if (label.Length == 0) label = Labels.Clean(id);
            if (label.Length == 0 || Labels.IsWrapperId(label)) continue;
            var fkey = id.Length > 0 && !Labels.IsWrapperId(id) ? id : label + "\u0001" + labelRow.Position;
            if (!seenKeys.Add(fkey))
            {
                // the inner editor's value wins over the wrapper's (the wrapper often reports an empty value)
                var existing = result.First(x => x.Key == fkey);
                if (existing.Value.Length == 0 && r.Value.Length > 0) existing.Value = r.Value;
                continue;
            }
            result.Add(new FieldRead { Key = fkey, Label = label, AutomationId = id, Type = labelRow.Type, Value = r.Value, IsPassword = r.IsPassword || labelRow.IsPassword, Position = labelRow.Position });
        }
        FlushRow();
        return result;
    }

    /// <summary>Column headers that number the lines of an order grid: a row keyed by its line number keeps its identity when a line is added above it.</summary>
    private static readonly HashSet<string> LineHeaders = new(StringComparer.OrdinalIgnoreCase) { "Ln", "Line", "Line #", "Seq", "#", "No", "No.", "Item" };

    /// <summary>The cell of a document that lies under the given rectangle (the editor that had the focus), by place: its key, or null.</summary>
    public static FieldRead? AtRect(IReadOnlyList<FieldRead> reads, string position)
    {
        if (!Labels.TryRect(position, out var x, out var y, out var w, out var h) || w <= 0 || h <= 0) return null;
        var cx = x + w / 2; var cy = y + h / 2;
        FieldRead? best = null; long bestArea = long.MaxValue;
        foreach (var f in reads)
        {
            if (!Labels.TryRect(f.Position, out var fx, out var fy, out var fw, out var fh) || fw <= 0 || fh <= 0) continue;
            if (cx < fx || cx > fx + fw || cy < fy || cy > fy + fh) continue;
            var area = (long)fw * fh;
            if (area < bestArea) { best = f; bestArea = area; }
        }
        return best;
    }

    /// <summary>A cell's column: its own Name when it is a header-like word, else the header whose x-range holds the cell's centre.</summary>
    private static string CellHeader(ControlRow cell, List<(string name, int x, int w)> headers)
    {
        var own = cell.Name.Replace("\r", " ").Replace("\n", " ").Trim();
        if (own.Length > 0 && own.Length <= 40 && own != cell.Value && headers.Any(h => h.name == own)) return own;
        if (Labels.TryRect(cell.Position, out var x, out _, out var w, out _) && w > 0)
        {
            var cx = x + w / 2;
            foreach (var h in headers) if (cx >= h.x && cx <= h.x + h.w) return h.name;
        }
        return own.Length > 0 && own.Length <= 40 && own != cell.Value && !own.All(c => char.IsDigit(c) || c is '.' or ',' or '-' or '/' or ' ') ? own : "";
    }

    /// <summary>
    /// What the program filled in between two reads: fields whose value changed and that the user did not type into
    /// (<paramref name="userKeys"/>: the fields the user's own value events named, by automation id or label). New fields
    /// with a value count too (a line the program added). Fields that went empty are left out: clearing is not filling.
    /// </summary>
    public static List<FieldRead> Filled(IReadOnlyList<FieldRead> before, IReadOnlyList<FieldRead> after, ISet<string> userKeys)
    {
        var prev = before.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var list = new List<FieldRead>();
        foreach (var f in after)
        {
            if (f.Value.Trim().Length == 0) continue;
            if (prev.TryGetValue(f.Key, out var old) && old == f.Value) continue;
            if (!prev.ContainsKey(f.Key) && before.Count == 0) continue; // the first read is the baseline
            // the field the user typed into is named by its label or its own id; a wrapper id ("_EmbeddableTextBox") is shared by every cell and names nothing
            if (f.AutomationId.Length > 0 && !Labels.IsWrapperId(f.AutomationId) && (userKeys.Contains(f.AutomationId) || userKeys.Contains(Labels.Clean(f.AutomationId)))) continue;
            if (userKeys.Contains(f.Label)) continue;
            list.Add(f);
        }
        return list;
    }

    /// <summary>
    /// The difference between two reads split by who did it: the changed fields the user had the focus in
    /// (<paramref name="typedKeys"/>: labels or automation ids of the field that has, or just had, the focus) are typed values;
    /// every other changed field is one the program filled in. Grid cells that report no change to UI Automation are caught
    /// this way: the cell that had the focus when the user moved on is the typed one.
    /// </summary>
    public static (List<FieldRead> typed, List<FieldRead> filled) Diff(IReadOnlyList<FieldRead> before, IReadOnlyList<FieldRead> after, ISet<string> typedKeys)
    {
        var prev = before.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var typed = new List<FieldRead>(); var filled = new List<FieldRead>();
        if (before.Count == 0) return (typed, filled); // the first read is the baseline
        foreach (var f in after)
        {
            if (prev.TryGetValue(f.Key, out var old) && old == f.Value) continue;
            var mine = f.AutomationId.Length > 0 && !Labels.IsWrapperId(f.AutomationId) && (typedKeys.Contains(f.AutomationId) || typedKeys.Contains(Labels.Clean(f.AutomationId)))
                       || typedKeys.Contains(f.Label);
            if (mine) { if (f.Value.Trim().Length > 0 || old is { Length: > 0 }) typed.Add(f); }
            else if (f.Value.Trim().Length > 0) filled.Add(f); // clearing is not filling
        }
        return (typed, filled);
    }

    /// <summary>
    /// A lookup list that opened inside the window under the field the user is typing in: a grid or list that is on screen now,
    /// was not before (by identity, not by size: a grid that grew a row is the same grid), sits below the editor and overlaps
    /// its columns, and does not hold the editor itself (a grid cell's own grid is never its lookup). Returns its name and first rows (header first).
    /// </summary>
    public static (string grid, List<string> lines)? NewLookup(IList<ControlRow> before, IList<ControlRow> after, string editorPosition, int maxRows = 10)
    {
        if (!Labels.TryRect(editorPosition, out var ex, out var ey, out var ew, out var eh) || ew <= 0) return null;
        static string Identity(ControlRow r) => r.Type + "\u0001" + r.AutomationId + "\u0001" + r.Name + (r.AutomationId.Length == 0 && r.Name.Length == 0 ? "\u0001" + r.Position : "");
        // a list or grid that was in the previous read of this window is not a lookup that opened, whatever rows it shows now
        // (a record loaded by a code refills grids that were there; the product lookup stays on screen while a cell is typed in)
        var old = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < before.Count; i++) if (GridTypes.Contains(before[i].Type)) old.Add(Identity(before[i]));
        for (var i = 0; i < after.Count; i++)
        {
            var g = after[i];
            if (!GridTypes.Contains(g.Type)) continue;
            if (old.Contains(Identity(g))) continue;
            if (!Labels.TryRect(g.Position, out var gx, out var gy, out var gw, out var gh) || gw <= 0 || gh <= 0) continue;
            var holdsEditor = ex >= gx && ey >= gy && ex + ew <= gx + gw && ey + eh <= gy + gh;
            if (holdsEditor) continue;
            if (!UnderOrBeside(ex, ey, ew, eh, gx, gy, gw, gh)) continue;
            var lines = GridLines(after, i, maxRows);
            if (lines.Count <= 1) continue;
            var name = g.Name.Length > 0 ? g.Name : Labels.Clean(g.AutomationId);
            return (name.Length == 0 ? "lookup" : name, lines);
        }
        return null;
    }

    /// <summary>The most a lookup list may start away from the field it serves (below it, or beside it).</summary>
    public const int LookupGapPx = 40;

    /// <summary>
    /// A list starts under the field (its top within <see cref="LookupGapPx"/> below the field's bottom, overlapping its columns)
    /// or beside it (its left within <see cref="LookupGapPx"/> of the field's right edge, overlapping its rows).
    /// </summary>
    public static bool UnderOrBeside(int ex, int ey, int ew, int eh, int gx, int gy, int gw, int gh)
    {
        var overlapsX = gx < ex + ew && gx + gw > ex;
        var overlapsY = gy < ey + eh && gy + gh > ey;
        var below = gy >= ey + eh - 4 && gy - (ey + eh) <= LookupGapPx;
        var beside = gx >= ex + ew - 4 && gx - (ex + ew) <= LookupGapPx;
        return overlapsX && below || overlapsY && beside;
    }

    /// <summary>A popup window (a top window of its own) is a lookup for the field when it starts under or beside it.</summary>
    public static bool PopupServesField(string fieldPosition, string popupPosition)
    {
        if (!Labels.TryRect(fieldPosition, out var ex, out var ey, out var ew, out var eh) || ew <= 0) return false;
        if (!Labels.TryRect(popupPosition, out var gx, out var gy, out var gw, out var gh) || gw <= 0 || gh <= 0) return false;
        return UnderOrBeside(ex, ey, ew, eh, gx, gy, gw, gh);
    }

    /// <summary>The cells of one grid row of a read, by row key.</summary>
    public static List<FieldRead> RowCells(IReadOnlyList<FieldRead> reads, string row) => row.Length == 0 ? new() : reads.Where(f => f.IsCell && f.Row == row).ToList();

    /// <summary>
    /// One grid row read on its own (the row under the cell the user typed in, read by place after the focus moved to another
    /// line): the grid row and its column headers from the last full read of the document, then the row's own subtree.
    /// The cells come out keyed exactly as in a full read (by the line number cell when there is one).
    /// </summary>
    public static List<FieldRead> RowFromSubtree(IList<ControlRow> lastFullRead, string anyCellPosition, IList<ControlRow> rowSubtree)
    {
        if (rowSubtree.Count == 0 || !Labels.TryRect(anyCellPosition, out var cx, out var cy, out var cw, out var ch)) return new();
        var gi = -1;
        for (var i = 0; i < lastFullRead.Count; i++)
        {
            var r = lastFullRead[i];
            if (!(GridTypes.Contains(r.Type) || r.Type == "Custom" && r.AutomationId.StartsWith("grid", StringComparison.OrdinalIgnoreCase))) continue;
            if (Labels.TryRect(r.Position, out var gx, out var gy, out var gw, out var gh) && cx + cw / 2 >= gx && cx + cw / 2 <= gx + gw && cy + ch / 2 >= gy && cy + ch / 2 <= gy + gh) gi = i;
        }
        if (gi < 0) return new();
        var grid = lastFullRead[gi];
        var synthetic = new List<ControlRow> { grid };
        for (var j = gi + 1; j < lastFullRead.Count && lastFullRead[j].Depth > grid.Depth; j++)
            if (lastFullRead[j].Type is "HeaderItem" or "Header") synthetic.Add(lastFullRead[j]);
        var top = rowSubtree.Min(r => r.Depth);
        foreach (var r in rowSubtree)
            synthetic.Add(new ControlRow { Depth = grid.Depth + 1 + (r.Depth - top), Type = r.Type, Name = r.Name, AutomationId = r.AutomationId, Position = r.Position, IsPassword = r.IsPassword, Value = r.Value });
        return FromRows(synthetic).Where(f => f.IsCell).ToList();
    }

    /// <summary>
    /// The difference inside one grid row between a snapshot and a later read of it: the typed column's change is the user's,
    /// every other changed cell is the program's (a price, an extension). Cells that went empty are left out.
    /// </summary>
    public static (List<FieldRead> typed, List<FieldRead> filled) RowDiff(IReadOnlyList<FieldRead> before, IReadOnlyList<FieldRead> after, string typedColumn)
    {
        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in before) prev.TryAdd(f.Label, f.Value);
        var typed = new List<FieldRead>(); var filled = new List<FieldRead>();
        foreach (var f in after)
        {
            if (prev.TryGetValue(f.Label, out var old) && old == f.Value) continue;
            if (f.Value.Trim().Length == 0) continue;
            if (f.Label == typedColumn) typed.Add(f); else filled.Add(f);
        }
        return (typed, filled);
    }

    /// <summary>Header line and first rows of the grid at <paramref name="index"/>, cells joined by " | ".</summary>
    public static List<string> GridLines(IList<ControlRow> rows, int index, int maxRows)
    {
        var depth = rows[index].Depth;
        var headers = new List<string>(); var lines = new List<string>();
        for (var j = index + 1; j < rows.Count && rows[j].Depth > depth; j++)
        {
            var r = rows[j];
            if (r.Type is "HeaderItem" or "Header" && r.Name.Length > 0) { headers.Add(r.Name.Trim()); continue; }
            if (r.Type is "DataItem" or "ListItem" or "TreeItem")
            {
                var cells = new List<string>();
                var k = j + 1;
                for (; k < rows.Count && rows[k].Depth > r.Depth; k++)
                {
                    var c = rows[k];
                    var text = c.Value is { Length: > 0 } ? c.Value : c.Name;
                    if (c.Depth == r.Depth + 1 && text.Length > 0 && c.Type != "Note") cells.Add(text);
                }
                lines.Add(cells.Count > 0 ? string.Join(" | ", cells) : (r.Value is { Length: > 0 } ? r.Value : r.Name));
                j = k - 1;
                if (lines.Count >= maxRows) break;
            }
        }
        var all = new List<string> { headers.Count > 0 ? string.Join(" | ", headers) : string.Join(" | ", Enumerable.Range(1, Math.Max(1, lines.Count == 0 ? 1 : lines[0].Split(" | ").Length)).Select(n => "column " + n)) };
        all.AddRange(lines.Select(l => l.Length > 400 ? l[..400] : l));
        return all;
    }
}
