using System.Diagnostics;
using AppWatcher.Core;
using AppWatcher.Core.Setup;
using Microsoft.Win32;

namespace AppWatcher;

/// <summary>
/// The double-click install and the --install / --uninstall commands: the same work as scripts\install.ps1 and
/// scripts\uninstall.ps1, done by the exe itself so nobody has to run PowerShell with -ExecutionPolicy Bypass.
/// Shows nothing: the installed copy's first-run wizard is the only window. The decisions are in Core (SelfInstall).
/// </summary>
internal static class Installer
{
    public sealed class Result
    {
        public List<string> Lines { get; } = new();
        /// <summary>The installed copy exists and was started (or is already running).</summary>
        public bool Started { get; set; }
        public bool Failed { get; set; }
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Copies this exe to %LOCALAPPDATA%\AppWatcher, registers the Run key, starts the installed copy.</summary>
    public static Result Install(bool start = true)
    {
        var r = new Result();
        var src = Environment.ProcessPath ?? "";
        var dest = SelfInstall.InstalledExe(LocalAppData);
        var folder = SelfInstall.InstallFolder(LocalAppData);
        try
        {
            Directory.CreateDirectory(folder);
            if (SelfInstall.SamePath(src, dest))
                r.Lines.Add("This is the installed copy: " + dest);
            else if (SelfInstall.SameContent(src, dest))
                r.Lines.Add("Already installed, same version: " + dest);
            else if (!File.Exists(dest))
            {
                File.Copy(src, dest);
                r.Lines.Add("Installed to " + dest);
            }
            else if (StopInstalledCopy(dest, r) && TryCopy(src, dest))
                r.Lines.Add("Installed to " + dest);
            else
                r.Lines.Add("The installed copy is running and could not be stopped safely, so it was kept; quit it from the tray icon and install again to update it.");

            // command-line wrapper (start /wait) so scripts can call AppWatcher.exe --learn and get its output
            var cmdSrc = Path.Combine(Path.GetDirectoryName(src) ?? "", SelfInstall.CmdName);
            var cmdDest = SelfInstall.InstalledCmd(LocalAppData);
            if (File.Exists(cmdSrc) && !SelfInstall.SamePath(cmdSrc, cmdDest))
                try { File.Copy(cmdSrc, cmdDest, overwrite: true); } catch (Exception ex) { Log.Warn("could not copy appwatcher.cmd: " + ex.Message); }

            using (var run = Registry.CurrentUser.CreateSubKey(SelfInstall.RunKeyPath))
                run.SetValue(SelfInstall.RunValueName, SelfInstall.RunValue(dest), RegistryValueKind.String);
            r.Lines.Add("Starts at login through the Run key for this user.");

            if (start && File.Exists(dest))
            {
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
                r.Started = true;
                r.Lines.Add("App Watcher started. Look for the eye icon in the tray; the setup window opens on the first run.");
            }
            r.Lines.Add("Data folder: " + SelfInstall.DataFolder(ProgramData));
        }
        catch (Exception ex)
        {
            r.Failed = true;
            r.Lines.Add("Install failed: " + ex.Message);
            Log.Error("install failed", ex);
        }
        foreach (var l in r.Lines) Log.Info("install: " + l);
        return r;
    }

    /// <summary>
    /// Stops the tray copy running from the installed path, if any, through its stop event (it saves and quits like
    /// Quit does). An older copy without the event, or a command still running from that file, is left alone: false.
    /// </summary>
    private static bool StopInstalledCopy(string dest, Result r)
    {
        var running = ProcessesAt(dest);
        try
        {
            if (running.Count == 0) return true;
            if (!EventWaitHandle.TryOpenExisting(SelfInstall.StopEventName(dest), out var stop)) return false;
            r.Lines.Add("Stopping the running App Watcher (it will be restarted)...");
            using (stop) stop.Set();
            return running.All(p => { try { return p.WaitForExit(10_000); } catch { return false; } });
        }
        finally { foreach (var p in running) p.Dispose(); }
    }

    /// <summary>The file can stay locked for a moment after its process ended.</summary>
    private static bool TryCopy(string src, string dest)
    {
        for (var i = 0; i < 10; i++)
        {
            try { File.Copy(src, dest, overwrite: true); return true; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
        return false;
    }

    /// <summary>Other AppWatcher processes whose file is this path (never this process).</summary>
    private static List<Process> ProcessesAt(string exe) =>
        OtherAppWatchers().Where(p => { var ok = SelfInstall.SamePath(p.path, exe); if (!ok) p.proc.Dispose(); return ok; }).Select(p => p.proc).ToList();

    private static List<(Process proc, string path)> OtherAppWatchers()
    {
        var list = new List<(Process, string)>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SelfInstall.ExeName)))
        {
            string? path = null;
            if (p.Id != Environment.ProcessId) try { path = p.MainModule?.FileName; } catch { }
            if (path is null) { p.Dispose(); continue; }
            list.Add((p, path));
        }
        return list;
    }

    /// <summary>The same as scripts\uninstall.ps1: stop it, remove the login start (Run key and scheduled task), delete the program folder; the data is kept unless asked.</summary>
    public static int Uninstall(bool removeData)
    {
        // stop every other App Watcher: cleanly through its stop event, then the way uninstall.ps1 does
        foreach (var (p, path) in OtherAppWatchers())
        {
            using (p)
            {
                Console.WriteLine("Stopping App Watcher...");
                try
                {
                    if (EventWaitHandle.TryOpenExisting(SelfInstall.StopEventName(path), out var stop)) using (stop) stop.Set();
                    if (!p.WaitForExit(5_000)) { p.Kill(); p.WaitForExit(5_000); }
                }
                catch (Exception ex) { Console.Error.WriteLine("Could not stop process " + p.Id + ": " + ex.Message); }
            }
        }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(SelfInstall.RunKeyPath, writable: true);
            run?.DeleteValue(SelfInstall.RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not remove the login start: " + ex.Message); }
        RemoveScheduledTask();

        var folder = SelfInstall.InstallFolder(LocalAppData);
        if (Directory.Exists(folder))
        {
            var self = Environment.ProcessPath ?? "";
            if (SelfInstall.SamePath(Path.GetDirectoryName(self) ?? "", folder))
            {
                // this very exe is in that folder: Windows keeps a running file, so the folder goes a moment after this command ends
                var psi = new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{folder}\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi)?.Dispose();
                Console.WriteLine($"Removing {folder} in a few seconds (this program runs from it).");
            }
            else
            {
                try { Directory.Delete(folder, recursive: true); Console.WriteLine("Removed " + folder); }
                catch (Exception ex) { Console.Error.WriteLine($"Could not remove {folder}: {ex.Message}"); }
            }
        }

        var data = SelfInstall.DataFolder(ProgramData);
        if (removeData)
        {
            if (Directory.Exists(data))
            {
                try { Directory.Delete(data, recursive: true); Console.WriteLine("Removed the data folder " + data); }
                catch (Exception ex) { Console.Error.WriteLine($"Could not remove the data folder {data}: {ex.Message}"); }
            }
        }
        else Console.WriteLine($"Kept the data folder {data} (add --remove-data to delete it).");
        Console.WriteLine("App Watcher is uninstalled.");
        return 0;
    }

    private static void RemoveScheduledTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{SelfInstall.ScheduledTaskName}\" /F")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (p.WaitForExit(10_000) && p.ExitCode == 0) Console.WriteLine("Removed the scheduled task " + SelfInstall.ScheduledTaskName + ".");
        }
        catch { /* no task, or schtasks missing: nothing to remove */ }
    }
}
