using AppWatcher.Core;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Screens;
using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sources;
using AppWatcher.Core.Sql;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>Tests for the improvements written after the first real run (labels, dialogs, tabs, titles, flows with actions, detection, sources, retention, merge).</summary>
public class ImprovementTests
{
    private static ControlRow Row(int depth, string type, string name = "", string aid = "", string pos = "") => new() { Depth = depth, Type = type, Name = name, AutomationId = aid, Position = pos };

    [Fact]
    public void Labels_clean_wrapper_suffixes_prefixes_and_camel_case()
    {
        Assert.Equal("Master Search", Labels.Clean("srcMasterSearch_EmbeddableTextBox"));
        Assert.Equal("Register Number", Labels.Clean("neRegisterNumber"));
        Assert.Equal("Effective", Labels.Clean("deEffective"));
        Assert.Equal("AP Invoice Number", Labels.Clean("txtAPInvoiceNumber"));
        Assert.Equal("Customer Name", Labels.Clean("customer_name"));
        Assert.Equal("Delivery Date", Labels.Clean("deliveryDate")); // "de" is not stripped when a lower-case letter follows
        Assert.Equal("Text Box", Labels.Clean("textBox12"));
        Assert.True(Labels.IsWrapperId("txtName_EmbeddableTextBox"));
        Assert.False(Labels.IsWrapperId("txtName"));
    }

    [Fact]
    public void Labels_resolve_from_name_then_nearest_text_then_id()
    {
        var rows = new List<ControlRow>
        {
            Row(0, "Window", "Customer"),
            Row(1, "Text", "Customer code:", "", "10,10 90x20"),
            Row(1, "Edit", "", "txtCode", "110,10 120x22"),
            Row(1, "Text", "Phone", "", "10,40 60x18"),
            Row(1, "Edit", "", "txtPhone", "10,62 120x22"),
            Row(1, "Edit", "", "neZip", "400,10 80x22"),
            Row(1, "Edit", "2026-01-01", "deEffective", "400,40 80x22"),
        };
        var r = new ScreenRecord { Title = "Customer", Controls = rows };
        var fields = r.LogicalFields();
        Assert.Equal("Customer code", fields.Single(f => f.row.AutomationId == "txtCode").label);   // text to the left, colon stripped
        Assert.Equal("Phone", fields.Single(f => f.row.AutomationId == "txtPhone").label);          // text just above, left aligned
        Assert.Equal("Zip", fields.Single(f => f.row.AutomationId == "neZip").label);               // no label near: cleaned id
        Assert.Equal("Effective", fields.Single(f => f.row.AutomationId == "deEffective").label);   // the Name is a value, not a label
    }

    [Fact]
    public void Wrapper_controls_collapse_to_one_logical_field()
    {
        var rows = new List<ControlRow>
        {
            Row(0, "Window", "New Product"),
            Row(1, "Pane", "", "pnlMain"),
            Row(2, "Edit", "", "txtName"),
            Row(3, "Edit", "", "txtName_EmbeddableTextBox"),
            Row(2, "ComboBox", "", "cboType"),
            Row(3, "Edit", "", "cboType[Editor] Edit Area"),
            Row(2, "Pane", "", "txtNotes"),
            Row(3, "Edit", "", "txtNotes_EmbeddableTextBox"),
            Row(2, "List", "", "lstHits"),
            Row(3, "ListItem", "Item 1"),
            Row(3, "ListItem", "Item 2"),
        };
        var r = new ScreenRecord { Title = "New Product", Controls = rows };
        var fields = r.LogicalFields();
        Assert.Equal(4, fields.Count); // Name, Type, Notes, the list
        Assert.Contains(fields, f => f.label == "Name" && f.row.AutomationId == "txtName_EmbeddableTextBox");
        Assert.Contains(fields, f => f.label == "Type" && f.row.Type == "Edit");
        Assert.Contains(fields, f => f.label == "Notes");
        var md = r.ToMarkdown();
        Assert.Contains("| Name | Edit | ? | | | txtName_EmbeddableTextBox |", md);
        Assert.DoesNotContain("| Edit Area |", md);
    }

