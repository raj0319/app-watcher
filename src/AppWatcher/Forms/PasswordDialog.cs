namespace AppWatcher.Forms;

internal sealed class PasswordDialog : Form
{
    private readonly TextBox _box = new() { UseSystemPasswordChar = true, Width = 260 };
    public string Password => _box.Text;

    public PasswordDialog(string why)
    {
        Text = "App Watcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(300, 110);
        var label = new Label { Text = why + ": enter the settings password", Left = 12, Top = 12, Width = 276 };
        _box.Left = 12; _box.Top = 36;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 132, Top = 70, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 213, Top = 70, Width = 75 };
        Controls.AddRange(new Control[] { label, _box, ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => _box.Focus();
    }
}
