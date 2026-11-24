using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Several PCs merged into one data folder: their sessions stay apart even when the users work at the same time.</summary>
public class MultiPcTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 9, 0, 0);
    private static string Strip(string md) => string.Join("\n", md.Split('\n').Where(l => !l.StartsWith("Last learn:")));

    private static List<UiEvent> Work(string machine, DateTime start, int visits, string screen)
    {
        var list = new List<UiEvent>();
        for (var i = 0; i < visits; i++)
        {
            var t = start.AddMinutes(i * 3);
            var src = $"events/{start:yyyy-MM-dd}.{machine}.jsonl#L{list.Count + 1}";
            list.Add(Synthetic.Ev(t, Acts.Foreground, "Main", "Main", ct: "Window", src: src));
            list.Add(Synthetic.Ev(t.AddSeconds(2), Acts.Invoke, "Main", "Open " + screen, ct: "Button", src: src));
            list.Add(Synthetic.Ev(t.AddSeconds(3), Acts.Foreground, screen, screen, ct: "Window", src: src));
            list.Add(Synthetic.Ev(t.AddSeconds(10), Acts.Value, screen, "Customer", "c" + i, src: src));
            list.Add(Synthetic.Ev(t.AddSeconds(20), Acts.Invoke, screen, "Save", ct: "Button", src: src));
            list.Add(Synthetic.Ev(t.AddSeconds(30), Acts.Foreground, "Main", "Main", ct: "Window", src: src));
        }
        return list;
    }

    [Fact]
    public void Machine_of_a_source_is_the_tag_after_the_date()
    {
        Assert.Equal("a1b2c3d4", FlowLearner.MachineOf("events/2026-09-01.a1b2c3d4.jsonl#L7"));
        Assert.Equal("", FlowLearner.MachineOf("events/2026-09-01.jsonl#L7"));
        Assert.Equal("", FlowLearner.MachineOf("events/test.jsonl#L1"));
        Assert.Equal("", FlowLearner.MachineOf(""));
    }

    [Fact]
    public void Users_on_two_PCs_working_at_the_same_time_are_two_sessions()
    {
        var ev = Work("aaaa1111", T0, 20, "Orders").Concat(Work("bbbb2222", T0.AddMinutes(1), 20, "Stock")).ToList();
        var sessions = FlowLearner.Sessions(ev);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Single(s.Select(e => FlowLearner.MachineOf(e.Source)).Distinct()));
        // the same events of one PC only are one session too, and a gap of 30+ minutes cuts a PC's own session
        Assert.Single(FlowLearner.Sessions(Work("aaaa1111", T0, 20, "Orders")));
        var split = Work("aaaa1111", T0, 3, "Orders").Concat(Work("aaaa1111", T0.AddHours(2), 3, "Orders")).ToList();
        Assert.Equal(2, FlowLearner.Sessions(split).Count);
    }

    [Fact]
    public void Incremental_learning_is_exact_when_a_closed_session_overlaps_an_open_one_of_another_PC()
    {
        // A works 09:00-10:00 (closed at the first learn), B 09:30-11:00 (still open: now is 11:10 minus a bit)
        var all = Work("aaaa1111", T0, 20, "Orders").Concat(Work("bbbb2222", T0.AddMinutes(30), 40, "Stock")).ToList();
        LearnInput Input(DateTime now, Func<UiEvent, bool> keep) => new() { AppName = "demo", Events = all.Where(keep).Select(Clone).ToList(), Now = now };
        var first = new DateTime(2026, 9, 1, 11, 0, 0).AddMinutes(10); // B's last event is ~11:27? make B open
        var cut = T0.AddMinutes(30 + 20 * 3); // B has done 20 of its 40 visits
        var r1 = Learner.Learn(Input(cut.AddMinutes(5), e => e.T <= cut), new LearnState(), out var state);
        // A (ends 09:57) is closed, B is open and started before A ended: nothing may be committed past B's start
        Assert.True(state.Watermark is null || state.Watermark < T0.AddMinutes(30));
        var final = T0.AddHours(6);
        var inc = Learner.Learn(Input(final, _ => true), state, out _);
        var full = Learner.Learn(Input(final, _ => true));
        var a = MemoryWriter.Render(inc); var b = MemoryWriter.Render(full);
        foreach (var k in MemoryWriter.Files) Assert.True(Strip(a[k]) == Strip(b[k]), k + " differs");
        Assert.Equal(2, full.SessionCount);
        _ = r1; _ = first;
    }

    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "aw-multipc-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Merge_keeps_two_PCs_effects_saved_in_the_same_millisecond_and_tells_them_apart()
    {
        var root = Temp();
        try
        {
            var t = new DateTime(2026, 9, 1, 10, 0, 0);
            var pcs = new[] { "pc-a", "pc-b" }.Select(n =>
            {
                var paths = new AppWatcher.Core.AppPaths(Path.Combine(root, n));
                paths.EnsureDirectories();
                File.WriteAllText(Path.Combine(paths.Events, "2026-09-01.jsonl"), $"{{\"t\":\"{t:s}\",\"act\":\"start\",\"val\":\"user:{n.Replace("-", "")}1 machine:{n.Replace("-", "")}2 version:1\"}}\n");
                Synthetic.Effect(t, "Orders", new AppWatcher.Core.Snapshots.DiffResult()).Save(paths.Effects); // the same name on both PCs
                return paths;
            }).ToList();
            var into = new AppWatcher.Core.AppPaths(Path.Combine(root, "all"));
            foreach (var p in pcs) AppWatcher.Core.Merge.Run(into, p.Root);
            var files = Directory.GetFiles(into.Effects).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(2, files.Count);
            var read = AppWatcher.Core.Snapshots.EffectRecord.ReadAll(into.Effects);
            Assert.Equal(2, read.Select(r => r.Machine).Distinct().Count());
            Assert.All(read, r => Assert.NotEmpty(r.Machine));
            // merging the merged folder keeps the tags, and a known file is not read again
            var again = new AppWatcher.Core.AppPaths(Path.Combine(root, "again"));
            AppWatcher.Core.Merge.Run(again, into.Root);
            Assert.Equal(files, Directory.GetFiles(again.Effects).Select(Path.GetFileName).OrderBy(x => x).ToList());
            Assert.Single(AppWatcher.Core.Snapshots.EffectRecord.ReadAll(into.Effects, new HashSet<string> { files[0]! }));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static UiEvent Clone(UiEvent e) => new() { T = e.T, Act = e.Act, Win = e.Win, Name = e.Name, Val = e.Val, Ct = e.Ct, Source = e.Source };
}
