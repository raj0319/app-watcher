using AppWatcher.Core;
using AppWatcher.Core.Setup;

namespace AppWatcher.Forms;

/// <summary>
/// The first run, for an office user rather than IT: three steps, one decision each, plain words, no tabs.
/// 1. Which program should I watch?  2. Anything to keep private?  3. Choose a password, then Start watching.
/// Everything else is decided by the program with the recommended (fullest safe, local) values: data sources found
/// on this PC are all ticked, file-access detection runs by itself after the start, and the advanced options stay
/// off until someone opens Settings, Advanced. No message boxes: problems show in the line above the buttons.
/// </summary>
internal sealed class SetupWizard : Form
{
    public WatcherConfig Config { get; private set; } = new();

    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 44, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f, FontStyle.Bold), Padding = new Padding(16, 10, 16, 0) };
    private readonly Label _sub = new() { Dock = DockStyle.Top, Height = 28, Padding = new Padding(18, 2, 16, 0), ForeColor = SystemColors.GrayText };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 6, 16, 6) };
    private readonly Label _error = new() { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(18, 4, 16, 0), ForeColor = Color.Firebrick };
    private readonly Button _back = new() { Text = "Back", Width = 100, Height = 32 };
    private readonly Button _next = new() { Text = "Next", Width = 100, Height = 32 };
    private readonly Label _stepDots = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 8, 0, 0) };
    private int _step;

    // step 1
    private readonly ProgramPicker _picker = new() { Dock = DockStyle.Fill };
    // step 2
    private readonly CheckBox _mask = new() { Text = "Hide names and numbers that appear in window titles", Checked = true, AutoSize = true };
    private readonly CheckBox _shots = new() { Text = "Keep pictures of the program's screens, never the desktop", Checked = true, AutoSize = true };
    private readonly CheckBox _ocr = new() { Text = "Read the text on those pictures so I can learn from programs that draw their own screens", Checked = true, AutoSize = true };
    private readonly TextBox _never = new() { Width = 560 };
    // step 3
    private readonly TextBox _pw1 = new() { UseSystemPasswordChar = true, Width = 260 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, Width = 260 };
    private readonly Button _start = new() { Text = "Start watching", Width = 220, Height = 46, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold), BackColor = Color.FromArgb(40, 110, 200), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

    private readonly Panel[] _pages;

    public SetupWizard()
    {
        Text = "App Watcher";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 600);
        MinimumSize = new Size(640, 560);
        Font = SystemFonts.MessageBoxFont!;

        _pages = new[] { Page1(), Page2(), Page3() };
        foreach (var p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; _body.Controls.Add(p); }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8) };
        bar.Controls.Add(_next); bar.Controls.Add(_back); bar.Controls.Add(_stepDots);
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => { if (CanLeave(_step)) Go(_step + 1); };
        _start.Click += (_, _) => Finish();
        _picker.Changed += () => { _error.Text = ""; UpdateButtons(); };
        _picker.PlanReady += UpdateButtons;

        Controls.Add(_body); Controls.Add(_error); Controls.Add(bar); Controls.Add(_sub); Controls.Add(_title);
        AcceptButton = _next;
        Go(0);
    }

    private Panel Page1() => new() { Controls = { _picker } };

    /// <summary>For --render-ui (pictures of the wizard for the docs): show a step and, on step 1, a chosen program.</summary>
    internal void ShowStep(int step, SetupPlan? demo = null)
    {
        if (demo is not null)
        {
            _picker.ShowDemoItems(new[] { ("Notepad", "Untitled - Notepad"), (demo.FriendlyName, "Main Menu"), ("Sage 50 Accounts", "Sage 50 Accounts - Customers"), ("Excel", "Book1 - Excel") });
            _picker.ShowDemoPlan(demo);
        }
        Go(step);
    }

    private Panel Page2()
    {
        var p = new Panel();
        _mask.Location = new Point(4, 10);
        _shots.Location = new Point(4, 44);
        _ocr.Location = new Point(4, 78);
        var hint = new Label { Text = "Pictures are of this one program's own window. Nothing is ever sent anywhere; everything stays on this PC.", Left = 24, Top = 106, Width = 620, Height = 44, ForeColor = SystemColors.GrayText };
        var l = new Label { Text = "Windows I must never record (for example Payroll):", Left = 4, Top = 160, Width = 600 };
        _never.Location = new Point(4, 182);
        var l2 = new Label { Text = "Type the window names, separated by commas. Leave it empty if there is nothing to hide.", Left = 4, Top = 208, Width = 640, ForeColor = SystemColors.GrayText };
        p.Controls.AddRange(new Control[] { _mask, _shots, _ocr, hint, l, _never, l2 });
        return p;
    }

    private Panel Page3()
    {
        var p = new Panel();
        var why = new Label { Text = "A password keeps anyone from stopping the watcher or changing what it records by accident. You need it to open Settings or to quit.", Left = 4, Top = 8, Width = 640, Height = 40 };
        var l1 = new Label { Text = "Password:", Left = 4, Top = 62, Width = 140 };
        _pw1.Location = new Point(150, 59);
        var l2 = new Label { Text = "Type it again:", Left = 4, Top = 96, Width = 140 };
        _pw2.Location = new Point(150, 93);
        var after = new Label { Text = "After you press Start, I sit next to the clock as a small eye. Just use the program as usual. I only show a short notice near the clock now and then, and I never change anything in the program.", Left = 4, Top = 140, Width = 640, Height = 60, ForeColor = SystemColors.GrayText };
        _start.Location = new Point(150, 212);
        _start.FlatAppearance.BorderSize = 0;
        p.Controls.AddRange(new Control[] { why, l1, _pw1, l2, _pw2, after, _start });
        return p;
    }

    private void Go(int step)
    {
        step = Math.Clamp(step, 0, _pages.Length - 1);
        _step = step;
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == step;
        (_title.Text, _sub.Text) = step switch
        {
            0 => ("Which program should I watch?", "I will only ever watch this one program, and only while you use it."),
            1 => ("Anything to keep private?", "These are already set the way most people want them."),
            _ => ("Choose a password to protect these settings", "Last step."),
        };
        _stepDots.Text = $"Step {step + 1} of 3   ";
        _error.Text = "";
        UpdateButtons();
        if (step == 2) { AcceptButton = _start; _pw1.Focus(); } else AcceptButton = _next;
    }

    private void UpdateButtons()
    {
        _back.Enabled = _step > 0;
        _next.Visible = _step < 2;
        _next.Enabled = _step != 0 || _picker.HasPick;
    }

    private bool CanLeave(int step)
    {
        if (step == 0 && !_picker.HasPick) { _error.Text = "Pick a program from the list, or press \"Find it on this PC...\"."; return false; }
        return true;
    }

    private void Finish()
    {
        if (!_picker.HasPick) { Go(0); _error.Text = "Pick a program first."; return; }
        if (_pw1.Text.Length < 4) { _error.Text = "The password needs at least 4 characters."; return; }
        if (_pw1.Text != _pw2.Text) { _error.Text = "The two passwords are not the same. Type them again."; return; }
        UseWaitCursor = true;
        try
        {
            var plan = _picker.PlanNow();
            var cfg = new WatcherConfig();
            SetupDefaults.Apply(cfg, plan);
            cfg.Privacy.MaskTitles = _mask.Checked;
            cfg.Privacy.Screenshots = _shots.Checked;
            cfg.Privacy.ScreenshotMode = _shots.Checked ? "actions" : "off";
            cfg.Privacy.ReadTextFromPictures = _ocr.Checked;
            foreach (var w in _never.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!cfg.Privacy.ExcludeWindows.Contains(w, StringComparer.OrdinalIgnoreCase)) cfg.Privacy.ExcludeWindows.Add(w.Contains('*') ? w : "*" + w + "*");
            cfg.PasswordProtected = Dpapi.Protect(PasswordHasher.CreateRecord(_pw1.Text));
            Config = cfg;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            Log.Error("setup failed", ex);
            _error.Text = "Something went wrong while saving: " + ex.Message;
        }
        finally { UseWaitCursor = false; }
    }
}
