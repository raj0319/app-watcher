using System.Diagnostics;

namespace SampleApp;

/// <summary>
/// A tiny business-like app used to test the collector against real windows: a main window with a menu, a
/// tabbed workspace (Customers, Orders) with labelled fields, a Save button that writes a SQLite-free JSON
/// "database", a New Customer dialog, a validation message window, and a login window first.
/// It is driven by handle from scripts/live-test.ps1 and killed at the end. It never shows a Windows
/// message box: its message windows are plain forms that close by themselves after two seconds.
/// Run with --no-login to skip the login window, --corner to open at the bottom-right corner.
/// </summary>
internal static class Program
{
    public static readonly string DataFile = Path.Combine(AppContext.BaseDirectory, "sample-data.json");

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var corner = args.Contains("--corner");
        if (!args.Contains("--no-login"))
        {
            using var login = new LoginForm(corner);
            if (login.ShowDialog() != DialogResult.OK) return;
        }
        Application.Run(new MainForm(corner));
    }
}

internal sealed class LoginForm : Form
{
    public LoginForm(bool corner)
    {
        Text = "Sample ERP - Login";
        ClientSize = new Size(300, 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = corner ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (corner) Location = new Point(Screen.PrimaryScreen!.WorkingArea.Right - 320, Screen.PrimaryScreen.WorkingArea.Bottom - 160);
        var l1 = new Label { Text = "User", Left = 10, Top = 14, Width = 80 };
        var user = new TextBox { Name = "txtUser", Left = 100, Top = 10, Width = 180, AccessibleName = "" };
        var l2 = new Label { Text = "Password", Left = 10, Top = 44, Width = 80 };
        var pw = new TextBox { Name = "txtPassword", Left = 100, Top = 40, Width = 180, UseSystemPasswordChar = true };
        var ok = new Button { Text = "Log in", Name = "btnLogin", Left = 100, Top = 80, Width = 90, DialogResult = DialogResult.OK };
        Controls.AddRange(new Control[] { l1, user, l2, pw, ok });
        AcceptButton = ok;
    }
}

internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Name = "tabWorkspace" };
    private readonly TextBox _custCode = new() { Name = "txtCustomerCode", Width = 160 };
    private readonly TextBox _custName = new() { Name = "txtCustomerName", Width = 220 };
    private readonly ComboBox _custType = new() { Name = "cboCustomerType", Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _orderNo = new() { Name = "txtOrderNo", Width = 120 };
    private readonly TextBox _orderAmount = new() { Name = "neAmount", Width = 100 };
    private readonly CheckBox _rush = new() { Name = "chkRush", Text = "Rush order", AutoSize = true };
    // Quote tab: the program fills the customer name from the code and calculates the line total
    private readonly TextBox _quoteCustomer = new() { Name = "txtQuoteCustomer", Width = 140 };
    private readonly TextBox _quoteCustomerName = new() { Name = "txtQuoteCustomerName", Width = 220, ReadOnly = true, TabStop = false };
    private readonly TextBox _qty = new() { Name = "txtQty", Width = 80 };
    private readonly TextBox _price = new() { Name = "txtPrice", Width = 80 };
    private readonly TextBox _discount = new() { Name = "txtDiscount", Width = 80 };
    private readonly TextBox _lineTotal = new() { Name = "txtLineTotal", Width = 100, ReadOnly = true, TabStop = false };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new("Ready");
    private int _refreshes;

    public MainForm(bool corner)
    {
        Text = "Sample ERP";
        ClientSize = new Size(560, 380);
        StartPosition = corner ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (corner) Location = new Point(Screen.PrimaryScreen!.WorkingArea.Right - 580, Screen.PrimaryScreen.WorkingArea.Bottom - 420);
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("File");
        var newCustomer = new ToolStripMenuItem("New Customer...") { Name = "mnuNewCustomer", ShortcutKeys = Keys.Control | Keys.N };
        newCustomer.Click += (_, _) => NewCustomer();
        var exit = new ToolStripMenuItem("Exit") { Name = "mnuExit" };
        exit.Click += (_, _) => Close();
        file.DropDownItems.AddRange(new ToolStripItem[] { newCustomer, new ToolStripSeparator(), exit });
        var view = new ToolStripMenuItem("View");
        var viewCustomers = new ToolStripMenuItem("Customer Master") { Name = "mnuViewCustomers" };
        viewCustomers.Click += (_, _) => _tabs.SelectedIndex = 0;
        var viewOrders = new ToolStripMenuItem("Orders") { Name = "mnuViewOrders" };
        viewOrders.Click += (_, _) => _tabs.SelectedIndex = 1;
        var viewQuote = new ToolStripMenuItem("Quote") { Name = "mnuViewQuote" };
        viewQuote.Click += (_, _) => _tabs.SelectedIndex = 2;
        var viewReports = new ToolStripMenuItem("Reports") { Name = "mnuViewReports" }; // never chosen by the live test: shows up as "not seen yet"
        viewReports.Click += (_, _) => Message("Reports", "Reports are not part of this sample.");
        // an MDI workspace like a tabbed-MDI ERP: documents are child windows of the MDI client, the one in front is the screen
        var viewWorkspace = new ToolStripMenuItem("Workspace") { Name = "mnuViewWorkspace" };
        viewWorkspace.Click += (_, _) => new WorkspaceForm(this).Show(this);
        view.DropDownItems.AddRange(new ToolStripItem[] { viewCustomers, viewOrders, viewQuote, viewReports, viewWorkspace });
        var help = new ToolStripMenuItem("Help");
        var about = new ToolStripMenuItem("About") { Name = "mnuAbout" };
        about.Click += (_, _) => Message("About", "Sample ERP 1.0 - test application for App Watcher.");
        help.DropDownItems.Add(about);
        menu.Items.AddRange(new ToolStripItem[] { file, view, help });
        MainMenuStrip = menu;

        var customers = new TabPage("Customer Master") { Name = "tabCustomers" };
        var y = 16;
        customers.Controls.Add(new Label { Text = "Customer code:", Left = 16, Top = y + 3, Width = 110 });
        _custCode.Location = new Point(132, y); customers.Controls.Add(_custCode); y += 32;
        customers.Controls.Add(new Label { Text = "Name:", Left = 16, Top = y + 3, Width = 110 });
        _custName.Location = new Point(132, y); customers.Controls.Add(_custName); y += 32;
        customers.Controls.Add(new Label { Text = "Type:", Left = 16, Top = y + 3, Width = 110 });
        _custType.Items.AddRange(new object[] { "Retail", "Wholesale", "Internal" }); _custType.SelectedIndex = 0;
        _custType.Location = new Point(132, y); customers.Controls.Add(_custType); y += 40;
        var saveCustomer = new Button { Text = "Save", Name = "btnSaveCustomer", Left = 132, Top = y, Width = 90 };
        saveCustomer.Click += (_, _) => SaveCustomer();
        var clear = new Button { Text = "Clear", Name = "btnClear", Left = 230, Top = y, Width = 90 };
        clear.Click += (_, _) => { _custCode.Text = ""; _custName.Text = ""; };
        // a toolbar-style button that opens the same blocking window as File > New Customer (the live test clicks it with a posted message)
        var newButton = new Button { Text = "New...", Name = "btnNewCustomer", Left = 328, Top = y, Width = 90 };
        newButton.Click += (_, _) => NewCustomer();
        customers.Controls.AddRange(new Control[] { saveCustomer, clear, newButton });

        var orders = new TabPage("Orders") { Name = "tabOrders" };
        y = 16;
        orders.Controls.Add(new Label { Text = "Order number:", Left = 16, Top = y + 3, Width = 110 });
        _orderNo.Location = new Point(132, y); orders.Controls.Add(_orderNo); y += 32;
        orders.Controls.Add(new Label { Text = "Amount:", Left = 16, Top = y + 3, Width = 110 });
        _orderAmount.Location = new Point(132, y); orders.Controls.Add(_orderAmount); y += 32;
        _rush.Location = new Point(132, y); orders.Controls.Add(_rush); y += 36;
        var post = new Button { Text = "Post", Name = "btnPost", Left = 132, Top = y, Width = 90 };
        post.Click += (_, _) => PostOrder();
        orders.Controls.Add(post);
        var grid = new DataGridView { Name = "grdLines", Left = 300, Top = 16, Width = 240, Height = 200, AllowUserToAddRows = false, ReadOnly = true };
        grid.Columns.Add("Item", "Item"); grid.Columns.Add("Qty", "Qty"); grid.Columns.Add("Price", "Price");
        for (var i = 1; i <= 12; i++) grid.Rows.Add($"ITEM-{i:000}", i, i * 2.5);
        orders.Controls.Add(grid);

        var tips = new ToolTip();
        tips.SetToolTip(post, "Post the order to the ledger");

        var quote = new TabPage("Quote") { Name = "tabQuote" };
        y = 16;
        foreach (var (label, box) in new (string, TextBox)[] { ("Customer code:", _quoteCustomer), ("Customer name:", _quoteCustomerName), ("Quantity:", _qty), ("Unit price:", _price), ("Discount %:", _discount), ("Line total:", _lineTotal) })
        {
            quote.Controls.Add(new Label { Text = label, Left = 16, Top = y + 3, Width = 110 });
            box.Location = new Point(132, y); quote.Controls.Add(box); y += 32;
        }
        _quoteCustomer.TextChanged += (_, _) => _quoteCustomerName.Text = Db.CustomerName(_quoteCustomer.Text.Trim()) ?? "(unknown customer)";
        _qty.TextChanged += (_, _) => Recalculate();
        _price.TextChanged += (_, _) => Recalculate();
        _discount.TextChanged += (_, _) => Recalculate();
        var printQuote = new Button { Text = "Print quote", Name = "btnPrintQuote", Left = 132, Top = y + 4, Width = 110 }; // never pressed by the live test
        printQuote.Click += (_, _) => Message("Print", "Printing is not part of this sample.");
        quote.Controls.Add(printQuote);

        // a toolbar-style button that raises no Invoke event and only changes the window (its title): the press can only be inferred
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 30, Name = "pnlToolbar" };
        var refresh = new Button { Text = "Refresh", Name = "btnRefresh", Left = 4, Top = 2, Width = 80, Height = 26 };
        refresh.Click += (_, _) => { _refreshes++; Text = $"Sample ERP - refreshed {_refreshes}"; _statusText.Text = $"Refreshed {_refreshes}"; };
        tips.SetToolTip(refresh, "Reload the lists from the database");
        toolbar.Controls.Add(refresh);

        _tabs.TabPages.Add(customers); _tabs.TabPages.Add(orders); _tabs.TabPages.Add(quote);
        _status.Items.Add(_statusText);
        Controls.Add(_tabs); Controls.Add(_status); Controls.Add(toolbar); Controls.Add(menu);
        toolbar.Top = menu.Height;
        _tabs.Top = menu.Height + toolbar.Height;
    }

    /// <summary>Line total = quantity × unit price × (1 − discount / 100), shown with two decimals once quantity and price are numbers.</summary>
    private void Recalculate()
    {
        if (!decimal.TryParse(_qty.Text, out var q) || !decimal.TryParse(_price.Text, out var p)) return;
        decimal.TryParse(_discount.Text, out var d);
        _lineTotal.Text = (q * p * (1 - d / 100)).ToString("0.00");
    }

    private void NewCustomer()
    {
        using var d = new NewCustomerForm(this);
        if (d.ShowDialog(this) == DialogResult.OK) { _custCode.Text = d.Code; _custName.Text = d.CustomerName; }
    }

    private void SaveCustomer()
    {
        if (_custCode.Text.Trim().Length < 3) { Message("Validation", "Customer code must have at least 3 characters."); return; }
        Db.Upsert("customers", _custCode.Text.Trim(), new Dictionary<string, string?> { ["code"] = _custCode.Text.Trim(), ["name"] = _custName.Text.Trim(), ["type"] = _custType.Text });
        _statusText.Text = $"Customer {_custCode.Text} saved";
    }

    private void PostOrder()
    {
        if (!decimal.TryParse(_orderAmount.Text, out var amount)) { Message("Validation", "Amount must be a number."); return; }
        var no = _orderNo.Text.Trim().Length > 0 ? _orderNo.Text.Trim() : Db.NextOrderNo();
        _orderNo.Text = no;
        Db.Upsert("orders", no, new Dictionary<string, string?> { ["order_no"] = no, ["customer"] = _custCode.Text.Trim(), ["amount"] = amount.ToString("0.00"), ["total"] = (amount * 1.2m).ToString("0.00"), ["rush"] = _rush.Checked ? "1" : "0", ["status"] = "open" });
        _statusText.Text = $"Order {no} posted";
    }

    /// <summary>A message window that is a plain form (Text + OK) and closes itself after two seconds, so no modal ever lingers.</summary>
    internal void Message(string title, string text)
    {
        var f = new Form { Text = title, ClientSize = new Size(340, 100), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false };
        f.Location = new Point(Location.X + 100, Location.Y + 100);
        var body = new Label { Text = text, Name = "lblBody", Left = 12, Top = 12, Width = 316, Height = 40 };
        var ok = new Button { Text = "OK", Name = "btnOK", Left = 250, Top = 62, Width = 78, DialogResult = DialogResult.OK };
        f.Controls.AddRange(new Control[] { body, ok });
        f.AcceptButton = ok;
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => { timer.Stop(); if (!f.IsDisposed) f.Close(); };
        timer.Start();
        f.Show(this);
    }
}

