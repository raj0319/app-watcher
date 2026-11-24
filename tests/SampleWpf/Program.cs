using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SampleWpf;

/// <summary>
/// A tiny WPF "ERP" mirroring tests/SampleApp (WinForms), to prove the watcher on a second toolkit: a login window,
/// a main window with a menu, a tabbed workspace (Customer Master, Orders, Quote), a New Customer dialog and
/// message windows. Synthetic data only. It never shows a real MessageBox (it would block the test): its message
/// windows are plain windows (title, text, OK) that close by themselves after two seconds. The blocking dialog
/// is opened through the dispatcher, so a UI Automation Invoke returns at once.
/// Driven by handle from scripts/live-test-wpf.ps1. --no-login skips the login window, --corner opens at the bottom-right.
/// </summary>
internal static class Program
{
    public static readonly string DataFile = Path.Combine(AppContext.BaseDirectory, "sample-wpf-data.json");

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var corner = args.Contains("--corner");
        if (!args.Contains("--no-login"))
        {
            var login = new LoginWindow(corner);
            if (login.ShowDialog() != true) return;
        }
        var main = new MainWindow(corner);
        main.Closed += (_, _) => app.Shutdown();
        app.Run(main);
    }
}

internal static class Ui
{
    public static T Id<T>(this T e, string id, string? name = null) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(e, id);
        if (name != null) AutomationProperties.SetName(e, name);
        return e;
    }

    public static TextBlock Label(string text, double left, double top, double width = 110) =>
        new TextBlock { Text = text, Width = width }.At(left, top);

    public static T At<T>(this T e, double left, double top) where T : FrameworkElement
    {
        Canvas.SetLeft(e, left); Canvas.SetTop(e, top); return e;
    }

    public static (double x, double y) Corner(double w, double h) =>
        (SystemParameters.WorkArea.Right - w - 20, SystemParameters.WorkArea.Bottom - h - 40);
}

internal sealed class LoginWindow : Window
{
    public LoginWindow(bool corner)
    {
        Title = "Sample WPF ERP - Login";
        Width = 320; Height = 170;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (corner) { WindowStartupLocation = WindowStartupLocation.Manual; (Left, Top) = Ui.Corner(Width, Height); }
        var c = new Canvas();
        var user = new TextBox { Width = 180 }.Id("txtUser", "User").At(100, 10);
        var pw = new PasswordBox { Width = 180 }.Id("txtPassword", "Password").At(100, 40);
        var ok = new Button { Content = "Log in", Width = 90, IsDefault = true }.Id("btnLogin").At(100, 80);
        ok.Click += (_, _) => DialogResult = true;
        c.Children.Add(Ui.Label("User", 10, 12, 80)); c.Children.Add(user);
        c.Children.Add(Ui.Label("Password", 10, 42, 80)); c.Children.Add(pw);
        c.Children.Add(ok);
        Content = c;
    }
}

internal sealed class OrderLine : INotifyPropertyChanged
{
    private string _product = "", _qty = "1", _price = "0";
    public int Id { get; init; }
    public string Product { get => _product; set { _product = value; Raise(); } }
    public string Quantity { get => _qty; set { _qty = value; Raise(); Raise(nameof(LineTotal)); } }
    public string UnitPrice { get => _price; set { _price = value; Raise(); Raise(nameof(LineTotal)); } }
    /// <summary>Line total = Quantity x Unit price, calculated by the program.</summary>
    public string LineTotal => decimal.TryParse(_qty, out var q) && decimal.TryParse(_price, out var p) ? (q * p).ToString("0.00") : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

internal sealed class MainWindow : Window
{
    private static readonly string[] Products = { "Widget", "Wing nut", "Gadget", "Gizmo", "Wire spool" };
    private static readonly Dictionary<string, string> KnownCustomers = new() { ["C001"] = "Contoso Trading", ["C002"] = "Fabrikam Supplies" };

    private readonly TabControl _tabs = new TabControl().Id("tabWorkspace");
    private readonly TextBox _custCode = new TextBox { Width = 160 }.Id("txtCustomerCode", "Customer code");
    private readonly TextBox _custName = new TextBox { Width = 220 }.Id("txtCustomerName", "Name");
    private readonly TextBlock _status = new TextBlock { Text = "Ready" }.Id("lblStatus", "Status");
    private readonly System.Collections.ObjectModel.ObservableCollection<OrderLine> _lines = new();
    private int _nextLine = 1;

