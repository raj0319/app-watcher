using System.Diagnostics;
using AppWatcher.Core.Setup;

namespace AppWatcher.Forms;

/// <summary>
/// "Which program should I watch?": the programs open right now with their icons and the names people know them by,
/// a "Find it on this PC..." button, and a green box that says in plain words what I found and what I will learn.
/// The technical facts sit behind a "Details" link. Used by the first-run wizard and by Settings (Change program).
/// </summary>
internal sealed class ProgramPicker : UserControl
{
    private sealed record Pick(string Path, string Process, string Friendly, int? Pid);

    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 4) };
    private readonly ImageList _icons = new() { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
    private readonly Button _find = new() { Text = "Find it on this PC...", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    private readonly Button _refresh = new() { Text = "Refresh the list", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    private readonly Label _box = new()
    {
        Dock = DockStyle.Bottom, Height = 124, Padding = new Padding(10), BorderStyle = BorderStyle.FixedSingle, Visible = false,
        BackColor = Color.FromArgb(226, 244, 226), ForeColor = Color.FromArgb(20, 70, 20), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f),
    };
    private readonly LinkLabel _detailsLink = new() { Text = "Details", Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(0, 4, 0, 0), Visible = false };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 96, Visible = false, Font = new Font("Consolas", 8.5f) };
    private int _version;

    public string? ExePath { get; private set; }
    public string ProcessName { get; private set; } = "";
    public string FriendlyName { get; private set; } = "";
    public SetupPlan? Plan { get; private set; }
    public bool Busy { get; private set; }
    public bool HasPick => ProcessName.Length > 0;
    /// <summary>The program that should appear selected when the list loads (Settings: the program watched now).</summary>
    public string? PreselectProcess { get; set; }

    public event Action? Changed;
    public event Action? PlanReady;

    public ProgramPicker()
    {
        _list.Columns.Add("Program", 250);
        _list.Columns.Add("What its window shows now", 330);
        _list.SmallImageList = _icons;
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is Pick p) Select(p); };
        _find.Click += (_, _) => FindOnPc();
        _refresh.Click += (_, _) => Reload();
        _detailsLink.LinkClicked += (_, _) => ToggleDetails();

        var intro = new Label { Text = "Programs open right now. Pick the one you use for work, or find it on this PC if it is not open.", Dock = DockStyle.Top, Height = 26 };
        _buttons.Controls.Add(_find); _buttons.Controls.Add(_refresh);
        // docking order: the list fills what is left; from the bottom edge up: details, details link, green box, buttons
        Controls.Add(_list);
        Controls.Add(_buttons);
        Controls.Add(_box);
        Controls.Add(_detailsLink);
        Controls.Add(_details);
        Controls.Add(intro);
        Load += (_, _) => Reload();
    }

    public void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();
        var self = Environment.ProcessId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in Process.GetProcesses().OrderBy(x => x.ProcessName))
        {
            try
            {
                if (pr.Id == self || pr.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(pr.MainWindowTitle)) continue;
                var path = "";
                try { path = pr.MainModule?.FileName ?? ""; } catch { }
                var key = path.Length > 0 ? path : pr.ProcessName;
                if (!seen.Add(key)) continue;
                var friendly = SetupDefaults.FriendlyName(path, pr.MainWindowTitle, pr.ProcessName);
                var item = new ListViewItem(new[] { friendly, pr.MainWindowTitle }) { Tag = new Pick(path, pr.ProcessName, friendly, pr.Id) };
                if (path.Length > 0)
                {
                    try
                    {
                        using var ic = Icon.ExtractAssociatedIcon(path);
                        if (ic is not null) { _icons.Images.Add(key, ic.ToBitmap()); item.ImageKey = key; }
                    }
                    catch { }
                }
                _list.Items.Add(item);
                if (PreselectProcess is not null && string.Equals(pr.ProcessName, PreselectProcess, StringComparison.OrdinalIgnoreCase) && ProcessName.Length == 0) item.Selected = true;
            }
            catch { }
            finally { pr.Dispose(); }
        }
        _list.EndUpdate();
    }

    private void FindOnPc()
    {
        // opened by the user on purpose: the one file dialog of the first run
        using var d = new OpenFileDialog { Title = "Find the program I should watch", Filter = "Programs|*.exe", CheckFileExists = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _list.SelectedItems.Clear();
        var proc = Path.GetFileNameWithoutExtension(d.FileName);
        int? pid = null;
        try { var ps = Process.GetProcessesByName(proc); pid = ps.FirstOrDefault()?.Id; foreach (var p in ps) p.Dispose(); } catch { }
        Select(new Pick(d.FileName, proc, SetupDefaults.FriendlyName(d.FileName, null, proc), pid));
    }

    /// <summary>For --render-ui: demo rows instead of this PC's real programs and window titles.</summary>
    internal void ShowDemoItems(IEnumerable<(string name, string window)> items)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (name, window) in items) _list.Items.Add(new ListViewItem(new[] { name, window }));
        if (_list.Items.Count > 0) _list.Items[0].Selected = false;
        _list.EndUpdate();
    }

    /// <summary>For --render-ui: show a fixed plan (no look-up on this PC), with its demo row selected.</summary>
    internal void ShowDemoPlan(SetupPlan plan)
    {
        foreach (ListViewItem it in _list.Items) if (it.Text == plan.FriendlyName) { it.Selected = true; it.Focused = true; }
        ExePath = plan.ExePath; ProcessName = plan.ProcessName; FriendlyName = plan.FriendlyName;
        _box.Visible = true;
        ShowPlan(++_version, plan);
    }

    /// <summary>Shows a program that is already chosen (Settings) without asking the list.</summary>
    public void Show(string? exePath, string processName, string friendly) => Select(new Pick(exePath ?? "", processName, friendly, null));

    private void Select(Pick p)
    {
        ExePath = p.Path.Length > 0 ? p.Path : null;
        ProcessName = p.Process;
        FriendlyName = p.Friendly;
        Plan = null;
        Busy = true;
        _box.Visible = true;
        _box.Text = $"{p.Friendly}. Looking at this program...";
        _detailsLink.Visible = false;
        Changed?.Invoke();
        var v = ++_version;
        // the look-up runs in the background; its result is posted back to this control (no dependency on the thread's synchronization context)
        Task.Run(() =>
        {
            SetupPlan plan;
            try { plan = SetupDefaults.Prepare(p.Path.Length > 0 ? p.Path : null, p.Process, p.Friendly, p.Pid); }
            catch (Exception ex) { Core.Log.Warn("program look-up failed: " + ex.Message); plan = new SetupPlan { ExePath = p.Path, ProcessName = p.Process, FriendlyName = p.Friendly }; }
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => ShowPlan(v, plan)); }
            catch (Exception ex) { Core.Log.Warn("program look-up result not shown: " + ex.Message); }
        });
    }

    private void ShowPlan(int version, SetupPlan plan)
    {
        if (version != _version || IsDisposed) return;
        Busy = false;
        Plan = plan;
        try
        {
            _box.Text = SetupDefaults.WhatIWillLearn(plan);
            _details.Text = SetupDefaults.Details(plan);
            _detailsLink.Visible = true;
        }
        catch (Exception ex) { _box.Text = $"{plan.FriendlyName}. I will record its screens, buttons, fields and the pictures of each screen."; Core.Log.Warn("green box text failed: " + ex.Message); }
        PlanReady?.Invoke();
    }

    private void ToggleDetails()
    {
        _details.Visible = !_details.Visible;
        _detailsLink.Text = _details.Visible ? "Hide details" : "Details";
    }

    /// <summary>The plan for the chosen program; worked out now when the background look is not finished yet.</summary>
    public SetupPlan PlanNow() => Plan ?? (Plan = SetupDefaults.Prepare(ExePath, ProcessName, FriendlyName));
}
