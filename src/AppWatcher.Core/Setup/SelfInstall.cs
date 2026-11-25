using System.Security.Cryptography;
using System.Text;

namespace AppWatcher.Core.Setup;

/// <summary>What a double-click on AppWatcher.exe should do.</summary>
public enum StartChoice
{
    /// <summary>Run the tray app (or the command) from where the exe is.</summary>
    RunHere,
    /// <summary>Copy the exe to the user's install folder, register the login start, start that copy, and leave.</summary>
    InstallAndHandOver,
}

/// <summary>
/// The pure decisions behind the double-click install, the same install scripts\install.ps1 does:
/// copy AppWatcher.exe to %LOCALAPPDATA%\AppWatcher, put appwatcher.cmd next to it, start at login through the
/// Run key of this user ("AppWatcher" = "&lt;exe&gt;" in quotes), then start it. No registry, file or process work here.
/// </summary>
public static class SelfInstall
{
    public const string FolderName = "AppWatcher";
    public const string ExeName = "AppWatcher.exe";
    public const string CmdName = "appwatcher.cmd";
    public const string PortableFolder = "AppWatcherData";
    /// <summary>The HKCU key and value name install.ps1 writes.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "AppWatcher";
    /// <summary>The task install.ps1 -ScheduledTask registers; uninstall removes it too.</summary>
    public const string ScheduledTaskName = "AppWatcher";

    /// <summary>%LOCALAPPDATA%\AppWatcher.</summary>
    public static string InstallFolder(string localAppData) => Path.Combine(localAppData, FolderName);

    /// <summary>%LOCALAPPDATA%\AppWatcher\AppWatcher.exe.</summary>
    public static string InstalledExe(string localAppData) => Path.Combine(InstallFolder(localAppData), ExeName);

    /// <summary>%LOCALAPPDATA%\AppWatcher\appwatcher.cmd.</summary>
    public static string InstalledCmd(string localAppData) => Path.Combine(InstallFolder(localAppData), CmdName);

    /// <summary>The Run value: the installed exe in quotes, exactly as install.ps1 writes it.</summary>
    public static string RunValue(string installedExe) => "\"" + installedExe + "\"";

    /// <summary>The learned data folder (kept by uninstall unless asked): %ProgramData%\AppWatcher.</summary>
    public static string DataFolder(string programData) => Path.Combine(programData, FolderName);

    /// <summary>True when the two paths name the same file (case, slashes, dots and a trailing separator do not matter).</summary>
    public static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when this exe is the installed copy in %LOCALAPPDATA%\AppWatcher.</summary>
    public static bool IsInstalledLocation(string exePath, string localAppData) => SamePath(exePath, InstalledExe(localAppData));

    /// <summary>
    /// A double-click (no arguments at all) on a published one-file exe that is not the installed copy installs itself.
    /// Anything else runs where it is: a command or --tray, a portable copy (--portable or an AppWatcherData folder next
    /// to the exe), the installed copy itself, and a developer build (not one file: the dlls lie next to it).
    /// </summary>
    public static StartChoice Decide(string exePath, IReadOnlyList<string> args, string localAppData, bool portableFolderNextToExe, bool singleFile)
    {
        if (args.Count > 0) return StartChoice.RunHere;
        if (portableFolderNextToExe || !singleFile) return StartChoice.RunHere;
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(localAppData)) return StartChoice.RunHere;
        return IsInstalledLocation(exePath, localAppData) ? StartChoice.RunHere : StartChoice.InstallAndHandOver;
    }

    /// <summary>
    /// The named event a running tray copy listens to so an update can stop it cleanly. One per exe path, so the
    /// installer stops only the copy whose file it replaces, never a portable copy elsewhere.
    /// </summary>
    public static string StopEventName(string exePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(exePath).ToUpperInvariant()));
        return @"Local\AppWatcher.Stop." + Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>True when both files exist and hold the same bytes (a second double-click on the same version copies nothing).</summary>
    public static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string Normalize(string p)
    {
        var full = Path.GetFullPath(p.Trim().Trim('"'));
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