    public MainWindow(bool corner)
    {
        Title = "Sample WPF ERP";
        Width = 620; Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (corner) { WindowStartupLocation = WindowStartupLocation.Manual; (Left, Top) = Ui.Corner(Width, Height); }

        var menu = new Menu().Id("mnuMain");
        var file = new MenuItem { Header = "File" }.Id("mnuFile");
        var newCustomer = new MenuItem { Header = "New Customer..." }.Id("mnuNewCustomer");
        newCustomer.Click += (_, _) => NewCustomer();
        var exit = new MenuItem { Header = "Exit" }.Id("mnuExit");
        exit.Click += (_, _) => Close();
        file.Items.Add(newCustomer); file.Items.Add(new Separator()); file.Items.Add(exit);
        var view = new MenuItem { Header = "View" }.Id("mnuView");
        string[] tabNames = { "Customer Master", "Orders", "Quote" };
        for (var i = 0; i < tabNames.Length; i++)
        {
            var index = i;
            var mi = new MenuItem { Header = tabNames[i] }.Id("mnuView" + tabNames[i].Replace(" ", ""));
            mi.Click += (_, _) => _tabs.SelectedIndex = index;
            view.Items.Add(mi);
        }
        var help = new MenuItem { Header = "Help" }.Id("mnuHelp");
        var about = new MenuItem { Header = "About" }.Id("mnuAbout");
        about.Click += (_, _) => Message("About", "Sample WPF ERP 1.0 - test application for App Watcher.");
        help.Items.Add(about);
        menu.Items.Add(file); menu.Items.Add(view); menu.Items.Add(help);

        _tabs.Items.Add(new TabItem { Header = "Customer Master", Content = BuildCustomers() }.Id("tabCustomers", "Customer Master"));
        _tabs.Items.Add(new TabItem { Header = "Orders", Content = BuildOrders() }.Id("tabOrders", "Orders"));
        _tabs.Items.Add(new TabItem { Header = "Quote", Content = BuildQuote() }.Id("tabQuote", "Quote"));

        var dock = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        var bar = new System.Windows.Controls.Primitives.StatusBar();
        bar.Items.Add(_status);
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(menu); dock.Children.Add(bar); dock.Children.Add(_tabs);
        Content = dock;
    }

    private UIElement BuildCustomers()
    {
        var c = new Canvas();
        c.Children.Add(Ui.Label("Customer code:", 16, 19)); c.Children.Add(_custCode.At(132, 16));
        c.Children.Add(Ui.Label("Name:", 16, 51)); c.Children.Add(_custName.At(132, 48));
        // the program fills the name when a known code is typed and the focus leaves the code box
        _custCode.LostFocus += (_, _) =>
        {
            var code = _custCode.Text.Trim();
            var name = Db.CustomerName(code) ?? (KnownCustomers.TryGetValue(code, out var n) ? n : null);
            if (name != null) _custName.Text = name;
        };
        var save = new Button { Content = "Save", Width = 90 }.Id("btnSaveCustomer").At(132, 90);
        save.Click += (_, _) => SaveCustomer();
        var clear = new Button { Content = "Clear", Width = 90 }.Id("btnClear").At(230, 90);
        clear.Click += (_, _) => { _custCode.Text = ""; _custName.Text = ""; };
        var nw = new Button { Content = "New...", Width = 90 }.Id("btnNewCustomer").At(328, 90);
        nw.Click += (_, _) => NewCustomer();
        c.Children.Add(save); c.Children.Add(clear); c.Children.Add(nw);
        return c;
    }

    private UIElement BuildOrders()
    {
        var c = new Canvas();
        c.Children.Add(Ui.Label("Product lookup:", 16, 19));
        var combo = new ComboBox { Width = 200, IsEditable = true, ItemsSource = Products }.Id("cboProduct", "Product lookup").At(132, 16);
        // a lookup: two or more typed characters open the drop-down list of matching products
        combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            var t = combo.Text ?? "";
            if (t.Length >= 2 && Products.Any(p => p.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            {
                combo.Items.Filter = o => ((string)o).StartsWith(t, StringComparison.OrdinalIgnoreCase);
                combo.IsDropDownOpen = true;
            }
            else { combo.Items.Filter = null; combo.IsDropDownOpen = false; }
        }));
        var add = new Button { Content = "Add Line", Width = 90 }.Id("btnAddLine").At(350, 14);
        add.Click += (_, _) =>
        {
            var id = _nextLine++;
            var product = string.IsNullOrWhiteSpace(combo.Text) ? "Widget" : combo.Text.Trim();
            _lines.Add(new OrderLine { Id = id, Product = product, Quantity = "1", UnitPrice = "2.50" });
            _status.Text = $"Line {id} added";
        };
        var grid = new DataGrid { Width = 560, Height = 240, AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = _lines }.Id("grdLines", "Order lines").At(16, 56);
        grid.Columns.Add(TextColumn("Product", "Product", "txtProduct_"));
        grid.Columns.Add(TextColumn("Quantity", "Quantity", "txtQty_"));
        grid.Columns.Add(TextColumn("Unit price", "UnitPrice", "txtPrice_"));
        grid.Columns.Add(new DataGridTextColumn { Header = "Line total", Binding = new System.Windows.Data.Binding("LineTotal") { Mode = System.Windows.Data.BindingMode.OneWay }, IsReadOnly = true });
        c.Children.Add(combo); c.Children.Add(add); c.Children.Add(grid);
        return c;
    }

