using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AppWatcher.Core;
using AppWatcher.Core.Events;
using AppWatcher.Core.Learning;
using Xunit;
using Xunit.Abstractions;

namespace AppWatcher.Tests;

/// <summary>
/// The learner, the merge of several PCs and the retention at office scale, on a synthetic office (OfficeGenerator).
/// The small run is always on; the big one (5 users, 30 working days, 300k-600k events) runs with APPWATCHER_SCALE=1,
/// usually through scripts\scale-test.ps1, and writes its numbers to APPWATCHER_SCALE_REPORT (default: scale-report.txt in the temp folder).
/// </summary>
public class ScaleTests
{
    private readonly ITestOutputHelper _out;
    public ScaleTests(ITestOutputHelper output) => _out = output;

    private static string Strip(string md) => string.Join("\n", md.Split('\n').Where(l => !l.StartsWith("Last learn:")));

    private sealed class Phase
    {
        public double Seconds, LoadSeconds; public double PeakManagedMb; public LearnResult? Result; public int Events;
    }

    /// <summary>Same steps as the tray app's learn run (LearnRunner): load what is newer than the watermark, learn, save state and memory.</summary>
    private static Phase LearnFolder(AppPaths paths, DateTime now, bool rebuild)
    {
        var p = new Phase();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var peak = 0L; var stop = false;
        var sampler = new Thread(() => { while (!Volatile.Read(ref stop)) { peak = Math.Max(peak, GC.GetTotalMemory(false)); Thread.Sleep(40); } }) { IsBackground = true };
        sampler.Start();
        var sw = Stopwatch.StartNew();
        var statePath = Path.Combine(paths.Memory, Learner.StateFile);
        var state = rebuild ? new LearnState() : LearnState.Load(statePath);
        var input = Learner.LoadInput(paths, "office", null, since: state.Watermark?.AddHours(-1), knownEffects: state.EffectFiles);
        p.LoadSeconds = sw.Elapsed.TotalSeconds;
        input.Now = now;
        p.Events = input.Events.Count;
        p.Result = Learner.Learn(input, state, out var committed);
        committed.Save(statePath);
        MemoryWriter.Write(paths.Memory, p.Result);
        sw.Stop();
        Volatile.Write(ref stop, true); sampler.Join();
        peak = Math.Max(peak, GC.GetTotalMemory(false));
        p.Seconds = sw.Elapsed.TotalSeconds; p.PeakManagedMb = peak / 1048576.0;
        return p;
    }

    private static Dictionary<string, string> MemoryOf(AppPaths paths)
        => MemoryWriter.Files.ToDictionary(f => f, f => Strip(File.ReadAllText(Path.Combine(paths.Memory, f))));

