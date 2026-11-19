using System.Diagnostics;
using AppWatcher.Core;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Setup;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Version 0.6: maximal defaults (D), structural dialog rules (E), CLI output to a file or pipe (F), the first-hour plain report (G).</summary>
public class EasySetupTests
{
    // ---------- D: defaults are maximal ----------

    [Fact]
    public void Defaults_are_the_fullest_safe_local_choice()
    {
        var c = new WatcherConfig();
        Assert.True(c.Collector.TabDocuments);
        Assert.True(c.Collector.MsaaFallback);
        Assert.True(c.Collector.PressInference);
        Assert.Equal("actions", c.Privacy.EffectiveShotMode);
        Assert.True(c.Privacy.ReadTextFromPictures);
        Assert.True(c.Privacy.MaskTitles);
        Assert.Equal(180, c.RetentionDays);
        Assert.Equal("02:00", c.LearnAt);
        // the things that need IT or leave the PC stay off
        Assert.False(c.SqlCapture.Enabled);
        Assert.False(c.SqlCapture.OdbcTracingOn);
        Assert.False(c.Api.Enabled);
        Assert.False(c.Local.Enabled);
    }

    [Fact]
    public void Every_scanned_source_is_ticked_except_those_needing_a_server_login()
    {
        var found = new List<DataCandidate>
        {
            new() { Path = @"C:\App\data.db", Kind = "sqlite" },
            new() { Path = @"C:\App\stock.accdb", Kind = "access" },
            new() { Path = @"C:\App\settings.ini", Kind = "file" },
            new() { Path = @"C:\App\export.csv", Kind = "file" },
            new() { Path = @"C:\App\main.mdf", Kind = "sqlserver-file" },
            new() { Path = @"C:\App\data.db", Kind = "sqlite" }, // duplicate
        };
        var sources = SetupDefaults.DefaultSources(found);
        Assert.Equal(5, sources.Count);
        Assert.All(sources.Where(s => !s.Path!.EndsWith(".mdf")), s => Assert.True(s.Enabled));
        Assert.False(sources.Single(s => s.Path!.EndsWith(".mdf")).Enabled);
    }

    [Fact]
    public void Setup_plan_applies_recommended_values_profile_and_ticked_sources()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Shopkeeper.exe");
            File.WriteAllBytes(exe, System.Text.Encoding.ASCII.GetBytes("MZ....mscoree.dll....System.Windows.Forms....#Strings"));
            var db = Path.Combine(dir, "shop.db");
            var header = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            File.WriteAllBytes(db, header.Concat(new byte[200]).ToArray());
            File.WriteAllText(Path.Combine(dir, "settings.ini"), "[db]\nfile=shop.db\n");

            var plan = SetupDefaults.Prepare(exe, "Shopkeeper", pid: -1);
            Assert.Equal("WinForms", plan.Toolkit.Name);
            Assert.Equal("winforms", plan.Profile!.Name);
            Assert.Equal("toolkit", plan.ProfileReason);
            Assert.Contains(plan.Candidates, c => c.Kind == "sqlite");

            var cfg = new WatcherConfig { RetentionDays = 7, LearnAt = "05:00" };
            cfg.Collector.TabDocuments = false;
            cfg.Privacy.ScreenshotMode = "off";
            SetupDefaults.Apply(cfg, plan);
            Assert.Equal("Shopkeeper", cfg.App!.ProcessName);
            Assert.Equal("winforms", cfg.Profile);
            Assert.Equal(180, cfg.RetentionDays);                 // recommended values come back
            Assert.Equal("02:00", cfg.LearnAt);
            Assert.True(cfg.Collector.TabDocuments);
            Assert.Equal("actions", cfg.Privacy.EffectiveShotMode);
            Assert.True(cfg.AutoDetectPending);
            Assert.NotNull(cfg.FirstStart);
            Assert.Contains(cfg.DataSources, s => s.Kind == "sqlite" && s.Enabled);
            Assert.All(cfg.DataSources, s => Assert.True(s.Enabled));