    /// <summary>An editable column whose cell always holds a text box (committed on every keystroke), so the program recalculates at once.</summary>
    private static DataGridTemplateColumn TextColumn(string header, string path, string idPrefix)
    {
        var xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:a='clr-namespace:System.Windows.Automation;assembly=PresentationCore'>" +
                   $"<TextBox BorderThickness='0' Text='{{Binding {path}, UpdateSourceTrigger=PropertyChanged}}' a:AutomationProperties.AutomationId='{{Binding Id, StringFormat={idPrefix}{{0}}}}' />" +
                   "</DataTemplate>";
        return new DataGridTemplateColumn { Header = header, Width = 130, CellTemplate = (DataTemplate)XamlReader.Parse(xaml) };
    }

    private UIElement BuildQuote()
    {
        var c = new Canvas();
        var print = new Button { Content = "Print quote", Width = 110 }.Id("btnPrintQuote").At(16, 16); // does nothing
        c.Children.Add(print);
        return c;
    }

    private void NewCustomer()
    {
        // opened through the dispatcher so the caller (a UI Automation Invoke) is not held until the dialog closes
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var d = new NewCustomerWindow(this);
            if (d.ShowDialog() == true) { _custCode.Text = d.Code; _custName.Text = d.CustomerName; }
        }));
    }

    private void SaveCustomer()
    {
        if (_custCode.Text.Trim().Length < 3) { Message("Validation", "Customer code must have at least 3 characters."); return; }
        Db.Upsert("customers", _custCode.Text.Trim(), new Dictionary<string, string?> { ["code"] = _custCode.Text.Trim(), ["name"] = _custName.Text.Trim() });
        _status.Text = $"Customer {_custCode.Text} saved";
        Message("Saved", "Customer saved.");
    }

    /// <summary>A message window (title, text, OK) that closes itself after two seconds, so no modal ever lingers.</summary>
    internal void Message(string title, string text)
    {
        var w = new Window { Title = title, Width = 360, Height = 150, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var c = new Canvas();
        c.Children.Add(new TextBlock { Text = text, Width = 330, TextWrapping = TextWrapping.Wrap }.Id("lblBody", "Message").At(12, 12));
        var ok = new Button { Content = "OK", Width = 78, IsDefault = true }.Id("btnOK").At(260, 76);
        ok.Click += (_, _) => w.Close();
        c.Children.Add(ok);
        w.Content = c;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); if (w.IsLoaded) w.Close(); };
        timer.Start();
        w.Show();
    }
}

internal sealed class NewCustomerWindow : Window
{
    private readonly TextBox _code = new TextBox { Width = 160 }.Id("txtNewCode", "Code");
    private readonly TextBox _name = new TextBox { Width = 220 }.Id("txtNewName", "Name");
    public string Code => _code.Text; public string CustomerName => _name.Text;

    public NewCustomerWindow(Window owner)
    {
        Title = "New Customer";
        Width = 440; Height = 190;
        Owner = owner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var c = new Canvas();
        c.Children.Add(Ui.Label("Code:", 12, 15, 80)); c.Children.Add(_code.At(100, 12));
        c.Children.Add(Ui.Label("Name:", 12, 47, 80)); c.Children.Add(_name.At(100, 44));
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true }.Id("btnNewOk").At(230, 95);
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true }.Id("btnNewCancel").At(320, 95);
        ok.Click += (_, _) => DialogResult = true;
        c.Children.Add(ok); c.Children.Add(cancel);
        Content = c;
    }
}

/// <summary>A JSON file "database": {"customers": {key: row}}. Rewritten on every save so file watchers see it.</summary>
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
}
