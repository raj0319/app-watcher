namespace AppWatcher.Forms;

/// <summary>Asks for the settings password. A wrong password is shown inside this dialog; no message box.</summary>
internal sealed class PasswordDialog : Form
{
    private readonly TextBox _box = new() { UseSystemPasswordChar = true, Width = 276 };
    private readonly Label _error = new() { ForeColor = Color.Firebrick, Left = 12, Top = 64, Width = 276, Text = "" };
    private readonly Func<string, bool> _verify;

    public PasswordDialog(string why, Func<string, bool> verify)
    {
        _verify = verify;
        Text = "App Watcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(300, 124);
        var label = new Label { Text = why + ": enter the settings password", Left = 12, Top = 12, Width = 276 };
        _box.Left = 12; _box.Top = 36;
        var ok = new Button { Text = "OK", Left = 132, Top = 88, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 213, Top = 88, Width = 75 };
        ok.Click += (_, _) =>
        {
            if (_verify(_box.Text)) { DialogResult = DialogResult.OK; return; }
            _error.Text = "Wrong password.";
            _box.SelectAll(); _box.Focus();
        };
        Controls.AddRange(new Control[] { label, _box, _error, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => _box.Focus();
    }
}
