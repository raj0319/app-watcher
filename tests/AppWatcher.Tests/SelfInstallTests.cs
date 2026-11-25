using AppWatcher.Core.Setup;
using Xunit;

namespace AppWatcher.Tests;

/// <summary>The double-click install: which start installs, which paths and which Run value (the same as scripts\install.ps1). No registry or real install here.</summary>
public class SelfInstallTests
{
    private const string Local = @"C:\Users\someone\AppData\Local";
    private const string Downloads = @"C:\Users\someone\Downloads\AppWatcher-0.13.2-win-x64\AppWatcher.exe";

    [Fact]
    public void Paths_match_install_ps1()
    {
        Assert.Equal(@"C:\Users\someone\AppData\Local\AppWatcher", SelfInstall.InstallFolder(Local));
        Assert.Equal(@"C:\Users\someone\AppData\Local\AppWatcher\AppWatcher.exe", SelfInstall.InstalledExe(Local));
        Assert.Equal(@"C:\Users\someone\AppData\Local\AppWatcher\appwatcher.cmd", SelfInstall.InstalledCmd(Local));
        Assert.Equal(@"C:\ProgramData\AppWatcher", SelfInstall.DataFolder(@"C:\ProgramData"));
    }

    [Fact]
    public void Run_key_is_the_one_install_ps1_writes()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", SelfInstall.RunKeyPath);
        Assert.Equal("AppWatcher", SelfInstall.RunValueName);
        Assert.Equal("\"C:\\Users\\someone\\AppData\\Local\\AppWatcher\\AppWatcher.exe\"", SelfInstall.RunValue(SelfInstall.InstalledExe(Local)));
        Assert.Equal("AppWatcher", SelfInstall.ScheduledTaskName);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\AppWatcher\AppWatcher.exe", true)]
    [InlineData(@"c:\users\SOMEONE\appdata\local\appwatcher\appwatcher.EXE", true)]
    [InlineData(@"C:/Users/someone/AppData/Local/AppWatcher/AppWatcher.exe", true)]
    [InlineData(@"C:\Users\someone\AppData\Local\AppWatcher\..\AppWatcher\AppWatcher.exe", true)]
    [InlineData(@"""C:\Users\someone\AppData\Local\AppWatcher\AppWatcher.exe""", true)]
    [InlineData(Downloads, false)]
    [InlineData(@"C:\Users\someone\AppData\Local\AppWatcher2\AppWatcher.exe", false)]
    [InlineData(@"C:\Users\other\AppData\Local\AppWatcher\AppWatcher.exe", false)]
    [InlineData("", false)]
    public void Installed_location_is_recognised(string exe, bool installed) =>
        Assert.Equal(installed, SelfInstall.IsInstalledLocation(exe, Local));

    [Fact]
    public void Double_click_on_the_downloaded_exe_installs() =>
        Assert.Equal(StartChoice.InstallAndHandOver, SelfInstall.Decide(Downloads, Array.Empty<string>(), Local, portableFolderNextToExe: false, singleFile: true));

    [Fact]
    public void The_installed_copy_runs_where_it_is() =>
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide(SelfInstall.InstalledExe(Local), Array.Empty<string>(), Local, false, true));

    [Theory]
    [InlineData("--portable")]
    [InlineData("--tray")]
    [InlineData("--status")]
    [InlineData("--install")]
    public void Any_argument_runs_where_it_is(string arg) =>
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide(Downloads, new[] { arg }, Local, false, true));

    [Fact]
    public void A_portable_copy_with_its_data_folder_runs_where_it_is() =>
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide(Downloads, Array.Empty<string>(), Local, portableFolderNextToExe: true, singleFile: true));

    [Fact]
    public void A_developer_build_never_installs_itself() =>
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide(@"C:\src\app-watcher\src\AppWatcher\bin\Debug\AppWatcher.exe", Array.Empty<string>(), Local, false, singleFile: false));

    [Fact]
    public void Unknown_folders_run_where_they_are()
    {
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide("", Array.Empty<string>(), Local, false, true));
        Assert.Equal(StartChoice.RunHere, SelfInstall.Decide(Downloads, Array.Empty<string>(), "", false, true));
    }

    [Fact]
    public void Stop_event_is_per_exe_path_and_ignores_case()
    {
        var installed = SelfInstall.StopEventName(SelfInstall.InstalledExe(Local));
        Assert.StartsWith(@"Local\AppWatcher.Stop.", installed);
        Assert.Equal(installed, SelfInstall.StopEventName(SelfInstall.InstalledExe(Local).ToLowerInvariant()));
        Assert.Equal(installed, SelfInstall.StopEventName(SelfInstall.InstalledExe(Local).Replace('\\', '/')));
        Assert.NotEqual(installed, SelfInstall.StopEventName(Downloads));
        Assert.Equal(installed.Length, SelfInstall.StopEventName(Downloads).Length);
    }

    [Fact]
    public void Same_content_compares_the_bytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aw-selfinstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.exe"), b = Path.Combine(dir, "b.exe"), c = Path.Combine(dir, "c.exe");
            File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(b, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(c, new byte[] { 1, 2, 3, 5 });
            Assert.True(SelfInstall.SameContent(a, b));
            Assert.False(SelfInstall.SameContent(a, c));
            Assert.False(SelfInstall.SameContent(a, Path.Combine(dir, "missing.exe")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
