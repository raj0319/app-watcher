using AppWatcher.Core;
using AppWatcher.Core.Events;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;
using CsCheck;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>
/// Property-based checks (CsCheck) of the pure logic in Core: random but valid evidence in, promises of CLAUDE.md out.
/// All data is synthetic. Each property runs a fixed number of cases; a failure prints the seed and the shrunk case.
/// </summary>
public class PropertyTests
{
    // ---------- synthetic evidence ----------

    private static readonly string[] Windows = { "Orders", "Invoices", "Stock" };
    private static readonly string[] Fields = { "Customer", "Amount", "Quantity", "Notes", "Reference" };
    private static readonly string[] Buttons = { "Save", "Post", "Print" };

    /// <summary>One work session: open a window, type some values, press a button, optionally a row appears, back to main.</summary>
    private sealed record Session(int Win, int Button, (int field, string value)[] Typed, bool Effect, int Minutes);

    private static readonly Gen<string> RawValue = Gen.String[Gen.Char.AlphaNumeric, 8, 14];

    private static readonly Gen<Session> SessionGen =
        Gen.Select(Gen.Int[0, Windows.Length - 1], Gen.Int[0, Buttons.Length - 1],
                   Gen.Select(Gen.Int[0, Fields.Length - 1], RawValue).Array[0, 4],
                   Gen.Bool, Gen.Int[1, 50],
                   (w, b, typed, fx, m) => new Session(w, b, typed, fx, m));

    // a day is a list of sessions; days are one calendar day apart
    private static readonly Gen<Session[][]> DaysGen = SessionGen.Array[1, 4].Array[2, 4];

    private static readonly DateTime Start = new(2026, 2, 2, 9, 0, 0);

    private static LearnInput Build(Session[][] days, int fromDay, int toDay)
    {
        var input = new LearnInput { AppName = "demo" };
        var id = 0;
        for (var d = fromDay; d < toDay; d++)
        {
            var day = Start.AddDays(d);
            var line = 0;
            for (var n = 0; n < days[d].Length; n++)
            {
                var s = days[d][n];
                var t = day.AddMinutes(n * 90 + s.Minutes);
                string Src() => $"events/{day:yyyy-MM-dd}.jsonl#L{++line}";
                var w = Windows[s.Win];
                input.Events.Add(Synthetic.Ev(t, Acts.Foreground, "Main", "Main", ct: "Window", src: Src()));
                input.Events.Add(Synthetic.Ev(t.AddSeconds(3), Acts.Invoke, "Main", "New " + w, ct: "Button", src: Src()));
                input.Events.Add(Synthetic.Ev(t.AddSeconds(4), Acts.Foreground, w, w, ct: "Window", src: Src()));
                var at = 8;
                foreach (var (f, v) in s.Typed)
                    input.Events.Add(Synthetic.Ev(t.AddSeconds(at += 4), Acts.Value, w, Fields[f], v, src: Src()));
                input.Events.Add(Synthetic.Ev(t.AddSeconds(at += 4), Acts.Invoke, w, Buttons[s.Button], ct: "Button", src: Src()));
                if (s.Effect)
                {
                    var diff = new DiffResult();
                    diff.Tables.Add(Synthetic.Added("app.db", w.ToLowerInvariant(), Synthetic.Row(("id", (++id + d * 1000).ToString()), ("status", "open"))));
                    input.Effects.Add(Synthetic.Effect(t.AddSeconds(at + 2), w, diff, $"fx-{d}-{id}.json"));
                }
                input.Events.Add(Synthetic.Ev(t.AddSeconds(at + 8), Acts.Foreground, "Main", "Main", ct: "Window", src: Src()));
            }
        }
        input.Latest = Synthetic.Snapshot("app.db", ("orders", Synthetic.Table(new[] { "id", "status" }, new[] { "id" }, new[] { "1", "open" }, new[] { "2", "paid" })));
        // "now" is the day after the last day included, so every session of the included days is closed
        input.Now = Start.AddDays(toDay + 1);
        return input;
    }

    private static string Strip(string md) => string.Join("\n", md.Split('\n').Where(l => !l.StartsWith("Last learn:")));

    private static Dictionary<string, string> Memory(LearnResult r)
        => MemoryWriter.Render(r).ToDictionary(kv => kv.Key, kv => Strip(kv.Value));

    private static void SameMemory(Dictionary<string, string> a, Dictionary<string, string> b, string what)
    {
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (var k in a.Keys) Assert.True(a[k] == b[k], $"{what}: {k} differs");
    }

    // ---------- learner ----------

    [Fact]
    public void Learning_twice_gives_identical_memory()
    {
        DaysGen.Sample(days =>
        {
            SameMemory(Memory(Learner.Learn(Build(days, 0, days.Length))), Memory(Learner.Learn(Build(days, 0, days.Length))), "learn twice");
        }, iter: 40);
    }

