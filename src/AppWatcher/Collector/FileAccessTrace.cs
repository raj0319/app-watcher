using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppWatcher.Collector;

/// <summary>
/// Finds the files the watched app reads and writes by listening to kernel file I/O events (ETW) for a
/// few minutes, filtered to ONE process id. Needs administrator rights once; the Settings window starts
/// this in an elevated copy of AppWatcher.exe and reads the result file. Events of other processes are
/// dropped inside the callback and never stored.
/// </summary>
internal static class FileAccessTrace
{
    public sealed class Entry { public int Reads { get; set; } public int Writes { get; set; } public int Creates { get; set; } }

    public static int Run(int pid, int seconds, string outFile)
    {
        if (!TraceEventSession.IsElevated() ?? false) { Console.Error.WriteLine("needs administrator rights"); return 3; }
        var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        using var session = new TraceEventSession("AppWatcherFileTrace") { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.DiskFileIO);
        Entry Get(string name)
        {
            if (!files.TryGetValue(name, out var e)) files[name] = e = new Entry();
            return e;
        }
        // the ONLY filter that matters: events of any other process are dropped here and never stored
        bool Wanted(TraceEvent d, string? name) => d.ProcessID == pid && !string.IsNullOrEmpty(name);
        session.Source.Kernel.FileIORead += d => { if (Wanted(d, d.FileName)) Get(d.FileName).Reads++; };
        session.Source.Kernel.FileIOWrite += d => { if (Wanted(d, d.FileName)) Get(d.FileName).Writes++; };
        session.Source.Kernel.FileIOCreate += d => { if (Wanted(d, d.FileName)) Get(d.FileName).Creates++; };
        var timer = new System.Threading.Timer(_ => session.Stop(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        session.Source.Process();
        timer.Dispose();
        var result = files
            .Where(f => !IsSystemPath(f.Key))
            .OrderByDescending(f => f.Value.Writes).ThenByDescending(f => f.Value.Reads)
            .Take(500)
            .ToDictionary(f => f.Key, f => f.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{result.Count} files written to {outFile}");
        return 0;
    }

    private static bool IsSystemPath(string p)
    {
        var l = p.ToLowerInvariant();
        return l.Contains(@"\windows\") || l.Contains(@"\$") || l.EndsWith(".dll") || l.EndsWith(".exe") || l.EndsWith(".mui") || l.EndsWith(".pf") || l.Contains(@"\appwatcher\");
    }

    public static Dictionary<string, Entry> Load(string file)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new(); } catch { return new(); }
    }
}