            var words = SetupDefaults.WhatIWillLearn(plan);
            Assert.StartsWith("Shopkeeper.", words);
            Assert.Contains("I found where it keeps its data on this PC", words);
            foreach (var jargon in new[] { "toolkit", "UIA", "MSAA", "JSON", "ODBC", "profile JSON" }) Assert.DoesNotContain(jargon, words, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_program_in_a_system_folder_is_looked_at_quickly_without_scanning_that_folder()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(notepad)) return;
        Assert.True(DataSourceScanner.IsSharedFolder(Environment.SystemDirectory));
        Assert.True(DataSourceScanner.IsSharedFolder(@"C:\"));
        Assert.False(DataSourceScanner.IsSharedFolder(Path.Combine(Path.GetTempPath(), "SomeApp")));
        var sw = Stopwatch.StartNew();
        var plan = SetupDefaults.Prepare(notepad, "notepad", "Notepad", pid: -1);
        var words = SetupDefaults.WhatIWillLearn(plan);
        var details = SetupDefaults.Details(plan);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"looking at Notepad took {sw.Elapsed}");
        Assert.DoesNotContain(plan.Candidates, c => c.Path.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Notepad.", words);
        Assert.Contains("Program file:", details);
    }

    [Fact]
    public void Green_box_says_plainly_when_the_data_is_on_a_server()
    {
        var plan = new SetupPlan
        {
            FriendlyName = "DDI inForm", ProcessName = "inform", ProfileReason = "program",
            Profile = ProfileStore.ByName("ddi-inform"),
            Backends = { new BackendInfo { Kind = "universe", Host = "uv01", Port = 31438, Where = "tcp connection" } },
        };
        Assert.Equal(
            "DDI inForm. I recognise this program (profile: ddi-inform). I will record its screens, buttons, fields, the pictures of each screen and the messages it shows. " +
            "Its data lives on a server I cannot read, so I will not learn its tables. That is fine for documenting how the program is used.",
            SetupDefaults.WhatIWillLearn(plan));
        // an ODBC data source that merely exists on the PC is not this program's server
        var other = new SetupPlan { FriendlyName = "Notes", ProcessName = "notes", Backends = { new BackendInfo { Kind = "odbc", Where = "system ODBC DSN" } } };
        Assert.Contains("I have not found where it keeps its data yet", SetupDefaults.WhatIWillLearn(other));
    }

    [Fact]
    public void Traced_files_are_added_ticked_without_logs_or_program_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string F(string name) { var p = Path.Combine(dir, name); File.WriteAllText(p, "x"); return p; }
            var cfg = new WatcherConfig();
            var added = SetupDefaults.AddTracedSources(cfg, new[]
            {
                (F("orders.dat"), 3, 0),      // data extension, read only: added
                (F("state.bin"), 0, 5),        // written by the app: added
                (F("trace.log"), 0, 50),       // a log: never
                (F("helper.dll"), 9, 0),       // program file: never
                (F("readme.txt"), 2, 0),       // not data, not written: never
                (Path.Combine(dir, "gone.db"), 0, 1), // no longer exists: never
            });
            Assert.Equal(2, added);
            Assert.All(cfg.DataSources, s => Assert.True(s.Enabled));
            Assert.Equal(0, SetupDefaults.AddTracedSources(cfg, new[] { (Path.Combine(dir, "orders.dat"), 1, 0) })); // no duplicates
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- E: dialogs by structure, never by title ----------

    private static ControlRow R(int depth, string type, string name = "", string aid = "", bool? readOnly = null) => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, IsReadOnly = readOnly };

