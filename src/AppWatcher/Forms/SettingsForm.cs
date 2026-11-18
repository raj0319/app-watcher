using System.ComponentModel;
using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Sources;
using Microsoft.Win32;

namespace AppWatcher.Forms;

/// <summary>
/// First-run wizard and later settings window. Plain controls, no designer file.
/// No message boxes: every validation message shows in the status line at the bottom. The only pickers are the
/// file and folder pickers for data sources, which the user opens on purpose.
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
    // Data tab
    private readonly CheckedListBox _sources = new() { CheckOnClick = true };
    private readonly List<DataSourceConfig> _sourceList = new();
    private readonly TextBox _sqlLogPath = new() { Width = 360 };
    private readonly CheckBox _sqlEnabled = new() { Text = "Use SQL statement log (Extended Events .xel / CSV folder, or ODBC SQL.LOG)", AutoSize = true };
    private readonly CheckBox _odbcTrace = new() { Text = "Turn on Windows ODBC tracing for this user (writes %TEMP%\\SQL.LOG)", AutoSize = true };
    private readonly Label _detectStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    // Learning tab
    private readonly TextBox _learnAt = new() { Width = 60 };
    private readonly TextBox _triggers = new() { Width = 420 };
    private readonly NumericUpDown _maxDbMb = new() { Minimum = 1, Maximum = 100_000, Width = 90 };
    private readonly NumericUpDown _maxRows = new() { Minimum = 100, Maximum = 10_000_000, Width = 110, Increment = 1000 };
    private readonly NumericUpDown _afterMs = new() { Minimum = 200, Maximum = 30_000, Width = 90, Increment = 100 };
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
        ClientSize = new Size(660, 540);
        MinimumSize = new Size(620, 500);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(AppTab());
        tabs.TabPages.Add(DataTab());
        tabs.TabPages.Add(LearningTab());
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
        Load += (_, _) => { RefreshProcesses(); LoadSources(); };
    }

    private void Status(string text, bool error = true) { _statusLine.ForeColor = error ? Color.Firebrick : SystemColors.ControlText; _statusLine.Text = text; }

    // ---------- tabs ----------

    private TabPage AppTab()
    {
        var p = new TabPage("1. App to watch");
        var intro = new Label { Text = "Pick the program App Watcher should learn from. Only this program is ever watched.", Left = 10, Top = 10, Width = 620, Height = 20 };
        _processes.SetBounds(10, 36, 630, 240);
        _processes.Columns.Add("Program", 140); _processes.Columns.Add("Window title", 300); _processes.Columns.Add("Path", 180);
        _processes.SelectedIndexChanged += (_, _) =>
        {
            if (_processes.SelectedItems.Count == 0) return;
            var it = _processes.SelectedItems[0];
            _exePath.Text = it.SubItems[2].Text;
            _displayName.Text = it.Text;
        };
        var refresh = new Button { Text = "Refresh list", Left = 10, Top = 282, Width = 100 };
        refresh.Click += (_, _) => RefreshProcesses();
        var l1 = new Label { Text = "Program file (or type/paste the full path of the .exe):", Left = 10, Top = 318, Width = 330 };
        _exePath.SetBounds(10, 340, 630, 23);
        _exePath.TextChanged += (_, _) => { if (_processes.SelectedItems.Count == 0 && _exePath.Text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) _displayName.Text = Path.GetFileNameWithoutExtension(_exePath.Text); };
        var l2 = new Label { Text = "Name for its data folder:", Left = 10, Top = 374, Width = 150 };
        _displayName.SetBounds(166, 371, 200, 23);
        var note = new Label { Text = "Changing the program later starts a new data folder; the old one is kept.", Left = 10, Top = 404, Width = 620, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _processes, refresh, l1, _exePath, l2, _displayName, note });
        if (Config.App is not null) { _exePath.Text = Config.App.ExePath ?? ""; _displayName.Text = Config.App.DisplayName; }
        return p;
    }

    private TabPage DataTab()
    {
        var p = new TabPage("2. Where its data lives");
        var intro = new Label { Text = "App Watcher reads these places (never writes) to see what the app saves. Tick the ones that belong to the app.", Left = 10, Top = 10, Width = 630, Height = 34 };
        _sources.SetBounds(10, 46, 630, 150);
        var scan = new Button { Text = "Scan folders", Left = 10, Top = 202, Width = 100 };
        scan.Click += (_, _) => ScanSources();
        var detect = new Button { Text = "Detect by watching file access (admin once)", Left = 116, Top = 202, Width = 270 };
        detect.Click += (_, _) => DetectByTrace();
        var addFile = new Button { Text = "Add file...", Left = 392, Top = 202, Width = 80 };
        addFile.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Choose a data file of the watched app", Filter = "Data files|*.db;*.sqlite;*.sqlite3;*.mdb;*.accdb;*.xml;*.json;*.csv;*.dbf;*.dat|All files|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var kind = SqliteSource.IsSqlite(d.FileName) ? "sqlite" : d.FileName.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || d.FileName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file";
            AddSource(new DataSourceConfig { Kind = kind, Path = d.FileName, Label = Path.GetFileName(d.FileName) });
        };
        var addFolder = new Button { Text = "Add folder...", Left = 478, Top = 202, Width = 86 };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Choose the folder where the app keeps its data" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "folder", Path = d.SelectedPath });
        };
        var remove = new Button { Text = "Remove", Left = 570, Top = 202, Width = 70 };
        remove.Click += (_, _) => { var i = _sources.SelectedIndex; if (i >= 0) { _sources.Items.RemoveAt(i); _sourceList.RemoveAt(i); } };
        var addSql = new Button { Text = "Add SQL Server connection (read only)...", Left = 10, Top = 232, Width = 250 };
        addSql.Click += (_, _) => AddSqlServer();
        _detectStatus.Location = new Point(270, 237);

        // SQL statement capture (opt-in)
        var box = new GroupBox { Text = "SQL statement log (optional, exact evidence)", Left = 10, Top = 266, Width = 630, Height = 150 };
        _sqlEnabled.Location = new Point(10, 22); _sqlEnabled.Checked = Config.SqlCapture.Enabled;
        var l = new Label { Text = "Folder or file:", Left = 10, Top = 50, Width = 90 };
        _sqlLogPath.SetBounds(100, 47, 420, 23); _sqlLogPath.Text = Config.SqlCapture.LogPath ?? "";
        var browse = new Button { Text = "Folder...", Left = 526, Top = 46, Width = 90 };
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Folder with the .xel or .csv files (or the folder holding SQL.LOG)" }; if (d.ShowDialog(this) == DialogResult.OK) _sqlLogPath.Text = d.SelectedPath; };
        _odbcTrace.Location = new Point(10, 78); _odbcTrace.Checked = Config.SqlCapture.OdbcTracingOn;
        var warn = new Label { Left = 10, Top = 100, Width = 610, Height = 44, ForeColor = Color.Firebrick, Text =
            "ODBC tracing is a Windows setting for this user account: it logs the SQL of EVERY program that uses ODBC under this account, slows them down, and the log grows. " +
            "App Watcher keeps only the lines of the watched program. Turn it off when done. For SQL Server prefer scripts\\sql\\xevents-session.sql (DBA runs it once)." };
        box.Controls.AddRange(new Control[] { _sqlEnabled, l, _sqlLogPath, browse, _odbcTrace, warn });
        var note = new Label { Text = "Read only. Databases over the size cap are read partly (only tables that changed). Cheap change signals (file time/size, SQLite data_version, SQL Server change tracking) decide what is re-read.", Left = 10, Top = 420, Width = 630, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _sources, scan, detect, addFile, addFolder, remove, addSql, _detectStatus, box, note });
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
            if (hint is not null) list.Add(new Label { Text = hint, Left = 236 + c.Width + 8, Top = y + 3, Width = 380 - c.Width, ForeColor = SystemColors.GrayText });
            y += 32;
            return list.ToArray();
        }
        _learnAt.Text = Config.LearnAt;
        _triggers.Text = string.Join(", ", Config.EffectTriggers);
        _maxDbMb.Value = Math.Max(1, Config.Snapshot.MaxDbBytes / (1024 * 1024));
        _maxRows.Value = Math.Clamp(Config.Snapshot.MaxRowsPerTable, 100, 10_000_000);
        _afterMs.Value = Math.Clamp(Config.Snapshot.AfterDelayMs, 200, 30_000);
        p.Controls.AddRange(Row("Learn every night at (HH:mm):", _learnAt, "also from the tray: Learn now"));
        p.Controls.AddRange(Row("Buttons that trigger a data check:", _triggers));
        p.Controls.AddRange(Row("Database size cap (MB):", _maxDbMb, "bigger: only recently changed tables"));
        p.Controls.AddRange(Row("Rows per table cap:", _maxRows, "bigger tables are skipped"));
        p.Controls.AddRange(Row("Wait after a save before reading (ms):", _afterMs));
        var note = new Label { Left = 10, Top = y + 10, Width = 630, Height = 160, Text =
            "What learning does, in plain words: it groups what users did into flows, matches values typed on a screen with the columns that changed " +
            "(a mapping is 'seen' after 3 distinct values or one captured SQL statement, else 'candidate'), finds links between tables, tests formulas on calculated " +
            "columns (copy, sum of child rows, product, percentage, linear fit; the first that explains 95% wins), turns message boxes after a typed value into validation rules, " +
            "orders status values into a state diagram (rare and not-allowed transitions marked), spots numbering sequences, and writes memory\\*.md with a confidence on every line, " +
            "plus health.md with what would improve accuracy. Learning is incremental (only new sessions are processed); 'Rebuild memory' in the tray recomputes from day one. No AI, no network." };
        p.Controls.Add(note);
        return p;
    }

    private TabPage AiTab()
    {
        var p = new TabPage("4. AI analysis (optional)");
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
        var note = new Label { Left = 10, Top = 290, Width = 630, Height = 150, Text =
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
        var p = new TabPage(_firstRun ? "5. Settings password" : "5. Change password");
        var intro = new Label { Text = _firstRun ? "Choose a password. It is needed to open settings or to quit App Watcher." : "Leave empty to keep the current password.", Left = 10, Top = 14, Width = 600 };
        var l1 = new Label { Text = "Password:", Left = 10, Top = 50, Width = 120 };
        _pw1.SetBounds(136, 47, 200, 23);
        var l2 = new Label { Text = "Repeat:", Left = 10, Top = 82, Width = 120 };
        _pw2.SetBounds(136, 79, 200, 23);
        var note = new Label { Text = "Stored as a salted hash, protected with Windows DPAPI. If it is lost, delete %ProgramData%\\AppWatcher\\config.json and set up again (data folders are kept).", Left = 10, Top = 116, Width = 630, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, l1, _pw1, l2, _pw2, note });
        return p;
    }

    private TabPage AboutTab()
    {
        var p = new TabPage("About");
        var t = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text =
            "App Watcher 0.2\r\n\r\n" +
            "Watches one chosen program and learns the logic behind it: fields, tables, rules, flows.\r\n\r\n" +
            "Recorded: control names, button presses, values typed in the watched program (passwords masked), window titles, and read-only before/after snapshots of its data.\r\n" +
            "Never recorded: other programs, the clipboard, screenshots, keystrokes outside the watched program, anything over the network.\r\n" +
            "Never shown: file dialogs or message boxes. This window, the tray menu and short tray notices are the only windows.\r\n\r\n" +
            $"Settings file: {Paths.ConfigFile}\r\nData folder: {Paths.DataRoot}\r\nLog: {Paths.LogFile}\r\n\r\n" +
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
            Status(found.Count == 0 ? "No data files found in the usual places. Add the file, folder or SQL Server connection by hand, or use the detect button." : $"Scan found {found.Count} candidate files ({added} new).", error: found.Count == 0);
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
    }

    private void AddSqlServer()
    {
        using var f = new Form { Text = "SQL Server connection (read only)", ClientSize = new Size(520, 170), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var l = new Label { Text = "Connection string (a read-only login is best):", Left = 10, Top = 10, Width = 500 };
        var box = new TextBox { Left = 10, Top = 34, Width = 500, Text = @"Server=.\SQLEXPRESS;Database=MyApp;Integrated Security=true;TrustServerCertificate=true" };
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

    private bool Apply()
    {
        // app
        if (string.IsNullOrWhiteSpace(_exePath.Text) && _processes.SelectedItems.Count == 0 && Config.App is null)
        { Status("Pick the program to watch on the first tab."); return false; }
        if (_processes.SelectedItems.Count > 0 || !string.IsNullOrWhiteSpace(_exePath.Text))
        {
            var processName = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Text : Path.GetFileNameWithoutExtension(_exePath.Text.Trim());
            var display = string.IsNullOrWhiteSpace(_displayName.Text) ? processName : _displayName.Text.Trim();
            if (Config.App is null || Config.App.ProcessName != processName || Config.App.DisplayName != display)
                Config.App = new WatchedApp { ProcessName = processName, ExePath = _exePath.Text.Trim(), DisplayName = display, ChosenAt = DateTime.Now };
            else Config.App.ExePath = _exePath.Text.Trim();
        }
        // password
        if (_pw1.Text.Length > 0 || _firstRun)
        {
            if (_pw1.Text.Length < 4) { Status("The password needs at least 4 characters (tab 5)."); return false; }
            if (_pw1.Text != _pw2.Text) { Status("The two passwords differ (tab 5)."); return false; }
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
        // learning
        if (!TimeOnly.TryParseExact(_learnAt.Text.Trim(), "HH:mm", out _)) { Status("Learn time must look like 02:00 (tab 3)."); return false; }
        Config.LearnAt = _learnAt.Text.Trim();
        Config.EffectTriggers = _triggers.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Config.Snapshot.MaxDbBytes = (long)_maxDbMb.Value * 1024 * 1024;
        Config.Snapshot.MaxRowsPerTable = (int)_maxRows.Value;
        Config.Snapshot.AfterDelayMs = (int)_afterMs.Value;
        // ai
        Config.Api.Enabled = _apiEnabled.Checked;
        Config.Api.Model = string.IsNullOrWhiteSpace(_apiModel.Text) ? "claude-sonnet-5-5" : _apiModel.Text.Trim();
        Config.Api.ApiKeyProtected = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : Dpapi.Protect(_apiKey.Text.Trim());
        if (Config.Api.Enabled && Config.Api.ApiKeyProtected is null) { Status("Claude API analysis is on but no API key is set (tab 4)."); return false; }
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
