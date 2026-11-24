using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SampleWeb;

/// <summary>
/// A WinForms host with one WebView2 control showing a local web page (web/index.html) that mirrors tests/SampleApp.
/// No network: the page is served from a virtual host name mapped to the folder next to the exe. The browser's
/// user data goes to %TEMP%, never next to the exe. Driven by handle from scripts/live-test-web.ps1. Run with --corner
/// to open at the bottom-right corner. Synthetic data only.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new HostForm(args.Contains("--corner")));
    }
}

internal sealed class HostForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, Name = "webView" };

    public HostForm(bool corner)
    {
        Text = "Sample Web ERP";
        ClientSize = new Size(900, 600);
        StartPosition = corner ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
        if (corner) Location = new Point(Math.Max(0, Screen.PrimaryScreen!.WorkingArea.Right - 920), Math.Max(0, Screen.PrimaryScreen.WorkingArea.Bottom - 660));
        Controls.Add(_web);
        Load += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "SampleWebData");
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping("sample.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
        // the page asks the host to close the window (the Exit button)
        core.WebMessageReceived += (_, e) => { if (e.TryGetWebMessageAsString() == "exit") Close(); };
        // the page's own document title must not replace the window title the watcher sees
        core.DocumentTitleChanged += (_, _) => Text = "Sample Web ERP";
        core.Navigate("https://sample.local/index.html");
    }
}