    [Fact]
    public void A_small_message_box_titled_like_a_screen_is_a_dialog_and_a_big_window_with_that_title_is_not()
    {
        const string title = "Advanced Demand Forecasting";
        // 17 controls besides the window and its title bar: an icon, a body in several Text pieces, an OK and a Cancel button
        var box = new List<ControlRow> { R(0, "Window", title), R(1, "TitleBar", title), R(2, "Button", "Close") };
        box.Add(R(1, "Pane", "", "pnlMain"));
        box.Add(R(2, "Image", "", "picIcon"));
        for (var i = 0; i < 12; i++) box.Add(R(2, "Text", $"Line {i} of the forecast message"));
        box.Add(R(2, "Pane", "", "pnlButtons"));
        box.Add(R(3, "Button", "OK", "btnOK"));
        box.Add(R(3, "Button", "Cancel", "btnCancel"));
        Assert.Equal(17, DialogDetector.Content(box).Count);
        var (isDialog, body) = DialogDetector.Detect("WindowsForms10.Window.8.app.0", box);
        Assert.True(isDialog);
        Assert.Contains("Line 0 of the forecast message", body);
        Assert.True(DialogDetector.CouldBeDialog(box));

        // 60 controls under the same title: fields to type into and a grid make it a screen, whatever the title says
        var screen = new List<ControlRow> { R(0, "Window", title) };
        for (var i = 0; i < 20; i++) { screen.Add(R(1, "Text", $"Label {i}")); screen.Add(R(1, "Edit", "", $"txtField{i}", readOnly: false)); }
        screen.Add(R(1, "DataGrid", "", "grdForecast"));
        for (var i = 0; i < 17; i++) screen.Add(R(2, "DataItem", $"Row {i}"));
        screen.Add(R(1, "Button", "OK")); screen.Add(R(1, "Button", "Cancel"));
        Assert.Equal(60, DialogDetector.Content(screen).Count);
        Assert.False(DialogDetector.Detect("WindowsForms10.Window.8.app.0", screen).isDialog);
        Assert.False(DialogDetector.CouldBeDialog(screen));
    }

    [Fact]
    public void Dialog_rules_need_only_dialog_buttons_a_body_and_nothing_to_type_into()
    {
        var withSave = new List<ControlRow> { R(0, "Window", "Note"), R(1, "Text", "Unsaved changes"), R(1, "Button", "Save"), R(1, "Button", "Cancel") };
        Assert.False(DialogDetector.Detect("x", withSave).isDialog); // Save is not a message-box button

        var askName = new List<ControlRow> { R(0, "Window", "Rename"), R(1, "Text", "New name:"), R(1, "Edit", "", "txtName", readOnly: false), R(1, "Button", "OK"), R(1, "Button", "Cancel") };
        Assert.False(DialogDetector.Detect("x", askName).isDialog); // an editable field is a small form

        var findInClassic = new List<ControlRow> { R(0, "Window", "Find"), R(1, "Text", "Find what:"), R(1, "Edit", "", "1152", readOnly: false), R(1, "Button", "OK") };
        Assert.False(DialogDetector.Detect("#32770", findInClassic).isDialog); // even the Windows dialog class, when you can type into it

        var readOnlyBody = new List<ControlRow> { R(0, "Window", "Posting"), R(1, "Edit", "Invoice is already posted.", "edtMessage", readOnly: true), R(1, "Button", "OK") };
        var (d, body) = DialogDetector.Detect("x", readOnlyBody);
        Assert.True(d);
        Assert.Equal("Invoice is already posted.", body);

        var noBody = new List<ControlRow> { R(0, "Window", "Empty"), R(1, "Button", "OK") };
        Assert.False(DialogDetector.Detect("x", noBody).isDialog);
    }

    [Fact]
    public void Profiles_hold_no_window_pattern_that_could_be_a_real_screen()
    {
        var inform = ProfileStore.ByName("ddi-inform")!;
        Assert.DoesNotContain("Advanced Demand Forecasting", inform.IgnoreWindows);
        Assert.DoesNotContain("A/P Invoice Entry", inform.DialogWindows);
        // a real screen title never matches an ignore or dialog pattern of any shipped profile
        var screens = new[] { "Advanced Demand Forecasting", "A/P Invoice Entry", "Customer Master", "Error Log", "Job Progress", "Warning Letters" };
        foreach (var p in ProfileStore.All())
            foreach (var t in screens)
            {
                Assert.False(p.IgnoreWindows.Any(w => DialogDetector.Wildcard(t, w)), $"{p.Name} ignores the screen '{t}'");
                Assert.False(p.DialogWindows.Any(w => DialogDetector.Wildcard(t, w)), $"{p.Name} calls the screen '{t}' a dialog");
            }
    }

    // ---------- G: the first-hour report in plain words ----------

    [Fact]
    public void First_hour_notice_says_what_was_recorded_and_what_is_missing()
    {
        var f = new PlainFacts { AppName = "DDI inForm", Screens = 12, Actions = 140, Pictures = 35, NoData = true };
        Assert.Equal("First hour done: 12 screens and 140 actions recorded. I cannot see this program's data; open Settings for what that means.", PlainReport.TrayFirstHour(f));
        var ok = new PlainFacts { AppName = "Shop", Screens = 1, Actions = 1 };
        Assert.Equal("First hour done: 1 screen and 1 action recorded. Everything is being recorded; there is nothing you need to do.", PlainReport.TrayFirstHour(ok));
        var lines = PlainReport.Lines(f, withLastLearn: true);
        Assert.Equal("So far I recorded 12 screens, 140 actions and 35 pictures.", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("I have not updated what I learned yet"));
        Assert.Contains(lines, l => l.StartsWith("I cannot see this program's data.") && l.Contains("read-only connection in Settings, Advanced"));
    }

