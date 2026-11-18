using System.ComponentModel;
using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Sources;
using Microsoft.Win32;

namespace AppWatcher.Forms;

/// <summary>
/// First-run wizard and later settings window. Plain controls, no designer file.
/// No message boxes: every validation message shows in the status line at the bottom. The only pickers are the
/// program, file and folder pickers, which the user opens on purpose with a Browse button.
/// </summary>
internal sealed class SettingsForm : Form
{
    public WatcherConfig Config { get; }
    private readonly bool _firstRun;
    private readonly Label _statusLine = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 8, 0), ForeColor = Color.Firebrick };

    // App tab
    private readonly ListView _processes = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly TextBox _exePath = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _expect = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Info };
    private readonly System.Windows.Forms.Timer _analyzeTimer = new() { Interval = 600 };
    private string _analyzedExe = "";
    private AppProfile? _profile;
    private ToolkitInfo? _toolkit;
    private List<BackendInfo> _backends = new();
    // Data tab
    private readonly CheckedListBox _sources = new() { CheckOnClick = true };
    private readonly List<DataSourceConfig> _sourceList = new();
    private readonly TextBox _sqlLogPath = new() { Width = 360 };
    private readonly CheckBox _sqlEnabled = new() { Text = "Use SQL statement log (Extended Events .xel / CSV / text log folder, or ODBC SQL.LOG)", AutoSize = true };
    private readonly CheckBox _odbcTrace = new() { Text = "Turn on Windows ODBC tracing for this user (writes %TEMP%\\SQL.LOG)", AutoSize = true };
    private readonly Label _detectStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _backendLabel = new() { Left = 10, Top = 262, Width = 640, Height = 34, ForeColor = SystemColors.GrayText };
    // Learning tab
    private readonly TextBox _learnAt = new() { Width = 60 };
    private readonly TextBox _triggers = new() { Width = 420 };
    private readonly NumericUpDown _maxDbMb = new() { Minimum = 1, Maximum = 100_000, Width = 90 };
    private readonly NumericUpDown _maxRows = new() { Minimum = 100, Maximum = 10_000_000, Width = 110, Increment = 1000 };
    private readonly NumericUpDown _afterMs = new() { Minimum = 200, Maximum = 30_000, Width = 90, Increment = 100 };
    private readonly NumericUpDown _retention = new() { Minimum = 0, Maximum = 3650, Width = 90 };
    private readonly CheckBox _msaa = new() { Text = "Read screens through MSAA when UI Automation shows fewer than ten controls", AutoSize = true };
    private readonly CheckBox _infer = new() { Text = "Count a button or menu focus followed by a window change as a press", AutoSize = true };
    private readonly CheckBox _tabs = new() { Text = "Record each selected tab document as a screen of its own (Window › Tab)", AutoSize = true };
    // Privacy tab
    private readonly CheckBox _maskTitles = new() { Text = "Mask quoted parts of window titles and tab labels (\"ACME INC\" becomes \"…\") in memory and export files", AutoSize = true };
    private readonly ComboBox _shotMode = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _exclude = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Width = 630 };
    private readonly TextBox _allow = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Width = 630 };
    // AI tab
    private readonly CheckBox _apiEnabled = new() { Text = "Claude API: send the memory files after each nightly learn (off by default)", AutoSize = true };
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true, Width = 420 };
    private readonly TextBox _apiModel = new() { Width = 200 };
    private readonly CheckBox _localEnabled = new() { Text = "Local model (OpenAI-compatible, e.g. Ollama): same analysis, nothing leaves this PC (off by default)", AutoSize = true };
    private readonly TextBox _localEndpoint = new() { Width = 300 };
    private readonly TextBox _localModel = new() { Width = 200 };
    private readonly Label _apiStatus = new() { AutoSize = true };
    // Password tab
    private readonly TextBox _pw1 = new() { UseSystemPasswordChar = true, Width = 200 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, Width = 200 };

    public SettingsForm(WatcherConfig config, bool firstRun)
    {
        Config = Clone(config);
        _firstRun = firstRun;
        Text = firstRun ? "App Watcher: first-time setup" : "App Watcher: settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 640);
        MinimumSize = new Size(640, 600);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(AppTab());
        tabs.TabPages.Add(DataTab());
        tabs.TabPages.Add(LearningTab());
        tabs.TabPages.Add(PrivacyTab());
        tabs.TabPages.Add(AiTab());
        tabs.TabPages.Add(PasswordTab());
        tabs.TabPages.Add(AboutTab());
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var ok = new Button { Text = firstRun ? "Start watching" : "Save", Width = 110 };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        bar.Controls.Add(cancel); bar.Controls.Add(ok);
        Controls.Add(tabs); Controls.Add(_statusLine); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        _analyzeTimer.Tick += (_, _) => { _analyzeTimer.Stop(); AnalyzeProgram(); };
        Load += (_, _) => { RefreshProcesses(); LoadSources(); if (_exePath.Text.Length > 0) AnalyzeProgram(); };
    }

    private void Status(string text, bool error = true) { _statusLine.ForeColor = error ? Color.Firebrick : SystemColors.ControlText; _statusLine.Text = text; }

    // ---------- tabs ----------

    private TabPage AppTab()
    {
        var p = new TabPage("1. App to watch");
        var intro = new Label { Text = "Pick the program App Watcher should learn from. Only this program is ever watched.", Left = 10, Top = 10, Width = 640, Height = 20 };
        _processes.SetBounds(10, 36, 650, 200);
        _processes.Columns.Add("Program", 140); _processes.Columns.Add("Window title", 310); _processes.Columns.Add("Path", 190);
        _processes.SelectedIndexChanged += (_, _) =>
        {
            if (_processes.SelectedItems.Count == 0) return;
            var it = _processes.SelectedItems[0];
            _exePath.Text = it.SubItems[2].Text;
            _displayName.Text = it.Text;
            ScheduleAnalyze();
        };
        var refresh = new Button { Text = "Refresh list", Left = 10, Top = 242, Width = 100 };
        refresh.Click += (_, _) => RefreshProcesses();
        var l1 = new Label { Text = "Program file: pick from the list above, browse to any .exe on any drive, or type its full path:", Left = 10, Top = 276, Width = 650 };
        _exePath.SetBounds(10, 298, 550, 23);
        _exePath.TextChanged += (_, _) =>
        {
            if (_processes.SelectedItems.Count == 0 && _exePath.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) _displayName.Text = Path.GetFileNameWithoutExtension(_exePath.Text);
            ScheduleAnalyze();
        };
        var browse = new Button { Text = "Browse...", Left = 568, Top = 297, Width = 92 };
        browse.Click += (_, _) =>
        {
            // user-initiated picker: the one place a file dialog is allowed, so any program anywhere can be chosen
            using var d = new OpenFileDialog
            {
                Title = "Choose the program App Watcher should learn from",
                Filter = "Programs|*.exe|All files|*.*",
                CheckFileExists = true,
                InitialDirectory = _exePath.Text.Length > 0 && Directory.Exists(Path.GetDirectoryName(_exePath.Text) ?? "") ? Path.GetDirectoryName(_exePath.Text) : "",
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _processes.SelectedItems.Clear();
            _exePath.Text = d.FileName;
            _displayName.Text = Path.GetFileNameWithoutExtension(d.FileName);
            Status(Process.GetProcessesByName(_displayName.Text).Length > 0
                ? $"{_displayName.Text} is running; it will be watched as soon as you press Start watching."
                : $"{_displayName.Text} is not running right now; it will be watched from the moment it starts.", error: false);
        };
        var l2 = new Label { Text = "Name for its data folder:", Left = 10, Top = 332, Width = 150 };
        _displayName.SetBounds(166, 329, 200, 23);
        var note = new Label { Text = "Changing the program later starts a new data folder; the old one is kept.", Left = 376, Top = 332, Width = 290, ForeColor = SystemColors.GrayText };
        var l3 = new Label { Text = "What App Watcher expects to learn from this program (found from the program file, its config files, ODBC data sources and open connections; nothing is read from its data):", Left = 10, Top = 362, Width = 650, Height = 32 };
        _expect.SetBounds(10, 396, 650, 150);
        _expect.Text = "Pick a program to see what can be learned from it.";
        p.Controls.AddRange(new Control[] { intro, _processes, refresh, l1, _exePath, browse, l2, _displayName, note, l3, _expect });
        if (Config.App is not null) { _exePath.Text = Config.App.ExePath ?? ""; _displayName.Text = Config.App.DisplayName; }
        return p;
    }

    private void ScheduleAnalyze() { _analyzeTimer.Stop(); _analyzeTimer.Start(); }

    /// <summary>A1/A2/A3: toolkit from the exe, matching profile, backends from config files, DSNs and the process's connections; then the expectation text.</summary>
    private void AnalyzeProgram()
    {
        var exe = _exePath.Text.Trim();
        if (exe.Length == 0 || exe == _analyzedExe) return;
        if (!File.Exists(exe)) { _expect.Text = "The program file was not found. Type the full path or use Browse."; return; }
        _analyzedExe = exe;
        _expect.Text = "Looking at the program file...";
        var proc = Path.GetFileNameWithoutExtension(exe);
        var display = _displayName.Text;
        Task.Run(() =>
        {
            var tk = ToolkitDetector.Detect(exe);
            var profile = ProfileStore.Match(exe, proc, Program.ProductName(exe), tk.Name);
            int? pid = null;
            try { pid = Process.GetProcessesByName(proc).FirstOrDefault()?.Id; } catch { }
            var backends = BackendDetector.Detect(exe, proc, pid, display);
            return (tk, profile, backends);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || IsDisposed) { if (!IsDisposed) _expect.Text = "Could not look at the program: " + t.Exception?.GetBaseException().Message; return; }
            (_toolkit, _profile, _backends) = t.Result;
            _expect.Text = ExpectationText();
            _backendLabel.Text = _backends.Count == 0 ? "Discovery found no backend (no connection strings, DSNs or open connections)." : "Discovery found: " + string.Join("; ", _backends.Take(4).Select(b => b.Describe()));
            // a sqlite or access file named in a config file next to the exe is worth proposing
            foreach (var b in _backends.Where(b => b.Kind is "sqlite" or "access" && b.Database is not null))
            {
                var dir = Path.GetDirectoryName(exe)!;
                foreach (var cand in new[] { Path.Combine(dir, b.Database!), Path.Combine(dir, "Data", b.Database!) })
                    if (File.Exists(cand) && !_sourceList.Any(s => string.Equals(s.Path, cand, StringComparison.OrdinalIgnoreCase)))
                    { var cfg = new DataSourceConfig { Kind = b.Kind, Path = cand, Label = Path.GetFileName(cand) }; _sourceList.Add(cfg); _sources.Items.Add($"{cfg.Describe()}  (named in {b.Where})", true); }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string ExpectationText()
    {
        var lines = new List<string>();
        var tk = _toolkit;
        var hasSources = _sourceList.Count > 0 && Enumerable.Range(0, _sourceList.Count).Any(i => i < _sources.Items.Count && _sources.GetItemChecked(i));
        if (tk is not null) lines.Add($"Toolkit: {tk.Name}. {tk.Expectation}");
        var input = new LearnInput { Backends = _backends, Toolkit = tk?.Name, Profile = _profile?.Name, HasDataSources = hasSources };
        lines.AddRange(Learner.Expectations(input));
        if (_profile is not null) lines.Add($"Profile '{_profile.Name}': {_profile.Description}{(_profile.Note is null ? "" : " " + _profile.Note)}");
        foreach (var b in _backends.Take(6)) lines.Add("Found: " + b.Describe());
        return string.Join("\r\n\r\n", lines);
    }

    private TabPage DataTab()
    {
        var p = new TabPage("2. Where its data lives");
        var intro = new Label { Text = "App Watcher reads these places (never writes) to see what the app saves. Tick the ones that belong to the app.", Left = 10, Top = 10, Width = 650, Height = 34 };
        _sources.SetBounds(10, 46, 650, 140);
        _sources.ItemCheck += (_, _) => BeginInvoke(() => { if (_toolkit is not null) _expect.Text = ExpectationText(); });
        var scan = new Button { Text = "Scan folders", Left = 10, Top = 192, Width = 100 };
        scan.Click += (_, _) => ScanSources();
        var detect = new Button { Text = "Detect by watching file access (admin once)", Left = 116, Top = 192, Width = 270 };
        detect.Click += (_, _) => DetectByTrace();
        var addFile = new Button { Text = "Add file...", Left = 392, Top = 192, Width = 80 };
        addFile.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Choose a data file of the watched app", Filter = "Data files|*.db;*.sqlite;*.sqlite3;*.mdb;*.accdb;*.xml;*.json;*.csv;*.dbf;*.dat|All files|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var kind = SqliteSource.IsSqlite(d.FileName) ? "sqlite" : d.FileName.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || d.FileName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file";
            AddSource(new DataSourceConfig { Kind = kind, Path = d.FileName, Label = Path.GetFileName(d.FileName) });
        };
        var addFolder = new Button { Text = "Add folder...", Left = 478, Top = 192, Width = 86 };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Choose the folder where the app keeps its data" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "folder", Path = d.SelectedPath });
        };
        var remove = new Button { Text = "Remove", Left = 570, Top = 192, Width = 90 };
        remove.Click += (_, _) => { var i = _sources.SelectedIndex; if (i >= 0) { _sources.Items.RemoveAt(i); _sourceList.RemoveAt(i); } };
        var addSql = new Button { Text = "Add SQL Server (read only)...", Left = 10, Top = 226, Width = 190 };
        addSql.Click += (_, _) => AddSqlServer();
        var addOdbc = new Button { Text = "Add ODBC connection (read only)...", Left = 206, Top = 226, Width = 220 };
        addOdbc.Click += (_, _) => AddOdbc();
        var addDrop = new Button { Text = "Add export drop folder...", Left = 432, Top = 226, Width = 228 };
        addDrop.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Folder where nightly CSV or Excel exports of the key tables are dropped (one file per table, header row first)" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "dropfolder", Path = d.SelectedPath, Label = "exports" });
        };
        _detectStatus.Location = new Point(10, 300);

        // SQL statement capture (opt-in)
        var box = new GroupBox { Text = "SQL statement log (optional, exact evidence)", Left = 10, Top = 318, Width = 650, Height = 150 };
        _sqlEnabled.Location = new Point(10, 22); _sqlEnabled.Checked = Config.SqlCapture.Enabled;
        var l = new Label { Text = "Folder or file:", Left = 10, Top = 50, Width = 90 };
        _sqlLogPath.SetBounds(100, 47, 440, 23); _sqlLogPath.Text = Config.SqlCapture.LogPath ?? "";
        var browse = new Button { Text = "Folder...", Left = 546, Top = 46, Width = 90 };
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Folder with the .xel, .csv or .log files (or the folder holding SQL.LOG)" }; if (d.ShowDialog(this) == DialogResult.OK) _sqlLogPath.Text = d.SelectedPath; };
        _odbcTrace.Location = new Point(10, 78); _odbcTrace.Checked = Config.SqlCapture.OdbcTracingOn;
        var warn = new Label { Left = 10, Top = 100, Width = 630, Height = 44, ForeColor = Color.Firebrick, Text =
            "ODBC tracing is a Windows setting for this user account: it logs the SQL of EVERY program that uses ODBC under this account, slows them down, and the log grows. " +
            "App Watcher keeps only the lines of the watched program. Turn it off when done. DBA scripts for SQL Server, MySQL, PostgreSQL, Oracle and UniVerse are in scripts\\sql." };
        box.Controls.AddRange(new Control[] { _sqlEnabled, l, _sqlLogPath, browse, _odbcTrace, warn });
        var note = new Label { Text = "Read only. Databases over the size cap are read partly (only tables that changed). Cheap change signals (file time/size, SQLite data_version, SQL Server change tracking) decide what is re-read. An ODBC source reads through the driver the app already uses; a drop folder reads CSV/XLSX exports.", Left = 10, Top = 474, Width = 650, Height = 56, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _sources, scan, detect, addFile, addFolder, remove, addSql, addOdbc, addDrop, _backendLabel, _detectStatus, box, note });
        return p;
    }

    private TabPage LearningTab()
    {
        var p = new TabPage("3. Learning");
        var y = 14;
        Control[] Row(string label, Control c, string? hint = null)
        {
            var l = new Label { Text = label, Left = 10, Top = y + 3, Width = 220 };
            c.Left = 236; c.Top = y;
            var list = new List<Control> { l, c };
            if (hint is not null) list.Add(new Label { Text = hint, Left = 236 + c.Width + 8, Top = y + 3, Width = 400 - c.Width, ForeColor = SystemColors.GrayText });
            y += 32;
            return list.ToArray();
        }
        _learnAt.Text = Config.LearnAt;
        _triggers.Text = string.Join(", ", Config.EffectTriggers);
        _maxDbMb.Value = Math.Max(1, Config.Snapshot.MaxDbBytes / (1024 * 1024));
        _maxRows.Value = Math.Clamp(Config.Snapshot.MaxRowsPerTable, 100, 10_000_000);
        _afterMs.Value = Math.Clamp(Config.Snapshot.AfterDelayMs, 200, 30_000);
        _retention.Value = Math.Clamp(Config.RetentionDays, 0, 3650);
        p.Controls.AddRange(Row("Learn every night at (HH:mm):", _learnAt, "also from the tray: Learn now"));
        p.Controls.AddRange(Row("Buttons that trigger a data check:", _triggers));
        p.Controls.AddRange(Row("Database size cap (MB):", _maxDbMb, "bigger: only recently changed tables"));
        p.Controls.AddRange(Row("Rows per table cap:", _maxRows, "bigger tables are skipped"));
        p.Controls.AddRange(Row("Wait after a save before reading (ms):", _afterMs));
        p.Controls.AddRange(Row("Keep raw events for (days, 0 = forever):", _retention, "learned days older than this are deleted"));
        _msaa.Location = new Point(10, y + 6); _msaa.Checked = Config.Collector.MsaaFallback;
        _infer.Location = new Point(10, y + 30); _infer.Checked = Config.Collector.PressInference;
        _tabs.Location = new Point(10, y + 54); _tabs.Checked = Config.Collector.TabDocuments;
        var export = new Button { Text = "Export these settings as a profile (for the next site with the same program)", Left = 10, Top = y + 84, Width = 480 };
        export.Click += (_, _) =>
        {
            if (!Apply()) return;
            var name = (Config.App?.DisplayName ?? "profile").ToLowerInvariant();
            var path = ProfileStore.Save(AppProfile.FromConfig(Config, name, Config.Toolkit));
            Status($"Profile written: {path}. Copy it to profiles\\ in the repository or to the same folder on another PC.", error: false);
        };
        var note = new Label { Left = 10, Top = y + 120, Width = 650, Height = 190, Text =
            "What learning does, in plain words: it groups what users did into flows (with the presses and typed fields on each screen), matches values typed on a screen with the columns that changed " +
            "(a mapping is 'seen' after 3 distinct values or one captured SQL statement, else 'candidate'), finds links between tables, tests formulas on calculated " +
            "columns (copy, sum of child rows, product, percentage, linear fit; the first that explains 95% wins), turns message boxes after a typed value or a button into rules, " +
            "orders status values into a state diagram (rare and not-allowed transitions marked), spots numbering sequences, infers each field's type from what was typed, " +
            "and writes memory\\*.md with a confidence on every line, plus health.md with what the collector could not do and what would improve accuracy. " +
            "Learning is incremental; 'Rebuild memory' in the tray recomputes from day one. No AI, no network." };
        p.Controls.AddRange(new Control[] { _msaa, _infer, _tabs, export, note });
        return p;
    }

    private TabPage PrivacyTab()
    {
        var p = new TabPage("4. Privacy");
        _maskTitles.Location = new Point(10, 14); _maskTitles.Checked = Config.Privacy.MaskTitles;
        var ls = new Label { Text = "Pictures of the app's window (never the desktop, never login windows):", Left = 10, Top = 40, Width = 650 };
        _shotMode.Items.AddRange(new object[] { "off: no pictures", "first: one picture per screen, the first time it is seen", "actions: one per state (after each press, tab, typed value, dialog, suggestion list), no duplicates, capped" });
        _shotMode.Location = new Point(10, 60);
        _shotMode.SelectedIndex = Config.Privacy.EffectiveShotMode switch { "off" => 0, "first" => 1, _ => 2 };
        var l1 = new Label { Text = "Windows never recorded at all (one title pattern per line, * is a wildcard; for example Payroll*, *HR*):", Left = 10, Top = 92, Width = 650 };
        _exclude.Location = new Point(10, 114); _exclude.Text = string.Join("\r\n", Config.Privacy.ExcludeWindows);
        var l2 = new Label { Text = "Only these windows are recorded (leave empty to record every window of the app):", Left = 10, Top = 194, Width = 650 };
        _allow.Location = new Point(10, 216); _allow.Text = string.Join("\r\n", Config.Privacy.AllowlistWindows);
        var preview = new Button { Text = "Show what we store (newest screen record and the last events)", Left = 10, Top = 298, Width = 400 };
        preview.Click += (_, _) => ShowStored();
        var note = new Label { Left = 10, Top = 334, Width = 650, Height = 170, Text =
            "What is stored: window titles (quoted parts masked when the box above is ticked), control names and ids, button presses, values typed into the app's own fields " +
            "(passwords and password-like fields as ***), suggestion lists that open while typing, what a grid shows after a search, read-only before/after copies of the app's data, " +
            "and pictures of the app's window in screens\\shots\\<screen>\\ (one per state, never the same state twice, capped per screen; a picture shows whatever data was on that window). " +
            "The memory files never contain typed values; the probes in an export show " +
            "them with personal-looking values masked.\r\n\r\n" +
            "What is never stored: other programs, the clipboard, screenshots, keystrokes outside the app, anything while another program is in front, anything over the network.\r\n\r\n" +
            "Excluded windows: no event, no screen record and no value is written while such a window is in front. Allowlist: when set, everything outside the listed windows is treated as excluded." };
        p.Controls.AddRange(new Control[] { _maskTitles, ls, _shotMode, l1, _exclude, l2, _allow, preview, note });
        return p;
    }

    private void ShowStored()
    {
        var app = _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
        var paths = Paths.ForApp(app);
        var sb = new System.Text.StringBuilder();
        try
        {
            var screen = Directory.Exists(paths.Screens) ? Directory.GetFiles(paths.Screens, "*.md").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;
            sb.AppendLine(screen is null ? "No screen record yet." : $"=== Newest screen record: {screen} ===\r\n{File.ReadAllText(screen)}");
            var ev = Directory.Exists(paths.Events) ? Directory.GetFiles(paths.Events, "*.jsonl").OrderByDescending(f => f).FirstOrDefault() : null;
            if (ev is null) sb.AppendLine("\r\nNo events yet.");
            else
            {
                var lines = Core.Events.EventReader.ReadFile(ev).TakeLast(30).ToList();
                sb.AppendLine($"\r\n=== Last {lines.Count} events of {Path.GetFileName(ev)} ===");
                foreach (var e in lines) sb.AppendLine($"{e.T:HH:mm:ss} {e.Act,-10} {e.Win} | {e.Ct} {e.Name}{(e.Val is null ? "" : " = " + e.Val)}");
            }
        }
        catch (Exception ex) { sb.AppendLine("Could not read: " + ex.Message); }
        using var f = new Form { Text = "What App Watcher stores for " + app, ClientSize = new Size(820, 560), StartPosition = FormStartPosition.CenterParent };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = sb.ToString().Replace("\n", "\r\n").Replace("\r\r", "\r") };
        f.Controls.Add(box);
        f.ShowDialog(this);
    }

    private TabPage AiTab()
    {
        var p = new TabPage("5. AI analysis (optional)");
        _apiEnabled.Location = new Point(10, 14); _apiEnabled.Checked = Config.Api.Enabled;
        var l1 = new Label { Text = "Claude API key:", Left = 10, Top = 46, Width = 120 };
        _apiKey.SetBounds(136, 43, 420, 23); _apiKey.Text = Dpapi.Unprotect(Config.Api.ApiKeyProtected) ?? "";
        var l2 = new Label { Text = "Model:", Left = 10, Top = 76, Width = 120 };
        _apiModel.SetBounds(136, 73, 200, 23); _apiModel.Text = Config.Api.Model;

        _localEnabled.Location = new Point(10, 116); _localEnabled.Checked = Config.Local.Enabled;
        var l3 = new Label { Text = "Endpoint:", Left = 10, Top = 148, Width = 120 };
        _localEndpoint.SetBounds(136, 145, 300, 23); _localEndpoint.Text = Config.Local.Endpoint;
        var l4 = new Label { Text = "Model name:", Left = 10, Top = 178, Width = 120 };
        _localModel.SetBounds(136, 175, 200, 23); _localModel.Text = Config.Local.Model;

        var show = new Button { Text = "Show exactly what is sent", Left = 10, Top = 212, Width = 200 };
        show.Click += (_, _) => ShowPayload();
        var runApi = new Button { Text = "Run Claude API now", Left = 216, Top = 212, Width = 150 };
        runApi.Click += async (_, _) => await RunNow(local: false);
        var runLocal = new Button { Text = "Run local model now", Left = 372, Top = 212, Width = 150 };
        runLocal.Click += async (_, _) => await RunNow(local: true);
        _apiStatus.Location = new Point(10, 248);
        _apiStatus.Text = $"Claude API: {(Config.Api.LastRun is null ? "never run" : $"{Config.Api.LastRun:yyyy-MM-dd HH:mm}: {Config.Api.LastResult}")}. Local: {(Config.Local.LastRun is null ? "never run" : $"{Config.Local.LastRun:yyyy-MM-dd HH:mm}: {Config.Local.LastResult}")}";
        var note = new Label { Left = 10, Top = 290, Width = 650, Height = 150, Text =
            "Both are off by default. When on, only the files in memory\\ (data-model.md, screens.md, rules.md, flows.md, architecture.md, health.md, changelog.md) are sent, " +
            "together with the analyst prompt. Never raw events, never values typed by users, never your data. The answer is saved as memory\\architecture-reviewed.md " +
            "(the local model's answer as architecture-reviewed-local.md when both are on). Keys are stored protected (Windows DPAPI).\r\n\r\n" +
            "The local option talks to an OpenAI-compatible server on this PC or your network (Ollama default http://localhost:11434/v1; start it with 'ollama serve' and pull a model first).\r\n\r\n" +
            "Without either, run the same analysis in Claude Code, OpenCode or OpenClaw with the app-watcher-analyze skill (see skills\\README.md)." };
        p.Controls.AddRange(new Control[] { _apiEnabled, l1, _apiKey, l2, _apiModel, _localEnabled, l3, _localEndpoint, l4, _localModel, show, runApi, runLocal, _apiStatus, note });
        return p;
    }

    private TabPage PasswordTab()
    {
        var p = new TabPage(_firstRun ? "6. Settings password" : "6. Change password");
        var intro = new Label { Text = _firstRun ? "Choose a password. It is needed to open settings or to quit App Watcher." : "Leave empty to keep the current password.", Left = 10, Top = 14, Width = 600 };
        var l1 = new Label { Text = "Password:", Left = 10, Top = 50, Width = 120 };
        _pw1.SetBounds(136, 47, 200, 23);
        var l2 = new Label { Text = "Repeat:", Left = 10, Top = 82, Width = 120 };
        _pw2.SetBounds(136, 79, 200, 23);
        var note = new Label { Text = "Stored as a salted hash, protected with Windows DPAPI. If it is lost, delete %ProgramData%\\AppWatcher\\config.json and set up again (data folders are kept).", Left = 10, Top = 116, Width = 650, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, l1, _pw1, l2, _pw2, note });
        return p;
    }

    private TabPage AboutTab()
    {
        var p = new TabPage("About");
        var t = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text =
            $"App Watcher {WatcherConfig.AppVersion}\r\n\r\n" +
            "Watches one chosen program and learns the logic behind it: fields, tables, rules, flows.\r\n\r\n" +
            "Recorded: control names, button presses (UI Automation, MSAA, menus, or inferred from focus and a window change), values typed in the watched program (passwords masked), window titles, and read-only before/after snapshots of its data.\r\n" +
            "Never recorded: other programs, the clipboard, screenshots, keystrokes outside the watched program, anything over the network.\r\n" +
            "Never shown: file dialogs or message boxes on its own. This window, the tray menu and short tray notices are the only windows.\r\n\r\n" +
            $"Settings file: {Paths.ConfigFile}\r\nData folder: {Paths.DataRoot}\r\nProfiles: {ProfileStore.UserFolder}\r\nLog: {Paths.LogFile}\r\n\r\n" +
            "MIT License." };
        p.Controls.Add(t);
        return p;
    }

    // ---------- helpers ----------

    private void RefreshProcesses()
    {
        _processes.Items.Clear();
        var self = Environment.ProcessId;
        foreach (var pr in Process.GetProcesses().OrderBy(x => x.ProcessName))
        {
            try
            {
                if (pr.Id == self || pr.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(pr.MainWindowTitle)) continue;
                string path = "";
                try { path = pr.MainModule?.FileName ?? ""; } catch { }
                var it = new ListViewItem(new[] { pr.ProcessName, pr.MainWindowTitle, path });
                _processes.Items.Add(it);
                if (Config.App is not null && string.Equals(pr.ProcessName, Config.App.ProcessName, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
            }
            catch { }
            finally { pr.Dispose(); }
        }
    }

    private void LoadSources()
    {
        _sources.Items.Clear(); _sourceList.Clear();
        foreach (var s in Config.DataSources) { _sourceList.Add(s); _sources.Items.Add(s.Describe(), s.Enabled); }
        if (_sourceList.Count == 0 && (!string.IsNullOrEmpty(_exePath.Text) || !string.IsNullOrEmpty(_displayName.Text))) ScanSources();
    }

    private void ScanSources()
    {
        UseWaitCursor = true;
        try
        {
            var name = _displayName.Text.Length > 0 ? _displayName.Text : Path.GetFileNameWithoutExtension(_exePath.Text);
            var found = DataSourceScanner.Scan(_exePath.Text, name, name);
            var added = 0;
            foreach (var c in found.Take(60))
            {
                if (_sourceList.Any(s => string.Equals(s.Path, c.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var cfg = DataSourceScanner.ToConfig(c);
                cfg.Enabled = c.Kind is "sqlite" or "access";
                _sourceList.Add(cfg);
                _sources.Items.Add($"{cfg.Describe()}  ({c.Size / 1024} KB, {c.Why})", cfg.Enabled);
                added++;
            }
            Status(found.Count == 0 ? "No data files found in the usual places. Add the file, folder, ODBC or SQL Server connection by hand, or use the detect button." : $"Scan found {found.Count} candidate files ({added} new).", error: found.Count == 0);
        }
        finally { UseWaitCursor = false; }
    }

    /// <summary>Starts an elevated copy of this exe that records the files the watched process touches for two minutes (ETW, one process id only).</summary>
    private void DetectByTrace()
    {
        var processName = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Text : Path.GetFileNameWithoutExtension(_exePath.Text);
        if (string.IsNullOrWhiteSpace(processName)) { Status("Pick the program first (tab 1)."); return; }
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) { Status($"{processName} is not running. Start it, then try again."); return; }
        var pid = procs[0].Id;
        foreach (var pr in procs) pr.Dispose();
        var outFile = Path.Combine(Paths.Root, "file-trace.json");
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var portable = Paths.RootOverride is not null ? " --portable" : "";
        const int seconds = 120;
        Process? elevated;
        try
        {
            elevated = Process.Start(new ProcessStartInfo(exe, $"--trace-files {pid} {seconds} \"{outFile}\"{portable}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Win32Exception)
        {
            Status("Administrator rights were declined. Falling back to folder scanning.", error: false);
            ScanSources();
            return;
        }
        if (elevated is null) { Status("Could not start the trace."); return; }
        _detectStatus.Text = $"Watching file access of {processName} for {seconds} s... use the app normally.";
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            if (!elevated.HasExited) return;
            timer.Stop(); timer.Dispose();
            _detectStatus.Text = "";
            if (!File.Exists(outFile)) { Status("The trace produced no result (see the log)."); return; }
            var files = FileAccessTrace.Load(outFile);
            var added = 0;
            foreach (var (path, e) in files.OrderByDescending(f => f.Value.Writes))
            {
                if (!File.Exists(path)) continue;
                var ext = Path.GetExtension(path);
                if (!DataSourceScanner.DataExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && e.Writes == 0) continue;
                if (_sourceList.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var kind = SqliteSource.IsSqlite(path) ? "sqlite" : ext.Equals(".mdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file";
                var cfg = new DataSourceConfig { Kind = kind, Path = path, Label = Path.GetFileName(path), Enabled = e.Writes > 0 };
                _sourceList.Add(cfg);
                _sources.Items.Add($"{cfg.Describe()}  (reads {e.Reads}, writes {e.Writes}, from file access trace)", cfg.Enabled);
                added++;
            }
            try { File.Delete(outFile); } catch { }
            Status($"File access trace proposed {added} new data sources (ticked: written by the app).", error: false);
        };
        timer.Start();
    }

    private void AddSource(DataSourceConfig cfg)
    {
        _sourceList.Add(cfg);
        _sources.Items.Add(cfg.Describe(), true);
        if (_toolkit is not null) _expect.Text = ExpectationText();
    }

    private void AddSqlServer()
    {
        using var f = new Form { Text = "SQL Server connection (read only)", ClientSize = new Size(520, 170), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var l = new Label { Text = "Connection string (a read-only login is best):", Left = 10, Top = 10, Width = 500 };
        var hint = _backends.FirstOrDefault(b => b.Kind == "sqlserver");
        var box = new TextBox { Left = 10, Top = 34, Width = 500, Text = hint is null ? @"Server=.\SQLEXPRESS;Database=MyApp;Integrated Security=true;TrustServerCertificate=true" : $"Server={hint.Host ?? "."}{(hint.Port is null ? "" : "," + hint.Port)};Database={hint.Database ?? "MyApp"};Integrated Security=true;TrustServerCertificate=true" };
        var label = new Label { Text = "Name:", Left = 10, Top = 68, Width = 60 };
        var name = new TextBox { Left = 70, Top = 65, Width = 200, Text = "main database" };
        var result = new Label { Left = 10, Top = 98, Width = 500, Height = 20, ForeColor = SystemColors.GrayText };
        var test = new Button { Text = "Test", Left = 10, Top = 125, Width = 80 };
        var ok = new Button { Text = "Add", Left = 350, Top = 125, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 435, Top = 125, Width = 75, DialogResult = DialogResult.Cancel };
        test.Click += (_, _) => { var err = SqlServerSource.TestConnection(box.Text); result.Text = err ?? "Connected."; };
        f.Controls.AddRange(new Control[] { l, box, label, name, result, test, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(box.Text)) return;
        AddSource(new DataSourceConfig { Kind = "sqlserver", ConnectionStringProtected = Dpapi.Protect(box.Text), Label = name.Text });
    }

    /// <summary>C1: any engine with an ODBC driver (UniVerse, Pervasive, Progress, MySQL, PostgreSQL, Oracle, ...). DSNs come from the registry.</summary>
    private void AddOdbc()
    {
        using var f = new Form { Text = "ODBC connection (read only)", ClientSize = new Size(560, 240), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var l0 = new Label { Text = "Data source (DSN) on this PC:", Left = 10, Top = 12, Width = 200 };
        var dsn = new ComboBox { Left = 210, Top = 9, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
        var dsns = BackendDetector.FromOdbc();
        dsn.Items.Add("(type a connection string below)");
        foreach (var d in dsns) dsn.Items.Add($"{d.Database?.Replace("DSN ", "")}  [{d.Driver}]");
        dsn.SelectedIndex = 0;
        var l = new Label { Text = "Connection string (DSN=...;UID=readonly;PWD=...; or a driver string):", Left = 10, Top = 42, Width = 540 };
        var box = new TextBox { Left = 10, Top = 64, Width = 540, Text = "DSN=MyApp;UID=readonly;PWD=" };
        dsn.SelectedIndexChanged += (_, _) => { if (dsn.SelectedIndex > 0) box.Text = $"DSN={dsns[dsn.SelectedIndex - 1].Database?.Replace("DSN ", "").Split(" (")[0]};UID=readonly;PWD="; };
        var l2 = new Label { Text = "Tables to read (comma separated; empty = all, capped at 400):", Left = 10, Top = 96, Width = 540 };
        var tables = new TextBox { Left = 10, Top = 118, Width = 540 };
        var label = new Label { Text = "Name:", Left = 10, Top = 152, Width = 60 };
        var name = new TextBox { Left = 70, Top = 149, Width = 200, Text = "odbc database" };
        var result = new Label { Left = 280, Top = 152, Width = 270, Height = 40, ForeColor = SystemColors.GrayText };
        var test = new Button { Text = "Test", Left = 10, Top = 200, Width = 80 };
        var ok = new Button { Text = "Add", Left = 390, Top = 200, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 475, Top = 200, Width = 75, DialogResult = DialogResult.Cancel };
        test.Click += (_, _) => { var err = OdbcSource.TestConnection(box.Text); result.Text = err ?? "Connected."; };
        f.Controls.AddRange(new Control[] { l0, dsn, l, box, l2, tables, label, name, result, test, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(box.Text)) return;
        AddSource(new DataSourceConfig { Kind = "odbc", ConnectionStringProtected = Dpapi.Protect(box.Text), Label = name.Text, Path = string.IsNullOrWhiteSpace(tables.Text) ? null : tables.Text.Trim() });
    }

    private void ShowPayload()
    {
        var app = _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
        var memory = Paths.ForApp(app).Memory;
        var text = AnalystPrompt.BuildUserMessage(memory);
        using var f = new Form { Text = "Exactly what is sent (Claude API or local model)", ClientSize = new Size(760, 520), StartPosition = FormStartPosition.CenterParent };
        var head = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8), Text = $"POST {Config.Api.Endpoint} (model {_apiModel.Text}) or {_localEndpoint.Text}/chat/completions (model {_localModel.Text}). One user message with this text plus a one-line system prompt. Nothing else." };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = text.Replace("\n", "\r\n") };
        f.Controls.Add(box); f.Controls.Add(head);
        f.ShowDialog(this);
    }

    private async Task RunNow(bool local)
    {
        var app = _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
        var memory = Paths.ForApp(app).Memory;
        if (!Directory.Exists(memory) || Directory.GetFiles(memory, "*.md").Length == 0) { Status("No memory files yet. Use 'Learn now' from the tray first."); return; }
        _apiStatus.Text = "Running...";
        try
        {
            string status;
            if (local)
            {
                var settings = new LocalAnalysisSettings { Endpoint = _localEndpoint.Text.Trim(), Model = _localModel.Text.Trim(), ApiKeyProtected = Config.Local.ApiKeyProtected };
                status = await new LocalLlmClient(settings).AnalyzeAndSaveAsync(memory, Config.Api.Enabled || _apiEnabled.Checked ? "architecture-reviewed-local.md" : "architecture-reviewed.md");
                Config.Local.LastRun = DateTime.Now; Config.Local.LastResult = status;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_apiKey.Text)) { Status("Enter an API key first."); _apiStatus.Text = ""; return; }
                var settings = new ApiSettings { Model = _apiModel.Text, Endpoint = Config.Api.Endpoint, MaxTokens = Config.Api.MaxTokens };
                status = await new ClaudeApiClient(settings, _apiKey.Text.Trim()).AnalyzeAndSaveAsync(memory);
                Config.Api.LastRun = DateTime.Now; Config.Api.LastResult = status;
            }
            _apiStatus.Text = $"Done: {status}";
        }
        catch (Exception ex)
        {
            if (local) { Config.Local.LastRun = DateTime.Now; Config.Local.LastResult = "failed: " + ex.Message; }
            else { Config.Api.LastRun = DateTime.Now; Config.Api.LastResult = "failed: " + ex.Message; }
            _apiStatus.Text = "Failed: " + ex.Message;
        }
    }

    private static List<string> Lines(TextBox t) => t.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private bool Apply()
    {
        // app
        if (string.IsNullOrWhiteSpace(_exePath.Text) && _processes.SelectedItems.Count == 0 && Config.App is null)
        { Status("Pick the program to watch on the first tab."); return false; }
        var appChanged = false;
        if (_processes.SelectedItems.Count > 0 || !string.IsNullOrWhiteSpace(_exePath.Text))
        {
            var processName = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Text : Path.GetFileNameWithoutExtension(_exePath.Text.Trim());
            var display = string.IsNullOrWhiteSpace(_displayName.Text) ? processName : _displayName.Text.Trim();
            if (Config.App is null || Config.App.ProcessName != processName || Config.App.DisplayName != display)
            { Config.App = new WatchedApp { ProcessName = processName, ExePath = _exePath.Text.Trim(), DisplayName = display, ChosenAt = DateTime.Now }; appChanged = true; }
            else Config.App.ExePath = _exePath.Text.Trim();
        }
        // discovery results travel with the config (no credentials in them); the profile is applied when the app is new
        if (_toolkit is not null) Config.Toolkit = _toolkit.Name;
        if (_backends.Count > 0 || appChanged) Config.Backends = _backends;
        if (_profile is not null && (appChanged || Config.Profile is null))
        {
            _profile.ApplyTo(Config);
            _triggers.Text = string.Join(", ", Config.EffectTriggers);
            _msaa.Checked = Config.Collector.MsaaFallback; _infer.Checked = Config.Collector.PressInference; _tabs.Checked = Config.Collector.TabDocuments;
            _maskTitles.Checked = Config.Privacy.MaskTitles; _shotMode.SelectedIndex = Config.Privacy.EffectiveShotMode switch { "off" => 0, "first" => 1, _ => 2 };
            _exclude.Text = string.Join("\r\n", Config.Privacy.ExcludeWindows);
        }
        // password
        if (_pw1.Text.Length > 0 || _firstRun)
        {
            if (_pw1.Text.Length < 4) { Status("The password needs at least 4 characters (tab 6)."); return false; }
            if (_pw1.Text != _pw2.Text) { Status("The two passwords differ (tab 6)."); return false; }
            Config.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(_pw1.Text));
        }
        // data
        for (var i = 0; i < _sourceList.Count; i++) _sourceList[i].Enabled = _sources.GetItemChecked(i);
        Config.DataSources = _sourceList.ToList();
        Config.SqlCapture.Enabled = _sqlEnabled.Checked;
        Config.SqlCapture.LogPath = string.IsNullOrWhiteSpace(_sqlLogPath.Text) ? null : _sqlLogPath.Text.Trim();
        if (Config.SqlCapture.Enabled && Config.SqlCapture.LogPath is null && !_odbcTrace.Checked) { Status("SQL statement log is on but no folder or file is set (tab 2)."); return false; }
        if (_odbcTrace.Checked != Config.SqlCapture.OdbcTracingOn)
        {
            var err = OdbcTracing.Set(_odbcTrace.Checked, out var logPath);
            if (err is not null) { Status("ODBC tracing could not be changed: " + err); return false; }
            Config.SqlCapture.OdbcTracingOn = _odbcTrace.Checked;
            if (_odbcTrace.Checked) { Config.SqlCapture.LogPath ??= logPath; Config.SqlCapture.Enabled = true; }
        }
        // learning and collector
        if (!TimeOnly.TryParseExact(_learnAt.Text.Trim(), "HH:mm", out _)) { Status("Learn time must look like 02:00 (tab 3)."); return false; }
        Config.LearnAt = _learnAt.Text.Trim();
        Config.EffectTriggers = _triggers.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Config.Snapshot.MaxDbBytes = (long)_maxDbMb.Value * 1024 * 1024;
        Config.Snapshot.MaxRowsPerTable = (int)_maxRows.Value;
        Config.Snapshot.AfterDelayMs = (int)_afterMs.Value;
        Config.RetentionDays = (int)_retention.Value;
        Config.Collector.MsaaFallback = _msaa.Checked;
        Config.Collector.PressInference = _infer.Checked;
        Config.Collector.TabDocuments = _tabs.Checked;
        // privacy
        Config.Privacy.MaskTitles = _maskTitles.Checked;
        Config.Privacy.ScreenshotMode = _shotMode.SelectedIndex switch { 0 => "off", 1 => "first", _ => "actions" };
        Config.Privacy.Screenshots = _shotMode.SelectedIndex != 0;
        Config.Privacy.ExcludeWindows = Lines(_exclude);
        Config.Privacy.AllowlistWindows = Lines(_allow);
        // ai
        Config.Api.Enabled = _apiEnabled.Checked;
        Config.Api.Model = string.IsNullOrWhiteSpace(_apiModel.Text) ? "claude-sonnet-5-5" : _apiModel.Text.Trim();
        Config.Api.ApiKeyProtected = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : Dpapi.Protect(_apiKey.Text.Trim());
        if (Config.Api.Enabled && Config.Api.ApiKeyProtected is null) { Status("Claude API analysis is on but no API key is set (tab 5)."); return false; }
        Config.Local.Enabled = _localEnabled.Checked;
        Config.Local.Endpoint = string.IsNullOrWhiteSpace(_localEndpoint.Text) ? "http://localhost:11434/v1" : _localEndpoint.Text.Trim();
        Config.Local.Model = string.IsNullOrWhiteSpace(_localModel.Text) ? "llama3.1" : _localModel.Text.Trim();
        return true;
    }

    private static WatcherConfig Clone(WatcherConfig c) =>
        System.Text.Json.JsonSerializer.Deserialize<WatcherConfig>(System.Text.Json.JsonSerializer.Serialize(c, WatcherConfig.JsonOptions), WatcherConfig.JsonOptions)!;
}

/// <summary>Windows ODBC tracing for the current user (HKCU\Software\ODBC\ODBC.INI\ODBC). Machine-wide in effect for this account's programs.</summary>
internal static class OdbcTracing
{
    public static string? Set(bool on, out string logPath)
    {
        logPath = Path.Combine(Path.GetTempPath(), "SQL.LOG");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\ODBC\ODBC.INI\ODBC", writable: true);
            if (key is null) return "registry key not available";
            key.SetValue("Trace", on ? "1" : "0", RegistryValueKind.String);
            if (on) key.SetValue("TraceFile", logPath, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