    [Fact]
    public void Dialog_detector_knows_classic_and_toolkit_message_boxes()
    {
        var classic = new List<ControlRow> { Row(0, "Window", "Error"), Row(1, "Text", "Please enter a valid date"), Row(1, "Button", "OK") };
        var (isDialog, body) = DialogDetector.Detect("#32770", classic);
        Assert.True(isDialog); Assert.Equal("Please enter a valid date", body);

        var infragistics = new List<ControlRow>
        {
            Row(0, "Window", "A/P Invoice Entry"), Row(1, "Pane", "", "pnl"), Row(2, "Edit", "Invoice INV410987 is already posted.", "lblBody"),
            Row(2, "Button", "OK", "btnOK"), Row(2, "Button", "Cancel", "btnCancel"),
        };
        var (d2, b2) = DialogDetector.Detect("WindowsForms10.Window.8.app.0", infragistics);
        Assert.True(d2); Assert.Contains("already posted", b2);

        var screen = new List<ControlRow> { Row(0, "Window", "Customer"), Row(1, "Edit", "", "txtName"), Row(1, "Text", "Name"), Row(1, "Button", "OK"), Row(1, "ComboBox", "", "cboType") };
        Assert.False(DialogDetector.Detect("WindowsForms10.Window.8.app.0", screen).isDialog);

        var form = new List<ControlRow> { Row(0, "Window", "Find"), Row(1, "Text", "Search for:"), Row(1, "Edit", "", "txtSearch"), Row(1, "Button", "OK"), Row(1, "Button", "Cancel") };
        Assert.False(DialogDetector.Detect("WindowsForms10.Window.8.app.0", form).isDialog); // an input field, not a body
    }

    [Fact]
    public void Session_windows_are_found_by_password_title_or_size()
    {
        Assert.True(DialogDetector.IsSessionWindow("Login", new List<ControlRow> { Row(0, "Window"), Row(1, "Edit", "", "txtUser"), new() { Depth = 1, Type = "Edit", IsPassword = true } }, false));
        Assert.True(DialogDetector.IsSessionWindow("Splash Screen", new List<ControlRow> { Row(0, "Window") }, false));
        Assert.True(DialogDetector.IsSessionWindow("inForm", new List<ControlRow> { Row(0, "Window"), Row(1, "Image") }, true)); // tiny first window
        Assert.False(DialogDetector.IsSessionWindow("inForm", Enumerable.Range(0, 30).Select(i => Row(1, "Edit", "", "f" + i)).Prepend(Row(0, "Window")).ToList(), true));
        Assert.True(DialogDetector.IsSessionWindow("Enter Register #", new List<ControlRow> { Row(0, "Window"), Row(1, "Edit", "", "neRegisterNumber") }, false, new[] { "Enter Register*" }));
        Assert.True(DialogDetector.Wildcard("Enter Register #", "enter register*"));
        Assert.False(DialogDetector.Wildcard("Customer", "enter register*"));
    }

    [Fact]
    public void Titles_mask_quoted_parts_and_normalizer_folds_varying_words()
    {
        Titles.MaskQuoted = true;
        Assert.Equal("Vendor - # \"…\" Internal Notes", Titles.Key("Vendor - 00283 \"EVERFLOW SUPPLIES INC\" Internal Notes"));
        Assert.Equal("A/P Invoice - INV#", Titles.Key("A/P Invoice - INV410987"));
        var n = TitleNormalizer.FromGroups(new[] { new[] { "Customer - 0002776 \"Nate Test\"", "Customer - 0003001 \"Other Co\"" }, new[] { "Order ABC", "Order XYZ" }, new[] { "Report for March", "Report for the whole of April" } });
        Assert.Equal("Customer - # \"…\"", n.Apply("Customer - 0002776 \"Nate Test\""));
        Assert.Equal("Order #", n.Apply("Order ABC"));
        Assert.Equal("Order #", n.Apply("Order XYZ"));
        Assert.Equal("Report for #", n.Apply("Report for March"));
        Assert.Equal("Main", n.Apply("Main"));
        Titles.MaskQuoted = false;
        Assert.Equal("Vendor - # \"EVERFLOW SUPPLIES INC\" Internal Notes", Titles.Key("Vendor - 00283 \"EVERFLOW SUPPLIES INC\" Internal Notes"));
        Titles.MaskQuoted = true;
    }

