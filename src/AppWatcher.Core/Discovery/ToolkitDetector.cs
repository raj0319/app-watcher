using System.Text;

namespace AppWatcher.Core.Discovery;

public sealed class ToolkitInfo
{
    /// <summary>WinForms | WPF | Infragistics | DevExpress | Telerik | Qt | Delphi | Electron | Java | Access | VB6 | MFC | PowerBuilder | WinUI | Avalonia | Flutter | Unknown</summary>
    public string Name { get; set; } = "Unknown";
    public List<string> Hints { get; set; } = new();
    public bool IsDotNet { get; set; }
    /// <summary>What to expect from UI Automation with this toolkit, in plain words.</summary>
    public string Expectation { get; set; } = "";
    /// <summary>Suggested profile name (profiles/*.json).</summary>
    public string Profile { get; set; } = "generic";
}

/// <summary>
/// Decides the UI toolkit from the program file and the files next to it, without running or decompiling
/// anything: the exe's import table and referenced-assembly strings, and the names of sibling DLLs.
/// Bounded: reads at most the first 24 MB of the exe, lists one folder.
/// </summary>
public static class ToolkitDetector
{
    private static readonly (string marker, string toolkit, string hint)[] Markers =
    {
        ("Infragistics", "Infragistics", "Infragistics assemblies"),
        ("DevExpress", "DevExpress", "DevExpress assemblies"),
        ("Telerik", "Telerik", "Telerik assemblies"),
        ("Syncfusion", "Syncfusion", "Syncfusion assemblies"),
        ("ComponentOne", "ComponentOne", "ComponentOne assemblies"),
        ("Janus", "Janus", "Janus assemblies"),
        ("PresentationFramework", "WPF", "PresentationFramework reference"),
        ("Microsoft.UI.Xaml", "WinUI", "WinUI reference"),
        ("Avalonia", "Avalonia", "Avalonia reference"),
        ("System.Windows.Forms", "WinForms", "System.Windows.Forms reference"),
        ("Qt5Core", "Qt", "Qt5 import"),
        ("Qt6Core", "Qt", "Qt6 import"),
        ("libcef", "Electron/CEF", "Chromium Embedded import"),
        ("electron", "Electron/CEF", "Electron"),
        ("flutter_windows", "Flutter", "Flutter engine"),
        ("msvbvm60", "VB6", "VB6 runtime import"),
        ("mfc1", "MFC", "MFC import"),
        ("pbvm", "PowerBuilder", "PowerBuilder VM import"),
        ("jvm.dll", "Java", "Java VM"),
        ("Embarcadero", "Delphi", "Embarcadero marker"),
        ("Borland", "Delphi", "Borland marker"),
        ("TForm", "Delphi", "VCL form class"),
        ("Vcl.Forms", "Delphi", "VCL reference"),
        ("wxWidgets", "wxWidgets", "wxWidgets"),
        ("gtk-3", "GTK", "GTK import"),
        ("Gdiplus", "", "GDI+"),
    };