/// <summary>
/// An MDI workspace (IsMdiContainer): two documents, "Price Change" and "Sales Order Entry", opened as MDI child windows
/// (children of the MDICLIENT pane, like Infragistics UltraTabbedMdi documents). Each has an inner tab control, a few
/// fields and an Exit button that closes the document; the second one opened is in front.
/// </summary>
internal sealed class WorkspaceForm : Form
{
    public WorkspaceForm(Form owner)
    {
        Text = "Sample ERP - Workspace";
        IsMdiContainer = true;
        ClientSize = new Size(720, 460);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Math.Max(0, owner.Location.X - 200), Math.Max(0, owner.Location.Y - 140));
        var close = new Button { Text = "Close workspace", Name = "btnCloseWorkspace", Dock = DockStyle.Top, Height = 28 };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        Shown += (_, _) =>
        {
            new DocumentForm(this, "Price Change", 12.5m).Show();
            new DocumentForm(this, "Sales Order Entry", 45.34m).Show();
        };
    }
}

/// <summary>One MDI document: an inner tab control (Main | Notes), Product and Qty fields, a Total the program calculates (Qty × unit price), and Exit.</summary>
internal sealed class DocumentForm : Form
{
    private readonly TextBox _qty = new() { Name = "txtDocQty", Width = 100 };
    private readonly TextBox _total = new() { Name = "txtDocTotal", Width = 120, ReadOnly = true };

