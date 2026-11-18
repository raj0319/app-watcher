using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AppWatcher.Core.Screens;

namespace AppWatcher.Core;

/// <summary>
/// Folds another PC's data folder (or its zip) into this one so one memory covers the whole office:
/// events are copied as &lt;date&gt;.&lt;machine&gt;.jsonl, effects by file name, screens through the
/// structure-hash index (duplicates merge, titles are added). Nothing in the source is changed.
/// </summary>
public static class Merge
{
    public sealed class Result { public int EventFiles; public int EffectFiles; public int ScreensNew; public int ScreensMerged; public string Machine = ""; }

    public static Result Run(AppPaths into, string source)
    {
        string? temp = null;
        try
        {
            if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                temp = Path.Combine(Path.GetTempPath(), "appwatcher-merge-" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(source, temp);
                source = FindDataRoot(temp) ?? temp;
            }
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            var r = new Result { Machine = MachineTag(source) };
            into.EnsureDirectories();
            var events = Path.Combine(source, "events");
            if (Directory.Exists(events))
                foreach (var f in Directory.GetFiles(events, "*.jsonl"))
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var date = stem.Length >= 10 ? stem[..10] : stem;
                    var dest = Path.Combine(into.Events, $"{date}.{r.Machine}.jsonl");
                    if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(f).Length) continue;
                    File.Copy(f, dest, true);
                    r.EventFiles++;
                }
            var effects = Path.Combine(source, "effects");
            if (Directory.Exists(effects))
                foreach (var f in Directory.GetFiles(effects, "*.json"))
                {
                    var dest = Path.Combine(into.Effects, Path.GetFileName(f));
                    if (File.Exists(dest)) continue;
                    File.Copy(f, dest);
                    r.EffectFiles++;
                }
            var screens = Path.Combine(source, "screens");
            if (Directory.Exists(screens))
                foreach (var f in Directory.GetFiles(screens, "*.md"))
                {
                    var rec = ScreenRecord.Parse(f);
                    if (rec is null) continue;
                    if (ScreenWriter.WriteOnce(into.Screens, rec)) r.ScreensNew++; else r.ScreensMerged++;
                }
            Log.Info($"merged {source}: {r.EventFiles} event files, {r.EffectFiles} effects, {r.ScreensNew} new screens, {r.ScreensMerged} merged");
            return r;
        }
        finally
        {
            if (temp is not null) { try { Directory.Delete(temp, true); } catch { } }
        }
    }

    private static string? FindDataRoot(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "events"))) return dir;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var r = FindDataRoot(d);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>Short tag for the source PC: the machine hash from its start events when present, else a hash of the folder name.</summary>
    public static string MachineTag(string source)
    {
        try
        {
            var events = Path.Combine(source, "events");
            if (Directory.Exists(events))
                foreach (var f in Directory.GetFiles(events, "*.jsonl").OrderBy(x => x))
                    foreach (var line in File.ReadLines(f).Take(5))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"machine:(\w{4,})");
                        if (m.Success) return m.Groups[1].Value;
                    }
        }
        catch { }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(source.TrimEnd('\\', '/')))))[..8].ToLowerInvariant();
    }

    /// <summary>Hash of a name (user or machine) for tagging sessions without storing the name.</summary>
    public static string Tag(string name) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..8].ToLowerInvariant();
}