    [Fact]
    public void Flow_steps_carry_actions_and_skip_session_windows()
    {
        var t = new DateTime(2026, 1, 1, 8, 0, 0);
        var events = new List<UiEvent>();
        void Ev(string act, string win, string name, string ct = "Button", string? how = null, string? val = null) { events.Add(new UiEvent { T = t, Act = act, Win = win, Name = name, Ct = ct, How = how, Val = val, Source = "events/a.jsonl#L1" }); t = t.AddSeconds(5); }
        Ev(Acts.Open, "Splash Screen", "Splash Screen", "Window");
        Ev(Acts.Open, "Login", "Login", "Window");
        Ev(Acts.Value, "Login", "User", "Edit", val: "bob");
        Ev(Acts.Foreground, "Main", "Main", "Window");
        for (var i = 0; i < 3; i++)
        {
            Ev(Acts.Invoke, "Main", "Customer Master", "MenuItem", PressHow.Menu);
            Ev(Acts.Open, "Main › Customer Master", "Main › Customer Master", "Window");
            Ev(Acts.Value, "Main › Customer Master", "Master Search", "Edit", val: "ZZTEST" + i);
            Ev(Acts.Invoke, "Main › Customer Master", "Save", "Button", PressHow.Inferred);
            Ev(Acts.Foreground, "Main", "Main", "Window");
            t = t.AddSeconds(60);
        }
        var sessionWindows = new HashSet<string> { "Splash Screen", "Login" };
        var (flows, nav, main, _) = FlowLearner.Learn(events, sessionWindows);
        Assert.Equal("Main", main);
        var flow = Assert.Single(flows);
        Assert.Equal("Main › Customer Master", flow.Key);
        Assert.Equal(3, flow.Count);
        Assert.Contains(flow.StepControls["Main › Customer Master"], c => c.name == "press Save" && c.count == 3);
        Assert.Contains(flow.StepControls["Main › Customer Master"], c => c.name == "type Master Search" && c.count == 3);
        Assert.DoesNotContain(flows, f => f.Key is "Splash Screen" or "Login");
        Assert.Contains(nav, e => e.From == "Login" && e.To == "Main"); // the navigation graph keeps them

        var input = new LearnInput { AppName = "demo", Events = events, Screens = { new ScreenRecord { Title = "Login", Kind = ScreenKinds.Session }, new ScreenRecord { Title = "Splash Screen", Kind = ScreenKinds.Session } } };
        var r = Learner.Learn(input);
        var md = MemoryWriter.Render(r);
        Assert.Contains("- Main › Customer Master: press Save (3), type Master Search (3)", md["flows.md"]);
        var cm = Assert.Single(r.Screens, s => s.Title == "Main › Customer Master");
        Assert.Equal(ScreenKinds.Tab, cm.Kind);
        Assert.Equal("Customer Master", cm.MenuPath);
        Assert.Equal("Main", cm.ReachedFrom);
        Assert.Equal(3, cm.FieldTyped["Master Search"]);
        Assert.Equal("code", cm.FieldTypes["Master Search"]);
        Assert.Contains("Menu item before it opened: Customer Master (seen 3×)", md["screens.md"]);
        Assert.Contains("Session windows (flows start after them): Login, Splash Screen", md["screens.md"]);
        Assert.Equal(3, r.Health.PressesSeen);
        Assert.Equal(3, r.Health.PressesInferred);
        Assert.DoesNotContain("ZZTEST", md["screens.md"]);
    }

