using System.ComponentModel;
using System.Diagnostics;
using AppWatcher.Collector;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Setup;
using AppWatcher.Core.Sources;
using Microsoft.Win32;

namespace AppWatcher.Forms;

/// <summary>
/// Settings, opened from the tray after the password. Two views:
/// Simple - the three privacy choices, the watched program with a Change button, the learn time, and what has been
///          collected so far in plain words (the same text as health.md).
/// Advanced - everything else (data sources, SQL capture, collector switches, AI, profiles, retention), each page with a
///          "Reset this page to recommended" link and the recommended value marked.
/// No message boxes: messages go to the status line at the bottom. The only pickers are the ones the user opens.
/// </summary>
internal sealed class SettingsForm : Form
{
    public WatcherConfig Config { get; }
    private readonly bool _firstRun;
    private readonly Action? _rebuild;
    private readonly Label _statusLine = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 3, 8, 0), ForeColor = Color.Firebrick };

    // view switch
    private readonly RadioButton _simpleView = new() { Text = "Simple", Appearance = Appearance.Button, Width = 100, Height = 28, TextAlign = ContentAlignment.MiddleCenter, Checked = true };
    private readonly RadioButton _advancedView = new() { Text = "Advanced", Appearance = Appearance.Button, Width = 100, Height = 28, TextAlign = ContentAlignment.MiddleCenter };
    private readonly LinkLabel _reset = new() { Text = "Reset this page to recommended", AutoSize = true, Visible = false };
    private readonly Panel _simple = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Visible = false };

    // simple view
    private readonly Label _sProgram = new() { AutoSize = false, Width = 470, Height = 40, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold) };
    private readonly Button _sChange = new() { Text = "Change...", Width = 110, Height = 28 };
    private readonly CheckBox _sMask = new() { Text = "Hide names and numbers that appear in window titles", AutoSize = true };
    private readonly CheckBox _sShots = new() { Text = "Keep pictures of the program's screens, never the desktop", AutoSize = true };
    private readonly CheckBox _sOcr = new() { Text = "Read the text on those pictures so I can learn from programs that draw their own screens", AutoSize = true };
    private readonly TextBox _sNever = new() { Width = 600 };
    private readonly DateTimePicker _sLearn = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private readonly Label _sCollected = new() { AutoSize = false, Width = 620, Height = 170, BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Info, Padding = new Padding(8) };

    // advanced: app page
    private readonly ListView _processes = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly TextBox _exePath = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _expect = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Info };
    private readonly System.Windows.Forms.Timer _analyzeTimer = new() { Interval = 600 };
    private string _analyzedExe = "";
    private string? _processOverride;
    private SetupPlan? _plan;
    // advanced: data page
    private readonly CheckedListBox _sources = new() { CheckOnClick = true };
    private readonly List<DataSourceConfig> _sourceList = new();
    private readonly TextBox _sqlLogPath = new() { Width = 360 };
    private readonly CheckBox _sqlEnabled = new() { Text = "Use a SQL statement log (recommended: off unless IT sets one up)", AutoSize = true };
    private readonly CheckBox _odbcTrace = new() { Text = "Turn on Windows ODBC tracing for this user (recommended: off)", AutoSize = true };
    private readonly Label _detectStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _backendLabel = new() { Left = 10, Top = 266, Width = 660, Height = 34, ForeColor = SystemColors.GrayText };
    // advanced: learning page
    private readonly TextBox _learnAt = new() { Width = 60 };
    private readonly TextBox _triggers = new() { Width = 360 };
    private readonly NumericUpDown _maxDbMb = new() { Minimum = 1, Maximum = 100_000, Width = 90 };
    private readonly NumericUpDown _maxRows = new() { Minimum = 100, Maximum = 10_000_000, Width = 110, Increment = 1000 };
    private readonly NumericUpDown _afterMs = new() { Minimum = 200, Maximum = 30_000, Width = 90, Increment = 100 };
    private readonly NumericUpDown _retention = new() { Minimum = 0, Maximum = 3650, Width = 90 };
    private readonly CheckBox _msaa = new() { Text = "Read screens the older way when the modern way shows almost nothing (recommended)", AutoSize = true };
    private readonly CheckBox _infer = new() { Text = "Count a button or menu focus followed by a window change as a click (recommended)", AutoSize = true };
    private readonly CheckBox _tabs2 = new() { Text = "Record each selected tab as a screen of its own, Window › Tab (recommended)", AutoSize = true };
    // advanced: privacy page
    private readonly CheckBox _maskTitles = new() { Text = "Mask quoted parts of window titles and tab labels (\"ACME INC\" becomes \"…\") (recommended)", AutoSize = true };
    private readonly ComboBox _shotMode = new() { Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _readText = new() { Text = "Read the text on each picture with the built-in Windows OCR; stays on this PC (recommended)", AutoSize = true };
    private readonly TextBox _exclude = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Width = 630 };
    private readonly TextBox _allow = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Width = 630 };
    // advanced: AI page
    private readonly CheckBox _apiEnabled = new() { Text = "Claude API: send the memory files after each nightly learn (recommended: off)", AutoSize = true };
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true, Width = 420 };
    private readonly TextBox _apiModel = new() { Width = 200 };
    private readonly CheckBox _localEnabled = new() { Text = "Local model (Ollama or similar): same analysis, nothing leaves this PC (recommended: off)", AutoSize = true };
    private readonly TextBox _localEndpoint = new() { Width = 300 };
    private readonly TextBox _localModel = new() { Width = 200 };
    private readonly Label _apiStatus = new() { AutoSize = true };
    // advanced: password page
    private readonly TextBox _pw1 = new() { UseSystemPasswordChar = true, Width = 200 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, Width = 200 };

    public SettingsForm(WatcherConfig config, bool firstRun, Action? requestRebuild = null)
    {
        Config = Clone(config);
        _firstRun = firstRun;
        _rebuild = requestRebuild;
        Text = "App Watcher: settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 660);
        MinimumSize = new Size(660, 600);
        Font = SystemFonts.MessageBoxFont!;

        _tabs.TabPages.Add(AppTab());
        _tabs.TabPages.Add(DataTab());
        _tabs.TabPages.Add(LearningTab());
        _tabs.TabPages.Add(PrivacyTab());
        _tabs.TabPages.Add(AiTab());
        _tabs.TabPages.Add(PasswordTab());
        _tabs.TabPages.Add(AboutTab());
        _tabs.SelectedIndexChanged += (_, _) => UpdateResetLink();
        BuildSimple();

        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 6, 10, 4) };
        _simpleView.Location = new Point(10, 6); _advancedView.Location = new Point(112, 6);
        _reset.Location = new Point(440, 12); _reset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _reset.LinkClicked += (_, _) => ResetPage();
        top.Controls.AddRange(new Control[] { _simpleView, _advancedView, _reset });
        _simpleView.CheckedChanged += (_, _) => { if (_simpleView.Checked) ShowSimple(); };
        _advancedView.CheckedChanged += (_, _) => { if (_advancedView.Checked) ShowAdvanced(); };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 28 };
        var ok = new Button { Text = "Save", Width = 110, Height = 28 };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        bar.Controls.Add(cancel); bar.Controls.Add(ok);
        Controls.Add(_tabs); Controls.Add(_simple); Controls.Add(top); Controls.Add(_statusLine); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        _analyzeTimer.Tick += (_, _) => { _analyzeTimer.Stop(); AnalyzeProgram(); };
        Load += (_, _) =>
        {
            RefreshProcesses();
            LoadSources();
            if (_exePath.Text.Length > 0) AnalyzeProgram();
            AdvancedToSimple();
            RefreshCollected();
        };
    }

    /// <summary>For --render-ui (pictures for the docs): show the Simple view, or the Advanced view on a page.</summary>
    internal void ShowView(bool advanced, int page = 0)
    {
        if (advanced) { _advancedView.Checked = true; _tabs.SelectedIndex = page; ShowAdvanced(); }
        else { _simpleView.Checked = true; ShowSimple(); }
    }

    private string AppName => _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
    private void Status(string text, bool error = true) { _statusLine.ForeColor = error ? Color.Firebrick : SystemColors.ControlText; _statusLine.Text = text; }

    // ---------- simple view ----------

    private void BuildSimple()
    {
        var bold = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Bold);
        var y = 12;
        Label H(string t) { var l = new Label { Text = t, Left = 16, Top = y, AutoSize = true, Font = bold }; y += 24; return l; }
        var h1 = H("The program I watch");
        _sProgram.Location = new Point(16, y); _sChange.Location = new Point(500, y + 4);
        _sChange.Click += (_, _) => ChangeProgram();
        y += 48;
        var h2 = H("Privacy");
        _sMask.Location = new Point(16, y); y += 26;
        _sShots.Location = new Point(16, y); y += 26;
        _sOcr.Location = new Point(16, y); y += 30;
        var ln = new Label { Text = "Windows I must never record (for example Payroll), separated by commas:", Left = 16, Top = y, AutoSize = true }; y += 22;
        _sNever.Location = new Point(16, y); y += 36;
        var h3 = H("When I update what I learned");
        var le = new Label { Text = "Every night at", Left = 16, Top = y + 3, AutoSize = true };
        _sLearn.Location = new Point(120, y);
        var lh = new Label { Text = "Also one hour after the start, and whenever you choose \"Update what I learned\" near the clock.", Left = 16, Top = y + 30, AutoSize = true, ForeColor = SystemColors.GrayText };
        y += 58;
        var h4 = H("What has been collected so far");
        _sCollected.Location = new Point(16, y);
        _simple.Controls.AddRange(new Control[] { h1, _sProgram, _sChange, h2, _sMask, _sShots, _sOcr, ln, _sNever, h3, le, _sLearn, lh, h4, _sCollected });
    }

    private void ShowSimple() { AdvancedToSimple(); RefreshCollected(); _tabs.Visible = false; _simple.Visible = true; UpdateResetLink(); }
    private void ShowAdvanced() { SimpleToAdvanced(); _simple.Visible = false; _tabs.Visible = true; UpdateResetLink(); }

    /// <summary>The simple controls and the advanced ones show the same settings; the visible view is the source.</summary>
    private void SimpleToAdvanced()
    {
        _maskTitles.Checked = _sMask.Checked;
        if (_sShots.Checked && _shotMode.SelectedIndex == 0) _shotMode.SelectedIndex = 2;
        if (!_sShots.Checked) _shotMode.SelectedIndex = 0;
        _readText.Checked = _sOcr.Checked;
        _exclude.Text = string.Join("\r\n", SplitList(_sNever.Text));
        _learnAt.Text = _sLearn.Value.ToString("HH:mm");
    }

    private void AdvancedToSimple()
    {
        _sMask.Checked = _maskTitles.Checked;
        _sShots.Checked = _shotMode.SelectedIndex != 0;
        _sOcr.Checked = _readText.Checked;
        _sNever.Text = string.Join(", ", SplitList(_exclude.Text));
        _sLearn.Value = TimeOnly.TryParseExact(_learnAt.Text.Trim(), "HH:mm", out var t) ? DateTime.Today.Add(t.ToTimeSpan()) : DateTime.Today.AddHours(2);
        _sProgram.Text = AppName.Length > 0 ? AppName : "(no program chosen yet)";
    }

    private static List<string> SplitList(string s) => s.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void RefreshCollected()
    {
        if (AppName.Length == 0) { _sCollected.Text = "Nothing yet: choose the program to watch."; return; }
        try
        {
            var facts = PlainReport.FromDisk(Paths.ForApp(Config.App?.DisplayName ?? AppName), Config, AppName);
            _sCollected.Text = string.Join("\r\n\r\n", PlainReport.Lines(facts, withLastLearn: true));
        }
        catch (Exception ex) { _sCollected.Text = "I could not read what was collected: " + ex.Message; }
    }

    private void ChangeProgram()
    {
        using var f = new Form { Text = "Which program should I watch?", ClientSize = new Size(660, 560), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = Font };
        var picker = new ProgramPicker { Dock = DockStyle.Fill, PreselectProcess = Config.App?.ProcessName };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = new Button { Text = "Watch this program", Width = 160, Height = 28, Enabled = false, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        bar.Controls.Add(ok); bar.Controls.Add(cancel);
        picker.Changed += () => ok.Enabled = picker.HasPick;
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        pad.Controls.Add(picker);
        f.Controls.Add(pad); f.Controls.Add(bar);
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || !picker.HasPick) return;
        _plan = picker.PlanNow();
        _processes.SelectedItems.Clear();
        _processOverride = picker.ProcessName;
        _exePath.Text = picker.ExePath ?? "";
        _displayName.Text = picker.FriendlyName;
        _analyzedExe = _exePath.Text;
        _expect.Text = SetupDefaults.WhatIWillLearn(_plan);
        ReplaceSources(SetupDefaults.DefaultSources(_plan.Candidates));
        _sProgram.Text = picker.FriendlyName;
        Status($"I will watch {picker.FriendlyName} once you press Save. Its old folder is kept; a new one starts.", error: false);
    }

    // ---------- advanced view: reset to recommended ----------

    private void UpdateResetLink() => _reset.Visible = _tabs.Visible && _tabs.SelectedIndex is 1 or 2 or 3 or 4;

    private void ResetPage()
    {
        var profile = ProfileStore.ByName(Config.Profile);
        var rec = new WatcherConfig();
        profile?.ApplyTo(rec);
        switch (_tabs.SelectedIndex)
        {
            case 1:
                var plan = _plan ?? (AppName.Length > 0 ? SetupDefaults.Prepare(_exePath.Text.Length > 0 ? _exePath.Text : null, ProcessNameNow(), AppName) : null);
                if (plan is not null) ReplaceSources(SetupDefaults.DefaultSources(plan.Candidates));
                _sqlEnabled.Checked = false; _odbcTrace.Checked = false; _sqlLogPath.Text = "";
                break;
            case 2:
                _learnAt.Text = rec.LearnAt;
                _triggers.Text = string.Join(", ", rec.EffectTriggers);
                _maxDbMb.Value = rec.Snapshot.MaxDbBytes / (1024 * 1024);
                _maxRows.Value = rec.Snapshot.MaxRowsPerTable;
                _afterMs.Value = rec.Snapshot.AfterDelayMs;
                _retention.Value = rec.RetentionDays;
                _msaa.Checked = rec.Collector.MsaaFallback; _infer.Checked = rec.Collector.PressInference; _tabs2.Checked = rec.Collector.TabDocuments;
                break;
            case 3:
                _maskTitles.Checked = rec.Privacy.MaskTitles; _shotMode.SelectedIndex = 2; _readText.Checked = rec.Privacy.ReadTextFromPictures;
                _exclude.Text = string.Join("\r\n", rec.Privacy.ExcludeWindows); _allow.Text = "";
                break;
            case 4:
                _apiEnabled.Checked = false; _localEnabled.Checked = false;
                break;
        }
        Status("This page is back to the recommended values. Press Save to keep them.", error: false);
    }

    // ---------- advanced pages ----------

    private TabPage AppTab()
    {
        var p = new TabPage("1. App to watch");
        var intro = new Label { Text = "The program App Watcher learns from (process names shown here; the Simple view shows friendly names).", Left = 10, Top = 10, Width = 660, Height = 20 };
        _processes.SetBounds(10, 36, 660, 200);
        _processes.Columns.Add("Program", 140); _processes.Columns.Add("Window title", 310); _processes.Columns.Add("Path", 200);
        _processes.SelectedIndexChanged += (_, _) =>
        {
            if (_processes.SelectedItems.Count == 0) return;
            var it = _processes.SelectedItems[0];
            _processOverride = it.Text;
            _exePath.Text = it.SubItems[2].Text;
            _displayName.Text = SetupDefaults.FriendlyName(it.SubItems[2].Text, it.SubItems[1].Text, it.Text);
            ScheduleAnalyze();
        };
        var refresh = new Button { Text = "Refresh list", Left = 10, Top = 242, Width = 100 };
        refresh.Click += (_, _) => RefreshProcesses();
        var l1 = new Label { Text = "Program file (browse to any .exe on any drive, or type its full path):", Left = 10, Top = 276, Width = 660 };
        _exePath.SetBounds(10, 298, 560, 23);
        _exePath.TextChanged += (_, _) => ScheduleAnalyze();
        var browse = new Button { Text = "Browse...", Left = 578, Top = 297, Width = 92 };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Choose the program App Watcher should learn from", Filter = "Programs|*.exe|All files|*.*", CheckFileExists = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _processes.SelectedItems.Clear();
            _processOverride = Path.GetFileNameWithoutExtension(d.FileName);
            _exePath.Text = d.FileName;
            _displayName.Text = SetupDefaults.FriendlyName(d.FileName, null, _processOverride);
        };
        var l2 = new Label { Text = "Name for its data folder:", Left = 10, Top = 332, Width = 150 };
        _displayName.SetBounds(166, 329, 220, 23);
        var l3 = new Label { Text = "What App Watcher expects to learn from this program:", Left = 10, Top = 364, Width = 660 };
        _expect.SetBounds(10, 386, 660, 140);
        p.Controls.AddRange(new Control[] { intro, _processes, refresh, l1, _exePath, browse, l2, _displayName, l3, _expect });
        if (Config.App is not null) { _exePath.Text = Config.App.ExePath ?? ""; _displayName.Text = Config.App.DisplayName; _processOverride = Config.App.ProcessName; _analyzedExe = _exePath.Text; }
        return p;
    }

    private string ProcessNameNow() =>
        _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Text
        : !string.IsNullOrEmpty(_processOverride) ? _processOverride!
        : Path.GetFileNameWithoutExtension(_exePath.Text.Trim());

    private void ScheduleAnalyze() { _analyzeTimer.Stop(); _analyzeTimer.Start(); }

    private void AnalyzeProgram()
    {
        var exe = _exePath.Text.Trim();
        if (exe.Length == 0 || exe == _analyzedExe && _plan is not null) return;
        var changed = Config.App is null || !string.Equals(exe, Config.App.ExePath ?? "", StringComparison.OrdinalIgnoreCase);
        _analyzedExe = exe;
        _expect.Text = "Looking at the program file...";
        var proc = ProcessNameNow();
        var name = AppName;
        Task.Run(() => SetupDefaults.Prepare(File.Exists(exe) ? exe : null, proc, name)).ContinueWith(t =>
        {
            if (t.IsFaulted || IsDisposed) { if (!IsDisposed) _expect.Text = "Could not look at the program: " + t.Exception?.GetBaseException().Message; return; }
            _plan = t.Result;
            _expect.Text = SetupDefaults.WhatIWillLearn(_plan) + "\r\n\r\n" + SetupDefaults.Details(_plan);
            var own = _plan.Backends.Where(b => !b.Where.Contains("ODBC DSN", StringComparison.OrdinalIgnoreCase)).ToList();
            _backendLabel.Text = own.Count == 0 ? "No server or database connection of this program was found." : "This program talks to: " + string.Join("; ", own.Take(4).Select(b => b.Describe()));
            if (changed) ReplaceSources(SetupDefaults.DefaultSources(_plan.Candidates));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private TabPage DataTab()
    {
        var p = new TabPage("2. Where its data lives");
        var intro = new Label { Text = "Places App Watcher reads (never writes) to see what the program saves. Everything found on this PC is ticked (recommended).", Left = 10, Top = 8, Width = 660, Height = 36 };
        _sources.SetBounds(10, 48, 660, 140);
        var scan = new Button { Text = "Scan folders", Left = 10, Top = 194, Width = 110, Height = 28 };
        scan.Click += (_, _) => ScanSources();
        var detect = new Button { Text = "Find what it uses (admin, 2 min)", Left = 126, Top = 194, Width = 230, Height = 28 };
        detect.Click += (_, _) => DetectByTrace();
        var addFile = new Button { Text = "Add file...", Left = 362, Top = 194, Width = 95, Height = 28 };
        addFile.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Choose a data file of the watched program", Filter = "Data files|*.db;*.sqlite;*.sqlite3;*.mdb;*.accdb;*.xml;*.json;*.csv;*.dbf;*.dat|All files|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var kind = SqliteSource.IsSqlite(d.FileName) ? "sqlite" : d.FileName.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || d.FileName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file";
            AddSource(new DataSourceConfig { Kind = kind, Path = d.FileName, Label = Path.GetFileName(d.FileName) });
        };
        var addFolder = new Button { Text = "Add folder...", Left = 463, Top = 194, Width = 105, Height = 28 };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Choose the folder where the program keeps its data" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "folder", Path = d.SelectedPath });
        };
        var remove = new Button { Text = "Remove", Left = 574, Top = 194, Width = 96, Height = 28 };
        remove.Click += (_, _) => { var i = _sources.SelectedIndex; if (i >= 0) { _sources.Items.RemoveAt(i); _sourceList.RemoveAt(i); } };
        var addSql = new Button { Text = "Add SQL Server login...", Left = 10, Top = 228, Width = 210, Height = 28 };
        addSql.Click += (_, _) => AddSqlServer();
        var addOdbc = new Button { Text = "Add ODBC connection...", Left = 226, Top = 228, Width = 220, Height = 28 };
        addOdbc.Click += (_, _) => AddOdbc();
        var addDrop = new Button { Text = "Add export drop folder...", Left = 452, Top = 228, Width = 218, Height = 28 };
        addDrop.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Folder where nightly CSV or Excel exports of the key tables are dropped (one file per table, header row first)" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "dropfolder", Path = d.SelectedPath, Label = "exports" });
        };
        _detectStatus.Location = new Point(10, 300);
        var box = new GroupBox { Text = "SQL statement log (optional, exact evidence; set up by IT)", Left = 10, Top = 318, Width = 660, Height = 156 };
        _sqlEnabled.Location = new Point(10, 22); _sqlEnabled.Checked = Config.SqlCapture.Enabled;
        var l = new Label { Text = "Folder or file:", Left = 10, Top = 50, Width = 100 };
        _sqlLogPath.SetBounds(112, 47, 430, 23); _sqlLogPath.Text = Config.SqlCapture.LogPath ?? "";
        var browse = new Button { Text = "Folder...", Left = 550, Top = 46, Width = 96 };
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Folder with the .xel, .csv or .log files (or the folder holding SQL.LOG)" }; if (d.ShowDialog(this) == DialogResult.OK) _sqlLogPath.Text = d.SelectedPath; };
        _odbcTrace.Location = new Point(10, 78); _odbcTrace.Checked = Config.SqlCapture.OdbcTracingOn;
        var warn = new Label { Left = 10, Top = 102, Width = 640, Height = 48, ForeColor = Color.Firebrick, Text =
            "ODBC tracing logs the SQL of every program that uses ODBC under this account and slows them down. " +
            "Turn it off when done. DBA scripts for each database are in scripts\\sql." };
        box.Controls.AddRange(new Control[] { _sqlEnabled, l, _sqlLogPath, browse, _odbcTrace, warn });
        var note = new Label { Text = "Read only, always. Database and ODBC connections need a login from IT and stay off unless someone adds one here.", Left = 10, Top = 480, Width = 660, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _sources, scan, detect, addFile, addFolder, remove, addSql, addOdbc, addDrop, _backendLabel, _detectStatus, box, note });
        return p;
    }

    private TabPage LearningTab()
    {
        var p = new TabPage("3. Learning");
        var intro = new Label { Text = "How often App Watcher updates what it learned, and how it reads the program. The recommended values are marked.", Left = 10, Top = 8, Width = 660, Height = 36 };
        var y = 46;
        Control[] Row(string label, Control c, string? hint = null)
        {
            var lb = new Label { Text = label, Left = 10, Top = y + 3, Width = 296 };
            c.Left = 310; c.Top = y;
            var list = new List<Control> { lb, c };
            if (hint is not null) list.Add(new Label { Text = hint, Left = 310 + c.Width + 8, Top = y + 3, Width = 360 - c.Width, ForeColor = SystemColors.GrayText });
            y += 32;
            return list.ToArray();
        }
        _learnAt.Text = Config.LearnAt;
        _triggers.Text = string.Join(", ", Config.EffectTriggers);
        _maxDbMb.Value = Math.Clamp(Config.Snapshot.MaxDbBytes / (1024 * 1024), 1, 100_000);
        _maxRows.Value = Math.Clamp(Config.Snapshot.MaxRowsPerTable, 100, 10_000_000);
        _afterMs.Value = Math.Clamp(Config.Snapshot.AfterDelayMs, 200, 30_000);
        _retention.Value = Math.Clamp(Config.RetentionDays, 0, 3650);
        p.Controls.AddRange(Row("Update what I learned every night at:", _learnAt, "recommended 02:00"));
        p.Controls.AddRange(Row("Buttons that mean \"save\":", _triggers));
        p.Controls.AddRange(Row("Largest database read in full (MB):", _maxDbMb, "recommended 256"));
        p.Controls.AddRange(Row("Rows per table cap:", _maxRows, "recommended 50000"));
        p.Controls.AddRange(Row("Wait after a save before reading (ms):", _afterMs, "recommended 2000"));
        p.Controls.AddRange(Row("Keep raw events for (days, 0 = forever):", _retention, "recommended 180"));
        _msaa.Location = new Point(10, y + 6); _msaa.Checked = Config.Collector.MsaaFallback;
        _infer.Location = new Point(10, y + 30); _infer.Checked = Config.Collector.PressInference;
        _tabs2.Location = new Point(10, y + 54); _tabs2.Checked = Config.Collector.TabDocuments;
        var rebuild = new Button { Text = "Recompute everything I learned from day one", Left = 10, Top = y + 88, Width = 340, Height = 28 };
        rebuild.Click += (_, _) =>
        {
            if (_rebuild is null) { Status("Recomputing is only possible while App Watcher runs in the tray."); return; }
            _rebuild();
            Status("Recomputing everything I learned; a notice near the clock tells you when it is done.", error: false);
        };
        var export = new Button { Text = "Export these settings as a profile", Left = 360, Top = y + 88, Width = 260, Height = 28 };
        export.Click += (_, _) =>
        {
            if (!Apply()) return;
            var name = Paths.SafeName(Config.App?.DisplayName ?? "profile");
            var path = ProfileStore.Save(AppProfile.FromConfig(Config, name, Config.Toolkit));
            Status($"Profile saved as {Path.GetFileName(path)} in the App Watcher profiles folder; copy it to the next PC with the same program.", error: false);
        };
        var note = new Label { Left = 10, Top = y + 124, Width = 660, Height = 120, ForeColor = SystemColors.GrayText, Text =
            "What learning does: it groups what users did into flows, matches values typed on a screen with the data that changed after a save, finds links between tables, " +
            "tests formulas on calculated columns, turns message boxes into rules, orders status values, spots numbering sequences, and writes memory\\*.md with a confidence on every line. " +
            "No AI and no network are involved." };
        p.Controls.AddRange(new Control[] { intro, _msaa, _infer, _tabs2, rebuild, export, note });
        return p;
    }

    private TabPage PrivacyTab()
    {
        var p = new TabPage("4. Privacy");
        _maskTitles.Location = new Point(10, 14); _maskTitles.Checked = Config.Privacy.MaskTitles;
        var ls = new Label { Text = "Pictures of the program's window (never the desktop, never login windows):", Left = 10, Top = 42, Width = 660 };
        _shotMode.Items.AddRange(new object[] { "off: no pictures", "first: one picture per screen, the first time it is seen", "actions: one per state, no duplicates, capped (recommended)" });
        _shotMode.Location = new Point(10, 62);
        _shotMode.SelectedIndex = Config.Privacy.EffectiveShotMode switch { "off" => 0, "first" => 1, _ => 2 };
        _readText.Location = new Point(10, 90); _readText.Checked = Config.Privacy.ReadTextFromPictures;
        var l1 = new Label { Text = "Windows never recorded at all, one per line (* is a wildcard, for example Payroll*):", Left = 10, Top = 118, Width = 660 };
        _exclude.Location = new Point(10, 140); _exclude.Text = string.Join("\r\n", Config.Privacy.ExcludeWindows);
        var l2 = new Label { Text = "Only record these windows (empty = every window of the program; recommended):", Left = 10, Top = 220, Width = 660 };
        _allow.Location = new Point(10, 242); _allow.Text = string.Join("\r\n", Config.Privacy.AllowlistWindows);
        var preview = new Button { Text = "Show what is stored", Left = 10, Top = 324, Width = 200, Height = 28 };
        preview.Click += (_, _) => ShowStored();
        var note = new Label { Left = 10, Top = 360, Width = 660, Height = 150, ForeColor = SystemColors.GrayText, Text =
            "Stored: window titles (quoted parts masked when ticked), control names, clicks, values typed into the program's own fields (passwords as ***), suggestion lists, " +
            "what a grid shows after a search, read-only before/after copies of the program's data, and pictures of the program's window with the text read from them. " +
            "Never stored: other programs, the clipboard, the desktop, keystrokes outside the program, anything while another program is in front. Nothing is sent over the network." };
        p.Controls.AddRange(new Control[] { _maskTitles, ls, _shotMode, _readText, l1, _exclude, l2, _allow, preview, note });
        return p;
    }

    private void ShowStored()
    {
        var paths = Paths.ForApp(AppName);
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
        using var f = new Form { Text = "What App Watcher stores for " + AppName, ClientSize = new Size(820, 560), StartPosition = FormStartPosition.CenterParent };
        f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = sb.ToString().Replace("\n", "\r\n").Replace("\r\r", "\r") });
        f.ShowDialog(this);
    }

    private TabPage AiTab()
    {
        var p = new TabPage("5. AI analysis");
        var intro = new Label { Text = "Optional and off by default: what App Watcher learns stays on this PC unless you switch one on.", Left = 10, Top = 8, Width = 660, Height = 22 };
        _apiEnabled.Location = new Point(10, 34); _apiEnabled.Checked = Config.Api.Enabled;
        var l1 = new Label { Text = "Claude API key:", Left = 10, Top = 66, Width = 120 };
        _apiKey.SetBounds(136, 63, 420, 23); _apiKey.Text = Dpapi.Unprotect(Config.Api.ApiKeyProtected) ?? "";
        var l2 = new Label { Text = "Model:", Left = 10, Top = 96, Width = 120 };
        _apiModel.SetBounds(136, 93, 200, 23); _apiModel.Text = Config.Api.Model;
        _localEnabled.Location = new Point(10, 136); _localEnabled.Checked = Config.Local.Enabled;
        var l3 = new Label { Text = "Endpoint:", Left = 10, Top = 168, Width = 120 };
        _localEndpoint.SetBounds(136, 165, 300, 23); _localEndpoint.Text = Config.Local.Endpoint;
        var l4 = new Label { Text = "Model name:", Left = 10, Top = 198, Width = 120 };
        _localModel.SetBounds(136, 195, 200, 23); _localModel.Text = Config.Local.Model;
        var show = new Button { Text = "Show exactly what is sent", Left = 10, Top = 232, Width = 200, Height = 28 };
        show.Click += (_, _) => ShowPayload();
        var runApi = new Button { Text = "Run Claude API now", Left = 216, Top = 232, Width = 170, Height = 28 };
        runApi.Click += async (_, _) => await RunNow(local: false);
        var runLocal = new Button { Text = "Run local model now", Left = 392, Top = 232, Width = 170, Height = 28 };
        runLocal.Click += async (_, _) => await RunNow(local: true);
        _apiStatus.Location = new Point(10, 268);
        _apiStatus.Text = $"Claude API: {(Config.Api.LastRun is null ? "never run" : $"{Config.Api.LastRun:yyyy-MM-dd HH:mm}: {Config.Api.LastResult}")}. Local: {(Config.Local.LastRun is null ? "never run" : $"{Config.Local.LastRun:yyyy-MM-dd HH:mm}: {Config.Local.LastResult}")}";
        var note = new Label { Left = 10, Top = 300, Width = 660, Height = 150, ForeColor = SystemColors.GrayText, Text =
            "When on, only the files in memory\\ are sent, with the analyst prompt: never raw events, typed values, pictures or data. The answer is saved as memory\\architecture-reviewed.md. " +
            "Keys are stored protected (Windows DPAPI). Without either, run the same analysis in Claude Code, OpenCode or OpenClaw with the app-watcher-analyze skill." };
        p.Controls.AddRange(new Control[] { intro, _apiEnabled, l1, _apiKey, l2, _apiModel, _localEnabled, l3, _localEndpoint, l4, _localModel, show, runApi, runLocal, _apiStatus, note });
        return p;
    }

    private TabPage PasswordTab()
    {
        var p = new TabPage("6. Password");
        var intro = new Label { Text = _firstRun ? "Choose a password. It is needed to open settings or to quit App Watcher." : "Leave empty to keep the current password.", Left = 10, Top = 14, Width = 600 };
        var l1 = new Label { Text = "Password:", Left = 10, Top = 50, Width = 120 };
        _pw1.SetBounds(136, 47, 200, 23);
        var l2 = new Label { Text = "Repeat:", Left = 10, Top = 82, Width = 120 };
        _pw2.SetBounds(136, 79, 200, 23);
        var note = new Label { Text = "Stored as a salted hash, protected with Windows DPAPI. If it is lost, delete %ProgramData%\\AppWatcher\\config.json and set up again (collected data is kept).", Left = 10, Top = 116, Width = 660, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, l1, _pw1, l2, _pw2, note });
        return p;
    }

    private TabPage AboutTab()
    {
        var p = new TabPage("About");
        p.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text =
            $"App Watcher {WatcherConfig.AppVersion}\r\n\r\n" +
            "Watches one chosen program and learns how it works: screens, fields, rules, flows.\r\n\r\n" +
            "Recorded: control names, clicks, values typed in the watched program (passwords masked), window titles, suggestion lists, grid contents after a search, pictures of the program's window and the text on them, and read-only before/after copies of its data.\r\n" +
            "Never recorded: other programs, the clipboard, the desktop, keystrokes outside the watched program, anything over the network.\r\n" +
            "Never shown: pop-ups or message boxes on its own. This window, the tray menu and short tray notices are the only windows.\r\n\r\n" +
            $"Settings file: {Paths.ConfigFile}\r\nData folder: {Paths.DataRoot}\r\nProfiles: {ProfileStore.UserFolder}\r\nLog: {Paths.LogFile}\r\n\r\nMIT License." });
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
                _processes.Items.Add(new ListViewItem(new[] { pr.ProcessName, pr.MainWindowTitle, path }));
            }
            catch { }
            finally { pr.Dispose(); }
        }
    }

    private void LoadSources()
    {
        _sources.Items.Clear(); _sourceList.Clear();
        foreach (var s in Config.DataSources) { _sourceList.Add(s); _sources.Items.Add(s.Describe(), s.Enabled); }
        if (_sourceList.Count == 0 && AppName.Length > 0) ScanSources();
    }

    private void ReplaceSources(List<DataSourceConfig> sources)
    {
        // connections someone added by hand (SQL Server, ODBC, drop folders) are kept; scanned files are replaced
        var keep = _sourceList.Where(s => s.Kind is "sqlserver" or "odbc" or "dropfolder").ToList();
        _sources.Items.Clear(); _sourceList.Clear();
        foreach (var s in sources.Concat(keep)) { _sourceList.Add(s); _sources.Items.Add(s.Describe(), s.Enabled); }
    }

    private void ScanSources()
    {
        UseWaitCursor = true;
        try
        {
            var found = DataSourceScanner.Scan(_exePath.Text, ProcessNameNow(), AppName);
            var added = 0;
            foreach (var cfg in SetupDefaults.DefaultSources(found.Take(60)))
            {
                if (_sourceList.Any(s => string.Equals(s.Path, cfg.Path, StringComparison.OrdinalIgnoreCase))) continue;
                _sourceList.Add(cfg);
                _sources.Items.Add(cfg.Describe(), cfg.Enabled);
                added++;
            }
            Status(found.Count == 0 ? "I found no data files in the usual places. That is fine; add one by hand or use the detect button if you know there is one." : $"I found {found.Count} places ({added} new), all ticked.", error: false);
        }
        finally { UseWaitCursor = false; }
    }

    private void DetectByTrace()
    {
        var processName = ProcessNameNow();
        if (string.IsNullOrWhiteSpace(processName)) { Status("Pick the program first (page 1)."); return; }
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) { Status($"{AppName} is not running. Start it, then try again."); return; }
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
        catch (Win32Exception) { Status("Permission was not given. I scanned the usual folders instead.", error: false); ScanSources(); return; }
        if (elevated is null) { Status("Could not start the detection."); return; }
        _detectStatus.Text = $"Watching which files {AppName} uses for {seconds} seconds... use the program normally.";
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            if (!elevated.HasExited) return;
            timer.Stop(); timer.Dispose();
            _detectStatus.Text = "";
            if (!File.Exists(outFile)) { Status("The detection found nothing (see the log)."); return; }
            var tmp = new WatcherConfig { DataSources = _sourceList.ToList() };
            var added = SetupDefaults.AddTracedSources(tmp, FileAccessTrace.Load(outFile).Select(f => (f.Key, f.Value.Reads, f.Value.Writes)));
            foreach (var s in tmp.DataSources.Skip(_sourceList.Count)) { _sourceList.Add(s); _sources.Items.Add($"{s.Describe()}  (seen in use)", true); }
            try { File.Delete(outFile); } catch { }
            Status($"The detection added {added} place{(added == 1 ? "" : "s")} the program uses, all ticked.", error: false);
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
        var hint = (_plan?.Backends ?? Config.Backends).FirstOrDefault(b => b.Kind == "sqlserver");
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
        var text = AnalystPrompt.BuildUserMessage(Paths.ForApp(AppName).Memory);
        using var f = new Form { Text = "Exactly what is sent (Claude API or local model)", ClientSize = new Size(760, 520), StartPosition = FormStartPosition.CenterParent };
        var head = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8), Text = $"POST {Config.Api.Endpoint} (model {_apiModel.Text}) or {_localEndpoint.Text}/chat/completions (model {_localModel.Text}). One user message with this text plus a one-line system prompt. Nothing else." };
        f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = text.Replace("\n", "\r\n") });
        f.Controls.Add(head);
        f.ShowDialog(this);
    }

    private async Task RunNow(bool local)
    {
        var memory = Paths.ForApp(AppName).Memory;
        if (!Directory.Exists(memory) || Directory.GetFiles(memory, "*.md").Length == 0) { Status("Nothing learned yet. Choose \"Update what I learned\" in the tray menu first."); return; }
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
        if (_simple.Visible) SimpleToAdvanced();
        if (string.IsNullOrWhiteSpace(_exePath.Text) && string.IsNullOrEmpty(_processOverride) && Config.App is null)
        { Status("Choose the program to watch first."); return false; }
        var processName = ProcessNameNow();
        var display = string.IsNullOrWhiteSpace(_displayName.Text) ? processName : _displayName.Text.Trim();
        var appChanged = Config.App is null || !string.Equals(Config.App.ProcessName, processName, StringComparison.OrdinalIgnoreCase) || Config.App.DisplayName != display;
        if (appChanged)
        {
            var plan = _plan ?? SetupDefaults.Prepare(File.Exists(_exePath.Text.Trim()) ? _exePath.Text.Trim() : null, processName, display);
            Config.App = new WatchedApp { ProcessName = processName, ExePath = _exePath.Text.Trim(), DisplayName = display, ChosenAt = DateTime.Now };
            Config.Toolkit = plan.Toolkit.Name;
            Config.Backends = plan.Backends;
            Config.Profile = null;
            plan.Profile?.ApplyTo(Config);
            Config.AutoDetectPending = true;
            Config.HealthCheckDone = false;
            Config.FirstStart = DateTime.Now;
        }
        else Config.App!.ExePath = _exePath.Text.Trim();
        if (_pw1.Text.Length > 0 || _firstRun)
        {
            if (_pw1.Text.Length < 4) { Status("The password needs at least 4 characters (Advanced, page 6)."); return false; }
            if (_pw1.Text != _pw2.Text) { Status("The two passwords are not the same (Advanced, page 6)."); return false; }
            Config.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(_pw1.Text));
        }
        for (var i = 0; i < _sourceList.Count; i++) _sourceList[i].Enabled = _sources.GetItemChecked(i);
        Config.DataSources = _sourceList.ToList();
        Config.SqlCapture.Enabled = _sqlEnabled.Checked;
        Config.SqlCapture.LogPath = string.IsNullOrWhiteSpace(_sqlLogPath.Text) ? null : _sqlLogPath.Text.Trim();
        if (Config.SqlCapture.Enabled && Config.SqlCapture.LogPath is null && !_odbcTrace.Checked) { Status("The SQL statement log is on but no folder is set (Advanced, page 2)."); return false; }
        if (_odbcTrace.Checked != Config.SqlCapture.OdbcTracingOn)
        {
            var err = OdbcTracing.Set(_odbcTrace.Checked, out var logPath);
            if (err is not null) { Status("ODBC tracing could not be changed: " + err); return false; }
            Config.SqlCapture.OdbcTracingOn = _odbcTrace.Checked;
            if (_odbcTrace.Checked) { Config.SqlCapture.LogPath ??= logPath; Config.SqlCapture.Enabled = true; }
        }
        if (!TimeOnly.TryParseExact(_learnAt.Text.Trim(), "HH:mm", out _)) { Status("The learn time must look like 02:00 (Advanced, page 3)."); return false; }
        Config.LearnAt = _learnAt.Text.Trim();
        Config.EffectTriggers = _triggers.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Config.Snapshot.MaxDbBytes = (long)_maxDbMb.Value * 1024 * 1024;
        Config.Snapshot.MaxRowsPerTable = (int)_maxRows.Value;
        Config.Snapshot.AfterDelayMs = (int)_afterMs.Value;
        Config.RetentionDays = (int)_retention.Value;
        Config.Collector.MsaaFallback = _msaa.Checked;
        Config.Collector.PressInference = _infer.Checked;
        Config.Collector.TabDocuments = _tabs2.Checked;
        Config.Privacy.MaskTitles = _maskTitles.Checked;
        Config.Privacy.ScreenshotMode = _shotMode.SelectedIndex switch { 0 => "off", 1 => "first", _ => "actions" };
        Config.Privacy.Screenshots = _shotMode.SelectedIndex != 0;
        Config.Privacy.ReadTextFromPictures = _readText.Checked;
        Config.Privacy.ExcludeWindows = Lines(_exclude);
        Config.Privacy.AllowlistWindows = Lines(_allow);
        Config.Api.Enabled = _apiEnabled.Checked;
        Config.Api.Model = string.IsNullOrWhiteSpace(_apiModel.Text) ? "claude-sonnet-5-5" : _apiModel.Text.Trim();
        Config.Api.ApiKeyProtected = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : Dpapi.Protect(_apiKey.Text.Trim());
        if (Config.Api.Enabled && Config.Api.ApiKeyProtected is null) { Status("Claude API analysis is on but no API key is set (Advanced, page 5)."); return false; }
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