    public static ToolkitInfo Detect(string? exePath)
    {
        var info = new ToolkitInfo();
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) { info.Expectation = Expect(info.Name); return info; }
        var scores = new Dictionary<string, int>();
        void Hit(string toolkit, string hint, int weight = 1)
        {
            if (toolkit.Length == 0) return;
            scores[toolkit] = scores.GetValueOrDefault(toolkit) + weight;
            if (!info.Hints.Contains(hint)) info.Hints.Add(hint);
        }
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name is "msaccess") Hit("Access", "msaccess.exe", 10);
        if (name is "javaw" or "java") Hit("Java", "java launcher", 10);
        if (name is "excel" or "winword") Hit("Office", "Office program", 10);

        // 1. strings inside the exe (imports, .NET assembly references, class names); for a framework-dependent
        //    .NET app the exe is only a host, so its <name>.dll and <name>.runtimeconfig.json are read as well
        var files = new List<string> { exePath };
        var dir0 = Path.GetDirectoryName(exePath)!;
        var stem = Path.GetFileNameWithoutExtension(exePath);
        foreach (var extra in new[] { Path.Combine(dir0, stem + ".dll"), Path.Combine(dir0, stem + ".runtimeconfig.json"), Path.Combine(dir0, stem + ".deps.json") })
            if (File.Exists(extra)) files.Add(extra);
        foreach (var file in files)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var len = (int)Math.Min(fs.Length, 24L * 1024 * 1024);
                var buf = new byte[len];
                var read = 0;
                while (read < len) { var n = fs.Read(buf, read, len - read); if (n <= 0) break; read += n; }
                var ascii = Encoding.ASCII.GetString(buf, 0, read);
                var where = file == exePath ? "exe" : Path.GetFileName(file);
                if (ascii.Contains("mscoree.dll", StringComparison.OrdinalIgnoreCase) || ascii.Contains("System.Runtime", StringComparison.Ordinal) || ascii.Contains("#Strings", StringComparison.Ordinal) || ascii.Contains("Microsoft.NETCore.App", StringComparison.Ordinal)) info.IsDotNet = true;
                if (ascii.Contains("Microsoft.WindowsDesktop.App", StringComparison.Ordinal) && !ascii.Contains("PresentationFramework", StringComparison.Ordinal)) Hit("WinForms", "WindowsDesktop runtime in " + where, 1);
                foreach (var (marker, toolkit, hint) in Markers)
                    if (ascii.Contains(marker, StringComparison.OrdinalIgnoreCase)) Hit(toolkit, hint + " in " + where, marker.Length > 8 ? 3 : 2);
                // .NET single-file hosts and UTF-16 strings
                var utf16 = Encoding.Unicode.GetString(buf, 0, read - read % 2);
                foreach (var (marker, toolkit, hint) in Markers)
                    if (marker.Length > 5 && utf16.Contains(marker, StringComparison.OrdinalIgnoreCase)) Hit(toolkit, hint + " in " + where, 2);
            }
            catch (Exception ex) { info.Hints.Add(Path.GetFileName(file) + " not readable: " + ex.Message); }
        }

        // 2. sibling DLL names (never opened)
        try
        {
            var dir = Path.GetDirectoryName(exePath)!;
            var siblings = Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(f => f is not null).Select(f => f!).Take(3000).ToList();
            foreach (var f in siblings)
            {
                foreach (var (marker, toolkit, hint) in Markers)
                    if (f.Contains(marker, StringComparison.OrdinalIgnoreCase)) Hit(toolkit, hint + " next to exe", 2);
                if (f.Equals("resources.pak", StringComparison.OrdinalIgnoreCase) || f.Equals("chrome_elf.dll", StringComparison.OrdinalIgnoreCase)) Hit("Electron/CEF", "Chromium files next to exe", 3);
                if (f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) Hit("Java", "jar next to exe", 1);
                if (f.EndsWith(".bpl", StringComparison.OrdinalIgnoreCase)) Hit("Delphi", "Delphi package next to exe", 3);
                if (f.EndsWith(".pbd", StringComparison.OrdinalIgnoreCase)) Hit("PowerBuilder", "PowerBuilder library next to exe", 3);
                if (f.Equals("app.asar", StringComparison.OrdinalIgnoreCase)) Hit("Electron/CEF", "app.asar", 3);
            }
            if (Directory.Exists(Path.Combine(dir, "resources")) && File.Exists(Path.Combine(dir, "resources", "app.asar"))) Hit("Electron/CEF", "resources/app.asar", 4);
            if (Directory.Exists(Path.Combine(dir, "jre")) || Directory.Exists(Path.Combine(dir, "runtime", "bin"))) Hit("Java", "bundled JRE", 3);
        }
        catch { }

        // component suites sit on top of WinForms or WPF; the suite decides the collector profile
        var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
        if (ranked.Count > 0)
        {
            var suite = ranked.FirstOrDefault(s => s.Key is "Infragistics" or "DevExpress" or "Telerik" or "Syncfusion" or "ComponentOne" or "Janus");
            var baseKit = ranked.FirstOrDefault(s => s.Key is "WPF" or "WinForms" or "WinUI" or "Avalonia");
            if (suite.Key is not null) info.Name = baseKit.Key is not null ? $"{suite.Key} on {baseKit.Key}" : suite.Key;
            else info.Name = ranked[0].Key;
        }
        else if (info.IsDotNet) info.Name = "WinForms";
        info.Profile = ProfileFor(info.Name);
        info.Expectation = Expect(info.Name);
        return info;
    }

    public static string ProfileFor(string toolkit)
    {
        var t = toolkit.ToLowerInvariant();
        if (t.Contains("infragistics")) return "infragistics";
        if (t.Contains("devexpress")) return "devexpress";
        if (t.Contains("telerik") || t.Contains("syncfusion") || t.Contains("componentone") || t.Contains("janus")) return "component-suite";
        if (t.Contains("wpf") || t.Contains("winui") || t.Contains("avalonia")) return "wpf";
        if (t.Contains("winforms")) return "winforms";
        if (t.Contains("electron") || t.Contains("cef")) return "electron";
        if (t.Contains("delphi") || t.Contains("vb6") || t.Contains("powerbuilder")) return "classic";
        if (t.Contains("java")) return "java";
        if (t.Contains("qt") || t.Contains("gtk") || t.Contains("wx") || t.Contains("flutter")) return "custom-drawn";
        if (t.Contains("access") || t.Contains("office")) return "office";
        return "generic";
    }

    public static string Expect(string toolkit) => ProfileFor(toolkit) switch
    {
        "infragistics" => "Infragistics controls expose fields and grids well, but menus and toolbar buttons rarely report presses; the MSAA and inference fallbacks are on, and tab documents are recorded as screens.",
        "devexpress" => "DevExpress controls expose fields; ribbon and bar items report presses through MSAA more reliably than through UI Automation, so that fallback is on.",
        "component-suite" => "A component suite on WinForms or WPF: fields and grids are exposed; presses may need the MSAA fallback (on).",
        "wpf" => "WPF exposes fields, buttons and presses well through UI Automation; virtualized lists show only visible rows.",
        "winforms" => "Standard WinForms controls expose fields, buttons and presses well; owner-drawn menus may need the inference fallback (on).",
        "electron" => "A web app in a window: Chromium exposes the page tree when accessibility is on (it is, once something subscribes); names come from labels and ARIA attributes.",
        "classic" => "Delphi, VB6 and PowerBuilder programs expose standard controls through MSAA; custom-drawn grids show as one control. Names only, no automation ids.",
        "java" => "Java Swing exposes nothing to UI Automation unless the Java Access Bridge is enabled (jabswitch -enable). Window titles, timing and data changes are still recorded.",
        "custom-drawn" => "This toolkit draws its own controls; UI Automation sees few or none. Window titles, timing and data changes are recorded; fields and buttons probably not.",
        "office" => "Office programs expose their UI; the document content itself is read through the Document pattern only.",
        _ => "Unknown toolkit: fields and presses depend on what the program exposes; the first hour will tell (health.md).",
    };
}