    public DocumentForm(Form mdiParent, string title, decimal unitPrice)
    {
        MdiParent = mdiParent;
        Text = title;
        Name = title.Replace(" ", "") + "View";
        ClientSize = new Size(520, 320);
        // fixed tab sizes, so the live test can click the second page at a known spot (WinForms hides tab items from a second UIA client)
        var tabs = new TabControl { Name = "tabsDoc", Left = 8, Top = 8, Width = 500, Height = 260, SizeMode = TabSizeMode.Fixed, ItemSize = new Size(80, 22) };
        var main = new TabPage("Main") { Name = "tabDocMain" };
        main.Controls.Add(new Label { Text = "Product:", Left = 12, Top = 16, Width = 80 });
        main.Controls.Add(new TextBox { Name = "txtDocProduct", Left = 100, Top = 12, Width = 160 });
        main.Controls.Add(new Label { Text = "Qty:", Left = 12, Top = 48, Width = 80 });
        _qty.Location = new Point(100, 44); main.Controls.Add(_qty);
        main.Controls.Add(new Label { Text = "Total:", Left = 12, Top = 80, Width = 80 });
        _total.Location = new Point(100, 76); main.Controls.Add(_total);
        _qty.TextChanged += (_, _) => _total.Text = decimal.TryParse(_qty.Text, out var q) ? (q * unitPrice).ToString("0.00") : "";
        var notes = new TabPage("Notes") { Name = "tabDocNotes" };
        notes.Controls.Add(new TextBox { Name = "txtDocNotes", Multiline = true, Left = 12, Top = 12, Width = 460, Height = 180 });
        tabs.TabPages.AddRange(new[] { main, notes });
        var exit = new Button { Text = "Exit", Name = "btnDocExit", Left = 420, Top = 280, Width = 88, Height = 28 };
        exit.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { tabs, exit });
    }
}