    private static List<string> Diff(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Keys.Where(k => a[k] != b[k]).ToList();

    private static string FirstDiff(string a, string b)
    {
        var x = a.Split('\n'); var y = b.Split('\n');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            if (i >= x.Length || i >= y.Length || x[i] != y[i]) return $"line {i + 1}: [{(i < x.Length ? x[i] : "")}] vs [{(i < y.Length ? y[i] : "")}]";
        return "";
    }

    private static string Root(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-scale-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CopyDay(AppPaths from, AppPaths to, Func<string, bool> keep)
    {
        to.EnsureDirectories();
        foreach (var f in Directory.GetFiles(from.Events, "*.jsonl").Where(f => keep(Path.GetFileName(f)))) File.Copy(f, Path.Combine(to.Events, Path.GetFileName(f)), true);
        foreach (var f in Directory.GetFiles(from.Effects, "*.json").Where(f => keep(Path.GetFileName(f)))) File.Copy(f, Path.Combine(to.Effects, Path.GetFileName(f)), true);
    }

    private static string Run(OfficeGenerator gen, bool big, Action<string> log)
    {
        var root = Root(big ? "big" : "small");
        try
        {
            var report = new StringBuilder();
            void Say(string s) { report.AppendLine(s); log(s); }
            var swGen = Stopwatch.StartNew();
            long events = 0;
            for (var u = 0; u < gen.Users; u++) events += gen.WritePc(root, u);
            swGen.Stop();
            var days = gen.WorkDays().ToList();
            var now = days[^1].AddDays(1);
            Say($"office: {gen.Users} users, {days.Count} working days, {gen.Screens.Length} screens + {gen.Dialogs.Length} dialogs, {gen.FieldCount} fields, {events:N0} events ({events / (double)(gen.Users * days.Count):N0} per user-day), {Directory.GetFiles(Path.Combine(gen.FolderFor(root, 0), "effects")).Length * gen.Users:N0} effect files, generated in {swGen.Elapsed.TotalSeconds:0.#} s");

            // 1. the whole office merged into one folder, learned from day one
            var office = new AppPaths(Path.Combine(root, "office"));
            var swMerge = Stopwatch.StartNew();
            for (var u = 0; u < gen.Users; u++) Merge.Run(office, gen.FolderFor(root, u));
            swMerge.Stop();
            File.Copy(new AppPaths(gen.FolderFor(root, 0)).BeforeSnapshot, office.BeforeSnapshot, true);
            var full = LearnFolder(office, now, rebuild: true);
            Say($"merge of {gen.Users} PC folders: {swMerge.Elapsed.TotalSeconds:0.#} s; full rebuild (files just written, cold): {full.Seconds:0.#} s (load {full.LoadSeconds:0.#} s) over {full.Events:N0} events, peak managed {full.PeakManagedMb:0} MB, process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB");
            Assert.Equal(events, (long)full.Events);
            var swRead = Stopwatch.StartNew();
            var warmEvents = EventReader.ReadAll(office.Events).Count; var tEv = swRead.Elapsed.TotalSeconds;
            var warmEffects = AppWatcher.Core.Snapshots.EffectRecord.ReadAll(office.Effects).Count;
            Say($"warm read of the files alone: {warmEvents:N0} events {tEv:0.#} s, {warmEffects:N0} effects {swRead.Elapsed.TotalSeconds - tEv:0.#} s");
            var fullMem = MemoryOf(office);
            var sizes = string.Join(", ", MemoryWriter.Files.Select(f => $"{f} {new FileInfo(Path.Combine(office.Memory, f)).Length / 1024.0:0.#} KB"));
            Say("memory sizes: " + sizes);
            var sessionsSeen = full.Result!.SessionCount;
            Say($"sessions learned: {sessionsSeen}, users counted: {full.Result.Health.UsersSeen}, machines counted: {full.Result.Health.MachinesSeen}");

            // 2. memory holds no raw typed value (text values start with "Zq", codes with K + 6 digits, amounts are plain numbers)
            var leak = Regex.Matches(string.Join("\n", fullMem.Values), @"Zq[a-z]{6}|\bK\d{6}\b").Count;
            Say($"raw typed values found in memory: {leak}");
            Assert.Equal(0, leak);

            // 3. learn twice: from day one again, and an incremental run with nothing new
            var again = LearnFolder(office, now, rebuild: true);
            if (big) Assert.True(again.Seconds < 60 && full.PeakManagedMb < 500, $"rebuild {again.Seconds:0.#} s, peak {full.PeakManagedMb:0} MB");
            Assert.Empty(Diff(fullMem, MemoryOf(office)));
            var idle = LearnFolder(office, now, rebuild: false);
            Assert.Empty(Diff(fullMem, MemoryOf(office)));
            Say($"learn twice identical: yes (second rebuild, files warm in the cache: {again.Seconds:0.#} s, idle incremental {idle.Seconds:0.#} s, {idle.Events:N0} events read)");

            // 4. incremental: everything but the last day, then the last day
            var last = days[^1].ToString("yyyy-MM-dd");
            var lastFx = days[^1].ToString("yyyy-MM-dd");
            var inc = new AppPaths(Path.Combine(root, "inc"));
            CopyDay(office, inc, n => string.CompareOrdinal(n[..10], last) < 0);
            foreach (var f in Directory.GetFiles(office.Screens)) File.Copy(f, Path.Combine(inc.Screens, Path.GetFileName(f)), true);
            File.Copy(new AppPaths(gen.FolderFor(root, 0)).BeforeSnapshot, inc.BeforeSnapshot, true);
            var first = LearnFolder(inc, days[^1], rebuild: true);
            CopyDay(office, inc, n => string.CompareOrdinal(n[..10], last) >= 0);
            var one = LearnFolder(inc, now, rebuild: false);
            if (big) Assert.True(one.Seconds < 5, $"incremental day {one.Seconds:0.#} s");
            Say($"incremental: {days.Count - 1} days {first.Seconds:0.#} s; then the last day ({one.Events:N0} events read) {one.Seconds:0.#} s (load {one.LoadSeconds:0.#} s), peak managed {one.PeakManagedMb:0} MB");
            var incDiff = Diff(fullMem, MemoryOf(inc));
            Say("incremental equals full rebuild: " + (incDiff.Count == 0 ? "yes" : "NO: " + string.Join(", ", incDiff)));
            foreach (var k in incDiff) Say("  " + k + ": " + FirstDiff(fullMem[k], MemoryOf(inc)[k]));
            Assert.Empty(incDiff);

            // 5. retention: the learned old days go, the memory stays
            var wm = LearnState.Load(Path.Combine(office.Memory, Learner.StateFile)).Watermark;
            var before = Directory.GetFiles(office.Events).Length;
            var deleted = Retention.RotateEvents(office, big ? 7 : 1, wm, now);
            var afterRotate = LearnFolder(office, now, rebuild: false);
            static string NoDisk(string s) => string.Join("\n", s.Split('\n').Where(l => !l.StartsWith("| Disk use")));
            var retMem = MemoryOf(office).ToDictionary(kv => kv.Key, kv => NoDisk(kv.Value)); var retRef = fullMem.ToDictionary(kv => kv.Key, kv => NoDisk(kv.Value)); var retDiff = Diff(retRef, retMem);
            foreach (var k in retDiff) Say("  " + k + ": " + FirstDiff(retRef[k], retMem[k]));
            Say($"retention ({(big ? 7 : 1)} days): {deleted.Count} of {before} event files deleted, incremental learn after it {afterRotate.Seconds:0.#} s; memory unchanged (apart from the disk-use line, which shrinks by design): " + (retDiff.Count == 0 ? "yes" : "NO: " + string.Join(", ", retDiff)));
            Assert.True(deleted.Count > 0);
            Assert.Empty(retDiff);

            // 6. merge of 3 PCs against the same events learned in one folder
            var m3 = new AppPaths(Path.Combine(root, "m3"));
            for (var u = 0; u < Math.Min(3, gen.Users); u++) Merge.Run(m3, gen.FolderFor(root, u));
            LearnFolder(m3, now, rebuild: true);
            var one3 = new AppPaths(Path.Combine(root, "one3"));
            one3.EnsureDirectories();
            foreach (var d in days)
            {
                var name = d.ToString("yyyy-MM-dd");
                var lines = new List<(DateTime t, string line)>();
                for (var u = 0; u < Math.Min(3, gen.Users); u++)
                {
                    var f = Path.Combine(gen.FolderFor(root, u), "events", name + ".jsonl");
                    foreach (var l in File.ReadLines(f)) lines.Add((System.Text.Json.JsonSerializer.Deserialize<AppWatcher.Core.Models.UiEvent>(l)!.T, l));
                }
                File.WriteAllLines(Path.Combine(one3.Events, name + ".jsonl"), lines.OrderBy(x => x.t).Select(x => x.line));
            }
            // the naive way: the three PCs' lines in one untagged file per day, effects untagged
            foreach (var f in Directory.GetFiles(m3.Effects)) { var n = Path.GetFileName(f); File.Copy(f, Path.Combine(one3.Effects, n[..23] + ".json"), true); }
            foreach (var f in Directory.GetFiles(m3.Screens)) File.Copy(f, Path.Combine(one3.Screens, Path.GetFileName(f)), true);
            File.Copy(new AppPaths(gen.FolderFor(root, 0)).BeforeSnapshot, one3.BeforeSnapshot, true);
            File.Copy(new AppPaths(gen.FolderFor(root, 0)).BeforeSnapshot, m3.BeforeSnapshot, true);
            var m3Run = LearnFolder(m3, now, rebuild: true);
            var one3Run = LearnFolder(one3, now, rebuild: true);
            var m3Mem = MemoryOf(m3);
            // the merge does not depend on the order of the PCs, and merging a PC again changes nothing
            var m3r = new AppPaths(Path.Combine(root, "m3r"));
            for (var u = Math.Min(3, gen.Users) - 1; u >= 0; u--) Merge.Run(m3r, gen.FolderFor(root, u));
            for (var u = 0; u < Math.Min(3, gen.Users); u++) Assert.Equal(0, Merge.Run(m3r, gen.FolderFor(root, u)).EventFiles + Merge.Run(m3r, gen.FolderFor(root, u)).EffectFiles);
            File.Copy(new AppPaths(gen.FolderFor(root, 0)).BeforeSnapshot, m3r.BeforeSnapshot, true);
            LearnFolder(m3r, now, rebuild: true);
            var orderDiff = Diff(m3Mem, MemoryOf(m3r));
            Say("merge of 3 PCs in another order, merged twice: memory identical: " + (orderDiff.Count == 0 ? "yes" : "NO: " + string.Join(", ", orderDiff)));
            Assert.Empty(orderDiff);
            // against the naive concatenation: users working at the same time melt into one session per day there
            var oneMem = MemoryOf(one3);
            var mDiff = Diff(oneMem, m3Mem);
            Say($"merge of 3 PCs vs the same lines concatenated untagged in one folder: sessions {m3Run.Result!.SessionCount} vs {one3Run.Result!.SessionCount}; differing memory files: {(mDiff.Count == 0 ? "none" : string.Join(", ", mDiff))}");
            foreach (var k in mDiff) Say($"  {k}: merged {m3Mem[k].Length / 1024.0:0.#} KB, concatenated {oneMem[k].Length / 1024.0:0.#} KB");
            // every user-day has at least a morning and an afternoon session of its own
            Assert.True(m3Run.Result.SessionCount >= Math.Min(3, gen.Users) * days.Count);
            Assert.True(m3Run.Result.SessionCount > one3Run.Result.SessionCount);
            return report.ToString();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Small_office_learns_merges_and_rotates_consistently()
    {
        var gen = new OfficeGenerator { Users = 3, Days = 4, EventsPerDay = 400 };
        Run(gen, false, s => _out.WriteLine(s));
    }

    [Fact]
    public void Big_office_30_days_5_users()
    {
        if (Environment.GetEnvironmentVariable("APPWATCHER_SCALE") != "1") return;
        var gen = new OfficeGenerator { Users = 5, Days = 30, EventsPerDay = 3000 };
        var text = Run(gen, true, s => { _out.WriteLine(s); Console.WriteLine(s); });
        var path = Environment.GetEnvironmentVariable("APPWATCHER_SCALE_REPORT") ?? Path.Combine(Path.GetTempPath(), "scale-report.txt");
        File.WriteAllText(path, text);
    }
}
