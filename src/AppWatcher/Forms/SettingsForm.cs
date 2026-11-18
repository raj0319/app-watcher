using System.Diagnostics;
using AppWatcher.Core;
using AppWatcher.Core.Analysis;
using AppWatcher.Core.Discovery;
using AppWatcher.Core.Sources;

namespace AppWatcher.Forms;

/// <summary>First-run wizard and later settings window. Plain controls, no designer file.</summary>
internal sealed class SettingsForm : Form
{
    public WatcherConfig Config { get; }
    private readonly bool _firstRun;

    // App tab
    private readonly ListView _processes = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    private readonly TextBox _exePath = new() { ReadOnly = true };
    private readonly TextBox _displayName = new();
    // Data tab
    private readonly CheckedListBox _sources = new() { CheckOnClick = true };
    private readonly List<DataSourceConfig> _sourceList = new();
    // Learning tab
    private readonly TextBox _learnAt = new() { Width = 60 };
    private readonly TextBox _triggers = new() { Width = 420 };
    private readonly NumericUpDown _maxDbMb = new() { Minimum = 1, Maximum = 100_000, Width = 90 };
    private readonly NumericUpDown _maxRows = new() { Minimum = 100, Maximum = 10_000_000, Width = 110, Increment = 1000 };
    private readonly NumericUpDown _afterMs = new() { Minimum = 200, Maximum = 30_000, Width = 90, Increment = 100 };
    // AI tab
    private readonly CheckBox _apiEnabled = new() { Text = "Send the memory files to the Claude API after each nightly learn (off by default)", AutoSize = true };
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true, Width = 420 };
    private readonly TextBox _apiModel = new() { Width = 200 };
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
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(600, 450);
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
        Controls.Add(tabs); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        Load += (_, _) => { RefreshProcesses(); LoadSources(); };
    }

    // ---------- tabs ----------

    private TabPage AppTab()
    {
        var p = new TabPage("1. App to watch");
        var intro = new Label { Text = "Pick the program App Watcher should learn from. Only this program is ever watched.", Left = 10, Top = 10, Width = 600, Height = 20 };
        _processes.SetBounds(10, 36, 610, 240);
        _processes.Columns.Add("Program", 140); _processes.Columns.Add("Window title", 300); _processes.Columns.Add("Path", 160);
        _processes.SelectedIndexChanged += (_, _) =>
        {
            if (_processes.SelectedItems.Count == 0) return;
            var it = _processes.SelectedItems[0];
            _exePath.Text = it.SubItems[2].Text;
            _displayName.Text = it.Text;
        };
        var refresh = new Button { Text = "Refresh list", Left = 10, Top = 282, Width = 100 };
        refresh.Click += (_, _) => RefreshProcesses();
        var browse = new Button { Text = "Browse for a program (.exe)...", Left = 116, Top = 282, Width = 200 };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the program to watch" };
            if (d.ShowDialog(this) == DialogResult.OK) { _exePath.Text = d.FileName; _displayName.Text = Path.GetFileNameWithoutExtension(d.FileName); _processes.SelectedItems.Clear(); }
        };
        var l1 = new Label { Text = "Program file:", Left = 10, Top = 318, Width = 100 };
        _exePath.SetBounds(116, 315, 504, 23);
        var l2 = new Label { Text = "Name for its data folder:", Left = 10, Top = 348, Width = 140 };
        _displayName.SetBounds(156, 345, 200, 23);
        var note = new Label { Text = "Changing the program later starts a new data folder; the old one is kept.", Left = 10, Top = 378, Width = 600, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _processes, refresh, browse, l1, _exePath, l2, _displayName, note });
        if (Config.App is not null) { _exePath.Text = Config.App.ExePath ?? ""; _displayName.Text = Config.App.DisplayName; }
        return p;
    }

    private TabPage DataTab()
    {
        var p = new TabPage("2. Where its data lives");
        var intro = new Label { Text = "App Watcher reads these places (never writes) to see what the app saves. Tick the ones that belong to the app.", Left = 10, Top = 10, Width = 610, Height = 34 };
        _sources.SetBounds(10, 48, 610, 260);
        var scan = new Button { Text = "Scan again", Left = 10, Top = 314, Width = 100 };
        scan.Click += (_, _) => ScanSources();
        var addFile = new Button { Text = "Add file...", Left = 116, Top = 314, Width = 90 };
        addFile.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Choose a data file", Filter = "Data files|*.db;*.sqlite;*.sqlite3;*.mdb;*.accdb;*.xml;*.json;*.csv;*.dbf;*.dat|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(SourceFactory.Create(new DataSourceConfig { Kind = "file", Path = d.FileName }) is SqliteSource ? new DataSourceConfig { Kind = "sqlite", Path = d.FileName } : new DataSourceConfig { Kind = d.FileName.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || d.FileName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase) ? "access" : "file", Path = d.FileName });
        };
        var addFolder = new Button { Text = "Add folder...", Left = 212, Top = 314, Width = 100 };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Choose the folder where the app keeps its data" };
            if (d.ShowDialog(this) == DialogResult.OK) AddSource(new DataSourceConfig { Kind = "folder", Path = d.SelectedPath });
        };
        var addSql = new Button { Text = "Add SQL Server connection...", Left = 318, Top = 314, Width = 190 };
        addSql.Click += (_, _) => AddSqlServer();
        var remove = new Button { Text = "Remove", Left = 514, Top = 314, Width = 106 };
        remove.Click += (_, _) => { var i = _sources.SelectedIndex; if (i >= 0) { _sources.Items.RemoveAt(i); _sourceList.RemoveAt(i); } };
        var note = new Label { Text = "Read only. Databases over the size cap are read partly (only tables that changed recently).", Left = 10, Top = 350, Width = 610, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, _sources, scan, addFile, addFolder, addSql, remove, note });
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
            if (hint is not null) list.Add(new Label { Text = hint, Left = 236 + c.Width + 8, Top = y + 3, Width = 360 - c.Width, ForeColor = SystemColors.GrayText });
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
        var note = new Label { Left = 10, Top = y + 10, Width = 610, Height = 120, Text =
            "What learning does, in plain words: it groups what users did into flows, matches values typed on a screen with the columns that changed, " +
            "finds links between tables, tests simple formulas on calculated columns, turns message boxes after a typed value into validation rules, " +
            "orders status values into a state diagram, and spots numbering sequences. It writes memory\\*.md with a confidence on every line. No AI, no network." };
        p.Controls.Add(note);
        return p;
    }

    private TabPage AiTab()
    {
        var p = new TabPage("4. AI analysis (optional)");
        _apiEnabled.Location = new Point(10, 14);
        _apiEnabled.Checked = Config.Api.Enabled;
        var l1 = new Label { Text = "Claude API key:", Left = 10, Top = 48, Width = 120 };
        _apiKey.SetBounds(136, 45, 420, 23);
        _apiKey.Text = Dpapi.Unprotect(Config.Api.ApiKeyProtected) ?? "";
        var l2 = new Label { Text = "Model:", Left = 10, Top = 80, Width = 120 };
        _apiModel.SetBounds(136, 77, 200, 23);
        _apiModel.Text = Config.Api.Model;
        var show = new Button { Text = "Show exactly what is sent", Left = 10, Top = 112, Width = 200 };
        show.Click += (_, _) => ShowPayload();
        var run = new Button { Text = "Run analysis now", Left = 216, Top = 112, Width = 150 };
        run.Click += async (_, _) => await RunApiNow();
        _apiStatus.Location = new Point(10, 148);
        _apiStatus.Text = Config.Api.LastRun is null ? "Never run." : $"Last run {Config.Api.LastRun:yyyy-MM-dd HH:mm}: {Config.Api.LastResult}";
        var note = new Label { Left = 10, Top = 180, Width = 610, Height = 150, Text =
            "Only the files in memory\\ (data-model.md, screens.md, rules.md, flows.md, architecture.md, changelog.md) are sent, " +
            "together with the analyst prompt. Never raw events, never values typed by users, never your data. " +
            "The answer is saved as memory\\architecture-reviewed.md. The key is stored protected (Windows DPAPI) and used for nothing else.\r\n\r\n" +
            "Without this, you can run the same analysis for free of network traffic from this PC: open the data folder in Claude Code and type /analyze." };
        p.Controls.AddRange(new Control[] { _apiEnabled, l1, _apiKey, l2, _apiModel, show, run, _apiStatus, note });
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
        var note = new Label { Text = "Stored as a salted hash, protected with Windows DPAPI. If it is lost, delete %ProgramData%\\AppWatcher\\config.json and set up again (data folders are kept).", Left = 10, Top = 116, Width = 610, Height = 40, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { intro, l1, _pw1, l2, _pw2, note });
        return p;
    }

    private TabPage AboutTab()
    {
        var p = new TabPage("About");
        var t = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text =
            "App Watcher 0.1\r\n\r\n" +
            "Watches one chosen program and learns the logic behind it: fields, tables, rules, flows.\r\n\r\n" +
            "Recorded: control names, button presses, values typed in the watched program (passwords masked), window titles, and read-only before/after snapshots of its data.\r\n" +
            "Never recorded: other programs, the clipboard, screenshots, keystrokes outside the watched program, anything over the network.\r\n\r\n" +
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
            foreach (var c in found.Take(60))
            {
                if (_sourceList.Any(s => string.Equals(s.Path, c.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var cfg = DataSourceScanner.ToConfig(c);
                cfg.Enabled = c.Kind is "sqlite" or "access";
                _sourceList.Add(cfg);
                _sources.Items.Add($"{cfg.Describe()}  ({c.Size / 1024} KB, {c.Why})", cfg.Enabled);
            }
            if (found.Count == 0) MessageBox.Show(this, "No data files found in the usual places. Add the file, folder or SQL Server connection by hand.", "App Watcher");
        }
        finally { UseWaitCursor = false; }
    }

    private void AddSource(DataSourceConfig cfg)
    {
        _sourceList.Add(cfg);
        _sources.Items.Add(cfg.Describe(), true);
    }

    private void AddSqlServer()
    {
        using var f = new Form { Text = "SQL Server connection (read only)", ClientSize = new Size(520, 150), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var l = new Label { Text = "Connection string (a read-only login is best):", Left = 10, Top = 10, Width = 500 };
        var box = new TextBox { Left = 10, Top = 34, Width = 500, Text = @"Server=.\SQLEXPRESS;Database=MyApp;Integrated Security=true;TrustServerCertificate=true" };
        var label = new Label { Text = "Name:", Left = 10, Top = 68, Width = 60 };
        var name = new TextBox { Left = 70, Top = 65, Width = 200, Text = "main database" };
        var test = new Button { Text = "Test", Left = 10, Top = 105, Width = 80 };
        var ok = new Button { Text = "Add", Left = 350, Top = 105, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 435, Top = 105, Width = 75, DialogResult = DialogResult.Cancel };
        test.Click += (_, _) => { var err = SqlServerSource.TestConnection(box.Text); MessageBox.Show(f, err ?? "Connected.", "App Watcher"); };
        f.Controls.AddRange(new Control[] { l, box, label, name, test, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(box.Text)) return;
        AddSource(new DataSourceConfig { Kind = "sqlserver", ConnectionStringProtected = Dpapi.Protect(box.Text), Label = name.Text });
    }

    private void ShowPayload()
    {
        var app = _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
        var memory = Paths.ForApp(app).Memory;
        var text = AnalystPrompt.BuildUserMessage(memory);
        using var f = new Form { Text = "Exactly what is sent to the Claude API", ClientSize = new Size(760, 520), StartPosition = FormStartPosition.CenterParent };
        var head = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8), Text = $"POST {Config.Api.Endpoint}  model={_apiModel.Text}. One user message with this text (plus a one-line system prompt naming the task). Nothing else leaves this PC." };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = text.Replace("\n", "\r\n") };
        f.Controls.Add(box); f.Controls.Add(head);
        f.ShowDialog(this);
    }

    private async Task RunApiNow()
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text)) { MessageBox.Show(this, "Enter an API key first.", "App Watcher"); return; }
        var app = _displayName.Text.Length > 0 ? _displayName.Text : Config.App?.DisplayName ?? "";
        var memory = Paths.ForApp(app).Memory;
        if (!Directory.Exists(memory) || Directory.GetFiles(memory, "*.md").Length == 0) { MessageBox.Show(this, "No memory files yet. Use 'Learn now' from the tray first.", "App Watcher"); return; }
        if (MessageBox.Show(this, "Send the memory files to the Claude API now? Use 'Show exactly what is sent' to review them first.", "App Watcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _apiStatus.Text = "Running...";
        try
        {
            var settings = new ApiSettings { Model = _apiModel.Text, Endpoint = Config.Api.Endpoint, MaxTokens = Config.Api.MaxTokens };
            var status = await new ClaudeApiClient(settings, _apiKey.Text.Trim()).AnalyzeAndSaveAsync(memory);
            Config.Api.LastRun = DateTime.Now; Config.Api.LastResult = status;
            _apiStatus.Text = $"Done: {status}. See memory\\architecture-reviewed.md";
        }
        catch (Exception ex)
        {
            Config.Api.LastRun = DateTime.Now; Config.Api.LastResult = "failed: " + ex.Message;
            _apiStatus.Text = "Failed: " + ex.Message;
        }
    }

    private bool Apply()
    {
        // app
        if (string.IsNullOrWhiteSpace(_exePath.Text) && _processes.SelectedItems.Count == 0 && Config.App is null)
        { MessageBox.Show(this, "Pick the program to watch on the first tab.", "App Watcher"); return false; }
        if (_processes.SelectedItems.Count > 0 || !string.IsNullOrWhiteSpace(_exePath.Text))
        {
            var processName = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Text : Path.GetFileNameWithoutExtension(_exePath.Text);
            var display = string.IsNullOrWhiteSpace(_displayName.Text) ? processName : _displayName.Text.Trim();
            if (Config.App is null || Config.App.ProcessName != processName || Config.App.DisplayName != display)
                Config.App = new WatchedApp { ProcessName = processName, ExePath = _exePath.Text, DisplayName = display, ChosenAt = DateTime.Now };
            else Config.App.ExePath = _exePath.Text;
        }
        // password
        if (_pw1.Text.Length > 0 || _firstRun)
        {
            if (_pw1.Text.Length < 4) { MessageBox.Show(this, "The password needs at least 4 characters.", "App Watcher"); return false; }
            if (_pw1.Text != _pw2.Text) { MessageBox.Show(this, "The two passwords differ.", "App Watcher"); return false; }
            Config.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(_pw1.Text));
        }
        // data
        for (var i = 0; i < _sourceList.Count; i++) _sourceList[i].Enabled = _sources.GetItemChecked(i);
        Config.DataSources = _sourceList.ToList();
        // learning
        if (!TimeOnly.TryParseExact(_learnAt.Text.Trim(), "HH:mm", out _)) { MessageBox.Show(this, "Learn time must look like 02:00.", "App Watcher"); return false; }
        Config.LearnAt = _learnAt.Text.Trim();
        Config.EffectTriggers = _triggers.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Config.Snapshot.MaxDbBytes = (long)_maxDbMb.Value * 1024 * 1024;
        Config.Snapshot.MaxRowsPerTable = (int)_maxRows.Value;
        Config.Snapshot.AfterDelayMs = (int)_afterMs.Value;
        // api
        Config.Api.Enabled = _apiEnabled.Checked;
        Config.Api.Model = string.IsNullOrWhiteSpace(_apiModel.Text) ? "claude-sonnet-5-5" : _apiModel.Text.Trim();
        Config.Api.ApiKeyProtected = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : Dpapi.Protect(_apiKey.Text.Trim());
        if (Config.Api.Enabled && Config.Api.ApiKeyProtected is null) { MessageBox.Show(this, "AI analysis is on but no API key is set.", "App Watcher"); return false; }
        return true;
    }

    private static WatcherConfig Clone(WatcherConfig c) =>
        System.Text.Json.JsonSerializer.Deserialize<WatcherConfig>(System.Text.Json.JsonSerializer.Serialize(c, WatcherConfig.JsonOptions), WatcherConfig.JsonOptions)!;
}