    [Fact]
    public void First_hour_health_is_written_from_the_data_folder_when_no_learn_ran()
    {
        var root = Path.Combine(Path.GetTempPath(), "appwatcher-firsthour-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        try
        {
            for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(paths.Screens, $"screen-{i}.md"), "# Screen: x");
            var shots = Path.Combine(paths.Screens, "shots", "screen-0");
            Directory.CreateDirectory(shots);
            for (var i = 0; i < 4; i++) File.WriteAllBytes(Path.Combine(shots, $"0{i}-state.png"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(paths.Events, "2026-11-19.jsonl"), "{}\n");
            new CollectorStatus { WindowsSeen = 5, DumpsOk = 0, DumpsFailed = 5, PressesUia = 0, ValuesTyped = 20 }.Save(root);
            var cfg = new WatcherConfig(); // no data source

            Assert.True(PlainReport.WriteFirstHourHealth(paths, cfg, "Legacy Stock"));
            var md = File.ReadAllText(Path.Combine(paths.Memory, "health.md"));
            Assert.Contains("# Health (first hour)", md);
            Assert.Contains("So far I recorded 3 screens, 20 actions and 4 pictures over 1 day.", md);
            Assert.Contains("I cannot read the fields and buttons of its screens.", md);
            Assert.Contains("I have not seen any button or menu clicks yet.", md);
            Assert.Contains("I cannot see this program's data.", md);
            Assert.False(PlainReport.WriteFirstHourHealth(paths, cfg, "Legacy Stock")); // a later learn owns health.md
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Learned_health_page_opens_with_the_same_plain_words()
    {
        var input = new LearnInput { AppName = "Shop", HasDataSources = false, Collector = new CollectorStatus { WindowsSeen = 4, DumpsOk = 4, PressesUia = 9, ValuesTyped = 11, Pictures = 6 } };
        input.Events.Add(Synthetic.Ev(new DateTime(2026, 1, 1, 9, 0, 0), Acts.Foreground, "Main", "Main", ct: "Window"));
        var health = MemoryWriter.Render(Learner.Learn(input))["health.md"];
        var plain = health.IndexOf("## In plain words", StringComparison.Ordinal);
        Assert.True(plain > 0 && plain < health.IndexOf("## The numbers", StringComparison.Ordinal));
        Assert.Contains("So far I recorded 1 screen, 20 actions and 6 pictures over 1 day.", health);
        Assert.Contains("I cannot see this program's data.", health);
    }

    // ---------- F: CLI output reaches a file or a pipe ----------

    private static string? FindExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AppWatcher"))) dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "src", "AppWatcher", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "AppWatcher.exe", SearchOption.AllDirectories).Where(f => !f.Contains(@"\publish\")).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    [Fact]
    public void Cli_writes_to_a_pipe_and_to_a_file()
    {
        var exe = FindExe();
        Assert.True(exe is not null, "AppWatcher.exe was not built (dotnet build src/AppWatcher)");

        // a pipe: what a test runner or "| more" gives the program
        var psi = new ProcessStartInfo(exe!, $"--portable --detect \"{exe}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(psi)!)
        {
            var output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("Toolkit:", output);
            Assert.Contains("Profile:", output);
        }

        // a file: appwatcher --detect <exe> > out.txt
        var outFile = Path.Combine(Path.GetTempPath(), "appwatcher-cli-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var cmd = new ProcessStartInfo("cmd.exe", $"/d /c start \"\" /b /wait \"{exe}\" --portable --detect \"{exe}\" > \"{outFile}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var c = Process.Start(cmd)!;
            Assert.True(c.WaitForExit(60_000));
            var text = File.Exists(outFile) ? File.ReadAllText(outFile) : "";
            Assert.Contains("Toolkit:", text);
        }
        finally { try { File.Delete(outFile); } catch { } }
    }
}