    [Fact]
    public void Message_boxes_become_rules_not_screens()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0);
        var events = new List<UiEvent>
        {
            Synthetic.Ev(t, Acts.Foreground, "A/P Invoice", "A/P Invoice", ct: "Window"),
            Synthetic.Ev(t.AddSeconds(1), Acts.Invoke, "A/P Invoice", "Post", ct: "Button"),
            Synthetic.Ev(t.AddSeconds(2), Acts.Dialog, "A/P Invoice Entry", "A/P Invoice Entry", "Invoice INV410987 is already posted.", ct: "Window", src: "events/a.jsonl#L3"),
            Synthetic.Ev(t.AddSeconds(3), Acts.Foreground, "A/P Invoice Entry", "A/P Invoice Entry", ct: "Window"),
            Synthetic.Ev(t.AddSeconds(4), Acts.Close, "A/P Invoice Entry", "A/P Invoice Entry", ct: "Window"),
            Synthetic.Ev(t.AddMinutes(1), Acts.Value, "A/P Invoice", "Amount", "abc"),
            Synthetic.Ev(t.AddMinutes(1).AddSeconds(1), Acts.Dialog, "Error", "Error", "Amount must be a number", ct: "Window", src: "events/a.jsonl#L7"),
        };
        var r = Learner.Learn(new LearnInput { AppName = "demo", Events = events });
        Assert.DoesNotContain(r.Screens, s => s.Title == "A/P Invoice Entry");
        Assert.Equal(1, r.MessageWindows["A/P Invoice Entry"]);
        var msg = Assert.Single(r.Rules, x => x.Kind == "message");
        Assert.Equal("on Post", msg.Field); Assert.Equal("(button)", msg.Pattern); Assert.Contains("already posted", msg.Message);
        var val = Assert.Single(r.Rules, x => x.Kind == "validation");
        Assert.Equal("Amount", val.Field);
        var md = MemoryWriter.Render(r);
        Assert.Contains("## Messages after an action", md["rules.md"]);
        Assert.Contains("Message windows (not screens; their text is in rules.md): A/P Invoice Entry (1×), Error (1×)", md["screens.md"]);
    }

    [Fact]
    public void Screen_record_round_trips_kind_shortcuts_and_header()
    {
        var rec = new ScreenRecord { Title = "Orders", Kind = ScreenKinds.Session, MenuPath = "File > Orders", ReachedFrom = "Main", Source = "uia-cached" };
        rec.Controls.Add(Row(0, "Window", "Orders"));
        rec.Controls.Add(new ControlRow { Depth = 1, Type = "Button", Name = "Save", AutomationId = "btnSave", Shortcut = "F3" });
        rec.Controls.Add(Row(1, "MenuItem", "Post"));
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-screen-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(ScreenWriter.WriteOnce(dir, rec));
            var back = Assert.Single(ScreenWriter.ReadAll(dir));
            Assert.Equal(ScreenKinds.Session, back.Kind);
            Assert.Equal("File > Orders", back.MenuPath);
            Assert.Equal("Main", back.ReachedFrom);
            Assert.Equal("uia-cached", back.Source);
            Assert.Equal("F3", back.Controls.Single(c => c.Name == "Save").Shortcut);
            Assert.Equal("orders.md", back.SourceFile);
            // a richer tree under the same title replaces the file; a poorer one does not
            var richer = new ScreenRecord { Title = "Orders 12" };
            richer.Controls.AddRange(rec.Controls); richer.Controls.Add(Row(1, "Edit", "", "txtNote"));
            Assert.True(ScreenWriter.WriteOnce(dir, richer));
            Assert.Equal(4, ScreenWriter.ReadAll(dir).Single().Controls.Count);
            Assert.False(ScreenWriter.WriteOnce(dir, rec));
            Assert.Equal(4, ScreenWriter.ReadAll(dir).Single().Controls.Count);
            Assert.Empty(ScreenWriter.TitleGroups(dir)); // different structures: each has its own index entry with one title
            Assert.False(ScreenWriter.WriteOnce(dir, new ScreenRecord { Title = "Orders 99", Controls = richer.Controls }));
            Assert.Equal(new[] { "Orders 12", "Orders 99" }, Assert.Single(ScreenWriter.TitleGroups(dir)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Toolkit_detector_reads_markers_from_exe_and_siblings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-tk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Inform.exe");
            File.WriteAllBytes(exe, System.Text.Encoding.ASCII.GetBytes("MZ....mscoree.dll....System.Windows.Forms....Infragistics.Win.UltraWinGrid.v20.2....#Strings"));
            File.WriteAllText(Path.Combine(dir, "Infragistics.Win.v20.2.dll"), "x");
            var tk = ToolkitDetector.Detect(exe);
            Assert.Equal("Infragistics on WinForms", tk.Name);
            Assert.True(tk.IsDotNet);
            Assert.Equal("infragistics", tk.Profile);
            Assert.Contains("tab documents", tk.Expectation);

            var qtDir = Path.Combine(dir, "qt"); Directory.CreateDirectory(qtDir);
            var qt = Path.Combine(qtDir, "tool.exe");
            File.WriteAllBytes(qt, System.Text.Encoding.ASCII.GetBytes("MZ....Qt5Core.dll....Qt5Widgets.dll"));
            Assert.Equal("Qt", ToolkitDetector.Detect(qt).Name);
            Assert.Equal("custom-drawn", ToolkitDetector.ProfileFor("Qt"));
            Assert.Equal("Unknown", ToolkitDetector.Detect(Path.Combine(dir, "missing.exe")).Name);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Backend_detector_classifies_connection_strings_without_keeping_secrets()
    {
        var sql = BackendDetector.Classify("Server=db01,1433;Database=Inform;User Id=app;Password=hunter2;TrustServerCertificate=true")!;
        Assert.Equal("sqlserver", sql.Kind); Assert.Equal("db01", sql.Host); Assert.Equal(1433, sql.Port); Assert.Equal("Inform", sql.Database);
        Assert.DoesNotContain("hunter2", sql.Describe());
        var uv = BackendDetector.Classify("DSN=INFORM;Driver={UniVerse ODBC Driver};UID=u;PWD=p")!;
        Assert.Equal("universe", uv.Kind);
        var access = BackendDetector.Classify(@"Provider=Microsoft.ACE.OLEDB.12.0;Data Source=C:\Apps\Data\inventory.accdb;Persist Security Info=False")!;
        Assert.Equal("access", access.Kind); Assert.Equal("inventory.accdb", access.Database);
        var jdbc = BackendDetector.Classify("jdbc:postgresql://pg.local:5432/erp")!;
        Assert.Equal("postgresql", jdbc.Kind); Assert.Equal("pg.local", jdbc.Host); Assert.Equal(5432, jdbc.Port); Assert.Equal("erp", jdbc.Database);
        Assert.Null(BackendDetector.Classify("just some text"));

        var found = BackendDetector.ParseText("<configuration>\n <connectionStrings>\n  <add name=\"Main\" connectionString=\"Data Source=.\\SQLEXPRESS;Initial Catalog=Shop;Integrated Security=True\" />\n </connectionStrings>\n <appSettings><add key=\"api\" value=\"https://api.vendor.com:8443/v2/\"/></appSettings>\n</configuration>", "app.exe.config");
        Assert.Contains(found, b => b.Kind == "sqlserver" && b.Database == "Shop");
        Assert.Contains(found, b => b.Kind == "rest" && b.Host == "api.vendor.com" && b.Port == 8443);
        var ini = BackendDetector.ParseText("[server]\nhost = uv01.corp.local\nport = 31438\n", "installedapplication.config");
        Assert.Contains(ini, b => b.Host == "uv01.corp.local");
        Assert.Contains(ini, b => b.Port == 31438 && b.Kind == "universe");
    }

    [Fact]
    public void Profiles_match_exe_then_toolkit_and_apply_onto_config()
    {
        var all = ProfileStore.All();
        Assert.Contains(all, p => p.Name == "generic");
        Assert.Contains(all, p => p.Name == "ddi-inform");
        var inform = ProfileStore.Match(@"D:\DDI\Inform.exe", "Inform", "DDI inForm", "Infragistics on WinForms")!;
        Assert.Equal("ddi-inform", inform.Name);
        Assert.Equal("infragistics", ProfileStore.Match(@"C:\x\other.exe", "other", null, "Infragistics on WinForms")!.Name);
        Assert.Equal("generic", ProfileStore.Match(@"C:\x\other.exe", "other", null, "Unknown")!.Name);
        var cfg = new WatcherConfig();
        inform.ApplyTo(cfg);
        Assert.Equal("ddi-inform", cfg.Profile);
        Assert.Contains("Splash Screen", cfg.Collector.SessionWindows);
        Assert.Contains("add", cfg.EffectTriggers);
        Assert.True(cfg.Collector.TabDocuments);
        var exported = AppProfile.FromConfig(cfg, "my-site", "Infragistics on WinForms");
        Assert.Contains("Splash Screen", exported.SessionWindows);
        Assert.DoesNotContain("save", exported.EffectTriggers); // defaults are not repeated
    }

    [Fact]
    public void Drop_folder_reads_csv_and_xlsx_exports_as_tables()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "CUSTOMER.csv"), "\uFEFFCustomerId;Name;Balance\r\n1;\"Acme; Inc\";10,50\r\n2;Bob;0\r\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "no header here");
            WriteXlsx(Path.Combine(dir, "ORDERS.xlsx"), new[] { new[] { "OrderNo", "Customer", "Amount" }, new[] { "A1", "Acme", "12.5" }, new[] { "A2", "Bob", "3" } });
            var src = new DropFolderSource(dir);
            var snap = new DataSnapshot();
            src.Snapshot(snap, new SnapshotSettings(), null);
            var tables = snap.Databases[src.Id];
            var cust = tables["CUSTOMER"];
            Assert.Equal(new[] { "CustomerId", "Name", "Balance" }, cust.Columns);
            Assert.Equal(new[] { "CustomerId" }, cust.PrimaryKey);
            Assert.Equal("Acme; Inc", cust.Rows["1"]["Name"]);
            var orders = tables["ORDERS"];
            Assert.Equal(2, orders.Rows.Count);
            Assert.Equal("12.5", orders.Rows["A1"]["Amount"]);
            var p1 = src.Probe(null); var p2 = src.Probe(p1.Token);
            Assert.False(p2.Changed);
            File.AppendAllText(Path.Combine(dir, "CUSTOMER.csv"), "3;Carl;1\r\n");
            Assert.True(src.Probe(p2.Token).Changed);
            Assert.NotNull(SourceFactory.Create(new DataSourceConfig { Kind = "dropfolder", Path = dir }));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void WriteXlsx(string path, string[][] rows)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(content); }
        Add("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
        Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\"/></sheets></workbook>");
        var sb = new System.Text.StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sb.Append("<row>");
            for (var c = 0; c < rows[r].Length; c++) sb.Append($"<c r=\"{(char)('A' + c)}{r + 1}\" t=\"inlineStr\"><is><t>{rows[r][c]}</t></is></c>");
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        Add("xl/worksheets/sheet1.xml", sb.ToString());
    }

    [Fact]
    public void Text_sql_logs_of_other_engines_are_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-sqllog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mysql-general.log"),
                "2026-03-01T10:00:00.123456Z\t 12 Query\tSELECT 1\n2026-03-01T10:00:05.000000Z\t 12 Query\tINSERT INTO orders (id, customer) VALUES (7, 'Acme')\n");
            File.WriteAllText(Path.Combine(dir, "postgresql.log"),
                "2026-03-01 10:01:00.000 UTC [123] app@erp LOG:  statement: UPDATE customers SET name = 'Bob' WHERE id = 2\n");
            var list = SqlLogReaders.ReadAll(dir, "inform");
            Assert.Equal(2, list.Count);
            Assert.Equal("insert", list[0].Kind); Assert.Equal("orders", list[0].Table); Assert.Equal("Acme", list[0].Values["customer"]);
            Assert.Equal("update", list[1].Kind); Assert.Equal("customers", list[1].Table);
            Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 5), list[0].T);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Retention_deletes_only_learned_old_event_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-ret-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        paths.EnsureDirectories();
        try
        {
            foreach (var d in new[] { "2026-01-01", "2026-01-20", "2026-02-10", "2026-01-05.abcd1234" }) File.WriteAllText(Path.Combine(paths.Events, d + ".jsonl"), "{}\n");
            var deleted = Retention.RotateEvents(paths, 30, watermark: new DateTime(2026, 1, 25), now: new DateTime(2026, 3, 1));
            Assert.Equal(3, deleted.Count); // 01-01, 01-05 (merged), 01-20 are older than 30 days and before the watermark
            Assert.True(File.Exists(Path.Combine(paths.Events, "2026-02-10.jsonl")));
            Assert.Empty(Retention.RotateEvents(paths, 0, new DateTime(2026, 3, 1)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Merge_folds_another_pc_and_health_counts_users_and_machines()
    {
        var root = Path.Combine(Path.GetTempPath(), "appwatcher-merge-" + Guid.NewGuid().ToString("N"));
        var into = new AppPaths(Path.Combine(root, "into")); into.EnsureDirectories();
        var other = new AppPaths(Path.Combine(root, "other")); other.EnsureDirectories();
        try
        {
            var t = new DateTime(2026, 2, 1, 9, 0, 0);
            string Line(DateTime at, string act, string win, string name, string? val = null) => System.Text.Json.JsonSerializer.Serialize(new UiEvent { T = at, Act = act, Win = win, Name = name, Ct = act == Acts.Start ? "watcher" : "Window", Val = val });
            File.WriteAllText(Path.Combine(into.Events, "2026-02-01.jsonl"), Line(t, Acts.Start, "", "App Watcher started", "user:aaaa1111 machine:bbbb2222") + "\n" + Line(t.AddSeconds(1), Acts.Foreground, "Main", "Main") + "\n");
            File.WriteAllText(Path.Combine(other.Events, "2026-02-01.jsonl"), Line(t.AddHours(2), Acts.Start, "", "App Watcher started", "user:cccc3333 machine:dddd4444") + "\n" + Line(t.AddHours(2).AddSeconds(1), Acts.Foreground, "Main", "Main") + "\n");
            var rec = new ScreenRecord { Title = "Main" }; rec.Controls.Add(Row(0, "Window", "Main")); rec.Controls.Add(Row(1, "Button", "Save"));
            ScreenWriter.WriteOnce(other.Screens, rec);
            var r = Merge.Run(into, other.Root);
            Assert.Equal("dddd4444", r.Machine);
            Assert.Equal(1, r.EventFiles); Assert.Equal(1, r.ScreensNew);
            Assert.True(File.Exists(Path.Combine(into.Events, "2026-02-01.dddd4444.jsonl")));
            var input = Learner.LoadInput(into, "demo");
            Assert.Equal(4, input.Events.Count);
            var result = Learner.Learn(input);
            Assert.Equal(2, result.Health.UsersSeen); Assert.Equal(2, result.Health.MachinesSeen);
            Assert.Contains("2 users on 2 PCs", MemoryWriter.Render(result)["health.md"]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Collector_status_problems_and_expectations_read_plainly()
    {
        var s = new CollectorStatus { WindowsSeen = 23, DumpsOk = 0, DumpsFailed = 23, HasDataSources = false };
        var problems = s.Problems();
        Assert.Contains(problems, p => p.Contains("no window's control tree could be read"));
        Assert.Contains(problems, p => p.Contains("no button or menu press was seen"));
        Assert.Contains(problems, p => p.Contains("no data source"));
        var input = new LearnInput { Collector = s, Backends = { new BackendInfo { Kind = "universe", Host = "uv01", Port = 31438, Where = "installedapplication.config" } }, HasDataSources = false, Toolkit = "Infragistics on WinForms" };
        var lines = Learner.Expectations(input);
        Assert.Contains(lines, l => l.StartsWith("Fields and buttons: no"));
        Assert.Contains(lines, l => l.Contains("universe server (uv01:31438)") && l.Contains("read-only login"));
        var r = Learner.Learn(input);
        var health = MemoryWriter.Render(r)["health.md"];
        Assert.Contains("## What the collector could not do", health);
        Assert.Contains("Discovery found a universe backend at uv01", health);
        Assert.Contains("Backend: universe, uv01:31438", MemoryWriter.Render(r)["architecture.md"]);
    }

    [Fact]
    public void Event_field_key_uses_the_cleaned_automation_id_and_how_round_trips()
    {
        var e = new UiEvent { Aid = "srcMasterSearch_EmbeddableTextBox", Ct = "Edit", Act = Acts.Invoke, How = PressHow.Msaa };
        Assert.Equal("Master Search", e.FieldKey);
        var json = System.Text.Json.JsonSerializer.Serialize(e);
        Assert.Contains("\"how\":\"msaa\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<UiEvent>(json)!;
        Assert.Equal(PressHow.Msaa, back.How);
        Assert.Equal("press Master Search", FlowLearner.Verb(back));
    }
}