internal sealed class NewCustomerForm : Form
{
    public string Code => _code.Text; public string CustomerName => _name.Text;
    private readonly TextBox _code = new() { Name = "txtNewCode", Width = 160 };
    private readonly TextBox _name = new() { Name = "txtNewName", Width = 220 };

    public NewCustomerForm(Form owner)
    {
        Text = "New Customer";
        ClientSize = new Size(420, 140);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(owner.Location.X + 60, owner.Location.Y + 60);
        Controls.Add(new Label { Text = "Code:", Left = 12, Top = 15, Width = 80 });
        _code.Location = new Point(100, 12); Controls.Add(_code);
        Controls.Add(new Label { Text = "Name:", Left = 12, Top = 47, Width = 80 });
        _name.Location = new Point(100, 44); Controls.Add(_name);
        var ok = new Button { Text = "OK", Name = "btnNewOk", Left = 230, Top = 95, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Name = "btnNewCancel", Left = 320, Top = 95, Width = 80, DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }
}

/// <summary>A JSON file "database": {"customers": {key: row}, "orders": {key: row}}. Rewritten on every save so file watchers see it.</summary>
internal static class Db
{
    private static Dictionary<string, Dictionary<string, Dictionary<string, string?>>> Load()
    {
        try { if (File.Exists(Program.DataFile)) return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string?>>>>(File.ReadAllText(Program.DataFile)) ?? new(); } catch { }
        return new();
    }

    public static void Upsert(string table, string key, Dictionary<string, string?> row)
    {
        var db = Load();
        if (!db.TryGetValue(table, out var t)) db[table] = t = new();
        t[key] = row;
        File.WriteAllText(Program.DataFile, System.Text.Json.JsonSerializer.Serialize(db, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? CustomerName(string code)
    {
        var db = Load();
        return code.Length > 0 && db.TryGetValue("customers", out var t) && t.TryGetValue(code, out var row) ? row.GetValueOrDefault("name") : null;
    }

    public static string NextOrderNo()
    {
        var db = Load();
        var n = db.TryGetValue("orders", out var t) ? t.Count : 0;
        return $"SO-{1000 + n + 1}";
    }
}