    [Fact]
    public void Incremental_learning_over_a_split_equals_one_full_rebuild()
    {
        Gen.Select(DaysGen, Gen.Int[1, 3]).Sample(t =>
        {
            var (days, c) = t;
            var cut = Math.Min(c, days.Length - 1);
            var full = Memory(Learner.Learn(Build(days, 0, days.Length)));
            Learner.Learn(Build(days, 0, cut), new LearnState(), out var state);
            var inc = Memory(Learner.Learn(Build(days, 0, days.Length), state, out _));
            SameMemory(full, inc, $"split after day {cut}");
        }, iter: 40);
    }

    // ---------- masking ----------

    [Fact]
    public void Memory_files_never_contain_a_raw_typed_value()
    {
        DaysGen.Sample(days =>
        {
            var input = Build(days, 0, days.Length);
            var memory = MemoryWriter.Render(Learner.Learn(input));
            var values = input.Events.Where(e => e.Act == Acts.Value && e.Val is { Length: >= 8 }).Select(e => e.Val!).Distinct().ToList();
            foreach (var (file, text) in memory)
                foreach (var v in values)
                    Assert.False(text.Contains(v, StringComparison.OrdinalIgnoreCase), $"{file} contains the typed value {v}");
        }, iter: 40);
    }

    [Fact]
    public void Shape_keeps_no_letter_or_digit_of_the_value()
    {
        Gen.String[Gen.Char.AlphaNumeric, 1, 40].Sample(v =>
        {
            var shape = Masking.Shape(v);
            // digits become #, letters become a or A: no digit and no other letter can survive
            Assert.DoesNotContain(shape, c => char.IsDigit(c));
            Assert.DoesNotContain(shape, c => char.IsLetter(c) && c != 'a' && c != 'A');
            if (v.Length is > 0 and <= 24) Assert.Equal(v.Length, shape.Length);
        }, iter: 300);
    }

    [Fact]
    public void Shape_cannot_reproduce_a_value_that_has_a_digit_or_a_letter_other_than_a()
    {
        Gen.String[Gen.Char.AlphaNumeric, 3, 30].Sample(v =>
        {
            if (v.Any(char.IsDigit) || v.Any(c => char.IsLetter(c) && c != 'a' && c != 'A'))
                Assert.NotEqual(v, Masking.Shape(v));
        }, iter: 300);
    }

    [Fact]
    public void Event_value_is_masked_for_password_like_names()
    {
        Gen.Select(RawValue, Gen.Int[0, 5].Select(i => new[] { "Password", "Login password", "API key", "Secret", "Token", "PIN" }[i])).Sample(t =>
        {
            var (v, name) = t;
            Assert.Equal(Masking.Masked, Masking.ForEvent(v, name, "", false));
            Assert.Equal(Masking.Masked, Masking.ForEvent(v, "Whatever", "", true));
            Assert.Equal(Masking.Masked, Masking.ForAppValue(v, name, "", false));
        }, iter: 200);
    }

    // ---------- FieldReads ----------

    private static readonly Gen<string> Plain = Gen.String[Gen.Char.AlphaNumeric, 1, 20];
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    [Fact]
    public void ShowsTyped_is_true_for_the_same_text()
    {
        Plain.Sample(x => Assert.True(FieldReads.ShowsTyped(x, x)), iter: 300);
        Plain.Sample(x => Assert.True(FieldReads.ShowsTyped("  " + x + " ", x)), iter: 100);
    }

    [Fact]
    public void ShowsTyped_accepts_the_same_number_formatted()
    {
        Gen.Select(Gen.Int[0, 99999], Gen.Int[0, 99]).Sample(t =>
        {
            var (whole, cents) = t;
            var typed = whole.ToString(Inv);
            Assert.True(FieldReads.ShowsTyped(whole.ToString("N2", Inv), typed));
            Assert.True(FieldReads.ShowsTyped("$" + whole.ToString("N2", Inv), typed));
            Assert.True(FieldReads.ShowsTyped(whole + ".00", typed));
            var withCents = $"{whole}.{cents:00}";
            Assert.True(FieldReads.ShowsTyped("$" + decimal.Parse(withCents, Inv).ToString("N2", Inv), withCents));
        }, iter: 300);
    }

    [Fact]
    public void ShowsTyped_rejects_an_empty_typed_value_and_a_different_number()
    {
        Plain.Sample(x => Assert.False(FieldReads.ShowsTyped(x, "   ")), iter: 100);
        Gen.Select(Gen.Int[0, 5000], Gen.Int[1, 5000]).Sample(t => Assert.False(FieldReads.ShowsTyped("$" + (t.Item1 + t.Item2 + 10000) + ".00", t.Item1.ToString(Inv))), iter: 200);
    }

