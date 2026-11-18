using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppWatcher.Core.Screens;

/// <summary>
/// Pictures of the watched app's own window, one per distinct STATE of a screen (after a press, a tab switch,
/// a typed value, a dialog, a dropdown), filed page-wise: screens/shots/&lt;screen&gt;/NN-&lt;what&gt;.png with an
/// index.md next to them. The same state is never stored twice: a coarse hash of the picture (small, grey,
/// 16 levels, so a blinking caret or a clock does not count) decides. Caps per screen and per app keep the disk safe.
/// </summary>
public sealed class ShotStore
{
    public sealed class Entry { public int N { get; set; } public string File { get; set; } = ""; public string Label { get; set; } = ""; public string Hash { get; set; } = ""; public DateTime T { get; set; } public string Event { get; set; } = ""; public long Bytes { get; set; } }
    private sealed class Index { public List<Entry> Entries { get; set; } = new(); }

    private readonly string _root;
    private readonly object _gate = new();
    private long _totalBytes = -1;

    public int MaxPerScreen { get; set; } = 25;
    public long MaxTotalBytes { get; set; } = 500L * 1024 * 1024;

    public ShotStore(string screensDir) => _root = Path.Combine(screensDir, "shots");

    public string Root => _root;

    /// <summary>Folder of one screen's pictures, relative to the screens folder ("shots/&lt;screen&gt;").</summary>
    public static string FolderFor(string screenTitle) => "shots/" + Paths.SafeName(Learning.Titles.Key(screenTitle));

    /// <summary>
    /// Stores the picture unless an identical state is already there or a cap is hit. Returns the path relative to the
    /// screens folder ("shots/&lt;screen&gt;/03-after-press-save.png") or null when nothing was stored and why in <paramref name="why"/>.
    /// </summary>
    public string? Add(string screenTitle, string label, byte[] png, string hash, DateTime t, string eventSource, out string why)
    {
        why = "";
        lock (_gate)
        {
            var folderRel = FolderFor(screenTitle);
            var folder = Path.Combine(_root, Paths.SafeName(Learning.Titles.Key(screenTitle)));
            Directory.CreateDirectory(folder);
            var index = Load(folder);
            if (index.Entries.Any(e => e.Hash == hash)) { why = "same state already pictured"; return null; }
            if (index.Entries.Count >= MaxPerScreen) { why = $"cap of {MaxPerScreen} pictures for this screen reached"; return null; }
            if (TotalBytes() + png.Length > MaxTotalBytes) { why = "picture folder size cap reached"; return null; }
            var n = index.Entries.Count == 0 ? 1 : index.Entries.Max(e => e.N) + 1;
            var file = $"{n:00}-{Paths.SafeName(label, 50)}.png";
            File.WriteAllBytes(Path.Combine(folder, file), png);
            index.Entries.Add(new Entry { N = n, File = file, Label = label, Hash = hash, T = t, Event = eventSource, Bytes = png.Length });
            _totalBytes = TotalBytes() + png.Length;
            Save(folder, screenTitle, index);
            return folderRel + "/" + file;
        }
    }

    public IReadOnlyList<Entry> Entries(string screenTitle)
    {
        var folder = Path.Combine(_root, Paths.SafeName(Learning.Titles.Key(screenTitle)));
        return Directory.Exists(folder) ? Load(folder).Entries : new List<Entry>();
    }

    private long TotalBytes()
    {
        if (_totalBytes >= 0) return _totalBytes;
        try { _totalBytes = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.png", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
        catch { _totalBytes = 0; }
        return _totalBytes;
    }

    private static Index Load(string folder)
    {
        try
        {
            var p = Path.Combine(folder, "index.json");
            if (File.Exists(p)) return JsonSerializer.Deserialize<Index>(File.ReadAllText(p)) ?? new Index();
        }
        catch { }
        return new Index();
    }

    private static void Save(string folder, string screenTitle, Index index)
    {
        File.WriteAllText(Path.Combine(folder, "index.json"), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        var sb = new StringBuilder();
        sb.AppendLine($"# Pictures of: {Learning.Titles.Key(screenTitle)}");
        sb.AppendLine();
        sb.AppendLine("One picture per distinct state of this screen (the app's window only). The picture before an action is the previous row.");
        sb.AppendLine();
        sb.AppendLine("| # | Picture | State | When | Event |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in index.Entries.OrderBy(e => e.N)) sb.AppendLine($"| {e.N} | {e.File} | {e.Label} | {e.T:yyyy-MM-dd HH:mm:ss} | {e.Event} |");
        File.WriteAllText(Path.Combine(folder, "index.md"), sb.ToString());
    }

    /// <summary>
    /// Coarse hash of a picture for "same state" checks: the image is reduced to a small grey grid with 16 levels and
    /// hashed, so a caret, a clock or anti-aliasing noise does not make a new state but a changed field or row does.
    /// Takes the pixel grid already reduced by the caller (width*height bytes, 0-255).
    /// </summary>
    public static string CoarseHash(byte[] greyGrid)
    {
        var q = new byte[greyGrid.Length];
        for (var i = 0; i < greyGrid.Length; i++) q[i] = (byte)(greyGrid[i] >> 4);
        return Convert.ToHexString(SHA1.HashData(q))[..20].ToLowerInvariant();
    }
}
