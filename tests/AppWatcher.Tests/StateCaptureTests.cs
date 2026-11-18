using AppWatcher.Core;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Version 0.4: one picture per state (deduped, capped, page-wise), dropdown suggestions and grid results as text.</summary>
public class StateCaptureTests
{
    private static byte[] Png(int seed) => Enumerable.Range(0, 64).Select(i => (byte)(i * 7 + seed)).ToArray();

    [Fact]
    public void Shot_store_files_pictures_per_screen_without_duplicates_and_with_caps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-shots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ShotStore(dir) { MaxPerScreen = 3, MaxTotalBytes = 10_000 };
            var t = new DateTime(2026, 3, 1, 9, 0, 0);
            var p1 = store.Add("Customer 123", "first-seen", Png(1), "aaa", t, "screen record", out _);
            Assert.Equal("shots/customer/01-first-seen.png", p1);
            Assert.Null(store.Add("Customer 999", "after-press-Save", Png(2), "aaa", t, "x", out var why)); // same state, other title of the same screen
            Assert.Contains("same state", why);
            Assert.Equal("shots/customer/02-after-press-save.png", store.Add("Customer 123", "after-press-Save", Png(2), "bbb", t.AddSeconds(5), "invoke Save", out _));
            Assert.Equal("shots/customer/03-after-type-name.png", store.Add("Customer 123", "after-type-Name", Png(3), "ccc", t.AddSeconds(9), "type Name", out _));
            Assert.Null(store.Add("Customer 123", "after-press-Post", Png(4), "ddd", t.AddSeconds(12), "invoke Post", out why));
            Assert.Contains("cap of 3", why);
            Assert.True(File.Exists(Path.Combine(dir, "shots", "customer", "index.md")));
            var md = File.ReadAllText(Path.Combine(dir, "shots", "customer", "index.md"));
            Assert.Contains("| 2 | 02-after-press-save.png | after-press-Save | 2026-03-01 09:00:05 | invoke Save |", md);
            Assert.Equal(3, store.Entries("Customer 7").Count);
            // another screen has its own folder and count
            Assert.Equal("shots/orders/01-first-seen.png", store.Add("Orders", "first-seen", Png(5), "eee", t, "screen record", out _));
            // the size cap applies across screens
            var big = new ShotStore(dir) { MaxPerScreen = 50, MaxTotalBytes = 300 };
            Assert.Null(big.Add("Orders", "after-press-Search", Png(6), "fff", t, "x", out why));
            Assert.Contains("size cap", why);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Coarse_hash_ignores_tiny_changes_but_not_a_changed_field()
    {
        var grid = new byte[96 * 54];
        for (var i = 0; i < grid.Length; i++) grid[i] = (byte)(i % 200);
        var a = ShotStore.CoarseHash(grid);
        var caret = (byte[])grid.Clone(); caret[10] = (byte)(caret[10] + 3); // a blink: same 16-level bucket
        Assert.Equal(a, ShotStore.CoarseHash(caret));
        var changed = (byte[])grid.Clone(); for (var i = 500; i < 540; i++) changed[i] = 255;
        Assert.NotEqual(a, ShotStore.CoarseHash(changed));
    }

    [Fact]
    public void Dropdown_and_result_events_become_lookup_and_grid_lines()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0);
        var events = new List<UiEvent>
        {
            Synthetic.Ev(t, Acts.Foreground, "Customer Master", "Customer Master", ct: "Window"),
            Synthetic.Ev(t.AddSeconds(1), Acts.Value, "Customer Master", "Master Search", "ZZ"),
            new() { T = t.AddSeconds(2), Act = Acts.Dropdown, Win = "Customer Master", Ct = "Edit", Name = "Master Search", Val = "ZZ", Items = new() { "ZZTEST-CUS-001 ZZTEST Customer One", "ZZTEST-CUS-002 ZZTEST Customer Two" }, Source = "events/a.jsonl#L3" },
            Synthetic.Ev(t.AddSeconds(3), Acts.Invoke, "Customer Master", "Search", ct: "Button"),
            new() { T = t.AddSeconds(4), Act = Acts.Result, Win = "Customer Master", Ct = "DataGrid", Name = "Search", Val = "Hits", Items = new() { "Code | Name | Balance", "ZZTEST-CUS-001 | ZZTEST Customer One | 10.50", "ZZTEST-CUS-002 | ZZTEST Customer Two | 0.00" }, Source = "events/a.jsonl#L5" },
            Synthetic.Ev(t.AddSeconds(30), Acts.Invoke, "Customer Master", "Search", ct: "Button"),
            new() { T = t.AddSeconds(31), Act = Acts.Result, Win = "Customer Master", Ct = "DataGrid", Name = "Search", Val = "Hits", Items = new() { "Code | Name | Balance" }, Source = "events/a.jsonl#L7" },
        };
        var r = Learner.Learn(new LearnInput { AppName = "demo", Events = events });
        var s = Assert.Single(r.Screens, x => x.Title == "Customer Master");
        Assert.Equal(1, s.FieldLookups["Master Search"]);
        Assert.Equal(2, s.LookupSizes["Master Search"]);
        Assert.Contains("AAAAAA-AAA-### AAAAAA Aa...", s.LookupShapes["Master Search"]);
        var res = s.ActionResults["Search › Hits"];
        Assert.Equal(2, res.Seen); Assert.Equal(new[] { "Code", "Name", "Balance" }, res.Columns); Assert.Equal(0, res.MinRows); Assert.Equal(2, res.MaxRows); Assert.Equal(1, res.EmptySeen);
        var md = MemoryWriter.Render(r)["screens.md"];
        Assert.Contains("- Master Search: shows up to 2 suggestions, shaped like `AAAAAA-AAA-### AAAAAA Aa...` — seen 1×", md);
        Assert.Contains("- after Search › Hits: columns Code, Name, Balance; 0 to 2 rows, empty 1× — seen 2×", md);
        Assert.DoesNotContain("ZZTEST Customer One", md); // never the values
        // the export's probe file names the lookup and the result without the raw rows
        var probe = Core.Export.Exporter.Probe(s, r);
        Assert.Contains("Suggestion list while typing: seen 1×, up to 2 items", probe);
        Assert.Contains("After Search › Hits: grid columns Code, Name, Balance; 0 to 2 rows (seen 2×", probe);
        Assert.DoesNotContain("ZZTEST Customer One", probe);
    }

    [Fact]
    public void Event_items_and_shot_round_trip_through_json()
    {
        var e = new UiEvent { Act = Acts.Dropdown, Name = "City", Val = "Os", Items = new() { "Oslo", "Osaka" }, Shot = "shots/customer/04-dropdown-city.png" };
        var json = System.Text.Json.JsonSerializer.Serialize(e);
        Assert.Contains("\"items\":[\"Oslo\",\"Osaka\"]", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<UiEvent>(json)!;
        Assert.Equal(2, back.Items!.Count);
        Assert.Equal("shots/customer/04-dropdown-city.png", back.Shot);
        var plain = System.Text.Json.JsonSerializer.Serialize(new UiEvent { Act = Acts.Focus }, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("items", plain);
    }

    [Fact]
    public void Picture_mode_falls_back_sensibly()
    {
        Assert.Equal("actions", new PrivacySettings().EffectiveShotMode);
        Assert.Equal("off", new PrivacySettings { Screenshots = false }.EffectiveShotMode);
        Assert.Equal("first", new PrivacySettings { ScreenshotMode = "first" }.EffectiveShotMode);
        Assert.Equal("actions", new PrivacySettings { ScreenshotMode = "nonsense" }.EffectiveShotMode);
    }
}