    [Fact]
    public void LongerThanKeys_grows_with_the_value_and_shrinks_with_the_keys()
    {
        Gen.Select(Gen.Int[0, 60], Gen.Int[0, 30], Gen.Int[0, 20]).Sample(t =>
        {
            var (len, keys, more) = t;
            var v = new string('x', len);
            var longer = new string('x', len + more);
            if (FieldReads.LongerThanKeys(v, keys)) Assert.True(FieldReads.LongerThanKeys(longer, keys));
            if (FieldReads.LongerThanKeys(v, keys + more)) Assert.True(FieldReads.LongerThanKeys(v, keys));
            Assert.False(FieldReads.LongerThanKeys(v, -1));
        }, iter: 500);
    }

    private static readonly Gen<string> Rect =
        Gen.Select(Gen.Int[-50, 400], Gen.Int[-50, 400], Gen.Int[1, 200], Gen.Int[1, 200], (x, y, w, h) => $"{x},{y} {w}x{h}");

    [Fact]
    public void Overlaps_is_symmetric_and_a_rectangle_overlaps_itself()
    {
        Gen.Select(Rect, Rect).Sample(t => Assert.Equal(FieldReads.Overlaps(t.Item1, t.Item2), FieldReads.Overlaps(t.Item2, t.Item1)), iter: 500);
        Rect.Sample(a => Assert.True(FieldReads.Overlaps(a, a)), iter: 200);
    }

    [Fact]
    public void IsReplacement_is_never_true_for_an_empty_text_an_old_value_or_the_same_text()
    {
        Gen.Select(Plain, Gen.Double[0, 30]).Sample(t =>
        {
            var (own, secs) = t;
            Assert.False(FieldReads.IsReplacement(own, "", secs));
            if (secs > 10) Assert.False(FieldReads.IsReplacement(own, own + "zzzzzz", secs));
            Assert.False(FieldReads.IsReplacement(own, own, 1));
        }, iter: 300);
    }

    // ---------- ValueDebouncer ----------

    /// <summary>A step on one field: the value typed (null = nothing, only time passes) after some milliseconds.</summary>
    private static readonly Gen<(string? val, int ms)[]> ReadSteps =
        Gen.Select(Gen.Int[0, 5].Select(i => i == 0 ? null : "v" + i), Gen.Int[10, 2500]).Array[1, 25];

    private static UiEvent Typed(DateTime t, string val) => new() { T = t, Act = Acts.Value, Win = "Orders", Ct = "Edit", Name = "Customer", Val = val };

    [Fact]
    public void Debouncer_emits_only_pushed_values_and_the_last_one_is_not_lost()
    {
        ReadSteps.Sample(steps =>
        {
            var deb = new ValueDebouncer();
            var now = Start;
            var pushed = new List<string>(); var emitted = new List<string>();
            foreach (var (val, ms) in steps)
            {
                now = now.AddMilliseconds(ms);
                if (val is not null) { pushed.Add(val); deb.Push(Typed(now, val), now); }
                emitted.AddRange(deb.Flush(now).Select(e => e.Val!));
            }
            emitted.AddRange(deb.Flush(now.AddSeconds(10), all: true).Select(e => e.Val!));
            Assert.All(emitted, v => Assert.Contains(v, pushed));
            if (pushed.Count > 0) Assert.Equal(pushed[^1], emitted[^1]);
            Assert.Equal(0, deb.PendingCount);
        }, iter: 400);
    }

    [Fact]
    public void Debouncer_writes_at_most_one_event_per_push()
    {
        ReadSteps.Sample(steps =>
        {
            var deb = new ValueDebouncer();
            var now = Start; var pushes = 0; var emitted = 0;
            foreach (var (val, ms) in steps)
            {
                now = now.AddMilliseconds(ms);
                if (val is not null) { pushes++; deb.Push(Typed(now, val), now); }
                emitted += deb.Flush(now).Count;
            }
            emitted += deb.Flush(now.AddSeconds(10), all: true).Count;
            Assert.True(emitted <= pushes);
        }, iter: 400);
        // the same value pushed again and again within the quiet time is one event
        Gen.Int[1, 30].Sample(n =>
        {
            var deb = new ValueDebouncer();
            var now = Start;
            for (var i = 0; i < n; i++) { now = now.AddMilliseconds(100); deb.Push(Typed(now, "same"), now); Assert.Empty(deb.Flush(now)); }
            Assert.Single(deb.Flush(now.AddSeconds(5)));
            Assert.Empty(deb.Flush(now.AddSeconds(10), all: true));
        }, iter: 30);
    }

    [Fact]
    public void Debouncer_never_writes_an_empty_typed_value()
    {
        Gen.String[Gen.Const(' '), 0, 4].Sample(blank =>
        {
            var deb = new ValueDebouncer();
            deb.Push(Typed(Start, blank), Start);
            Assert.Empty(deb.Flush(Start.AddSeconds(10), all: true));
        }, iter: 50);
    }

    // ---------- ClickTracker / PressDecider: no click, no press ----------

    private sealed class EmptyWorld : IWindowWorld
    {
        public IReadOnlyList<ControlRow>? RowsOf(long hwnd) => null;
        public bool IsVisible(long hwnd) => true;
        public long WindowAt(int x, int y) => 0;
        public long RootOf(long hwnd) => hwnd;
        public long DocumentOf(long hwnd) => 0;
        public string ScreenOf(long hwnd) => "Main";
        public bool MenuPopupAt(int x, int y, long goingAway) => false;
    }

    /// <summary>A world with one button under every point, so that any click WOULD find a target.</summary>
    private sealed class ButtonEverywhere : IWindowWorld
    {
        private readonly List<ControlRow> _rows = new()
        {
            new ControlRow { Depth = 0, Type = "Window", Name = "Main", Position = "0,0 2000x2000" },
            new ControlRow { Depth = 1, Type = "Button", Name = "Save", Position = "0,0 2000x2000" },
        };
        public IReadOnlyList<ControlRow>? RowsOf(long hwnd) => _rows;
        public bool IsVisible(long hwnd) => true;
        public long WindowAt(int x, int y) => 1;
        public long RootOf(long hwnd) => 1;
        public long DocumentOf(long hwnd) => 0;
        public string ScreenOf(long hwnd) => "Main";
        public bool MenuPopupAt(int x, int y, long goingAway) => false;
    }

    // samples with the button never down, never went down, and no Enter/Space: only moves, Tab and focus switches
    private static readonly Gen<(int x, int y, int ms, bool tab, long front, long away)[]> QuietSamples =
        Gen.Select(Gen.Int[0, 1900], Gen.Int[0, 1900], Gen.Int[1, 3000], Gen.Bool, Gen.Long[0, 3], Gen.Long[0, 3], (x, y, ms, tab, f, a) => (x, y, ms, tab, f, a)).Array[1, 40];

    [Fact]
    public void Without_a_click_or_a_press_key_the_tracker_never_produces_a_press()
    {
        QuietSamples.Sample(samples =>
        {
            foreach (IWindowWorld world in new IWindowWorld[] { new EmptyWorld(), new ButtonEverywhere() })
            {
                var tracker = new ClickTracker();
                var now = Start;
                foreach (var (x, y, ms, tab, front, away) in samples)
                {
                    now = now.AddMilliseconds(ms);
                    var o = tracker.Observe(new InputSample(now, x, y, false, false, tab, front, away), world);
                    Assert.Equal(ClickKind.None, o.Kind);
                    Assert.Null(tracker.Confirm(now, "window changed", "Main", out _));
                    Assert.Null(tracker.Decider.Pending);
                    Assert.False(tracker.MayCreditFocused(now));
                    Assert.False(tracker.MayCreditPointer(now));
                }
            }
        }, iter: 300);
    }

    [Fact]
    public void Without_a_click_the_press_decider_confirms_nothing()
    {
        Gen.Select(Gen.Int[0, 1900], Gen.Int[0, 1900], Gen.Int[0, 12000]).Sample(t =>
        {
            var (x, y, ms) = t;
            var d = new PressDecider();
            var now = Start.AddMilliseconds(ms);
            Assert.Null(d.Confirm(now));
            Assert.Null(d.Pending);
            Assert.False(d.ClickedRecently(now));
            // a pressed state under the pointer is remembered, not counted: it is nothing until a click lands
            d.RememberPressed(new PressCandidate("Save", "", "Button", "Main"), x, y, now);
            Assert.Null(d.Confirm(now.AddMilliseconds(10)));
        }, iter: 300);
    }

    [Fact]
    public void A_confirmed_target_is_counted_once()
    {
        Gen.Select(Gen.Int[0, 1900], Gen.Int[0, 1900], Gen.Int[0, 1400]).Sample(t =>
        {
            var (x, y, ms) = t;
            var tracker = new ClickTracker();
            var world = new ButtonEverywhere();
            tracker.Observe(new InputSample(Start, x, y, true, true, false, 1), world);
            tracker.Observe(new InputSample(Start.AddMilliseconds(60), x, y, false, false, false, 1), world);
            var at = Start.AddMilliseconds(60 + ms);
            var first = tracker.Confirm(at, "window changed", "Main", out var deferred);
            if (!deferred && first is not null) Assert.Null(tracker.Confirm(at, "window changed", "Main", out _));
        }, iter: 300);
    }
}
