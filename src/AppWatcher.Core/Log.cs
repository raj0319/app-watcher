namespace AppWatcher.Core;

/// <summary>Tiny file logger. Rotates at 2 MB. Never logs values typed by the user.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static Action<string>? Sink { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message} @ {FirstFrame(ex)}");

    /// <summary>The first stack frame inside App Watcher's own code, so a log line says where, not only what.</summary>
    private static string FirstFrame(Exception ex)
    {
        try
        {
            var lines = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at "));
            var own = lines.FirstOrDefault(l => l.Contains("AppWatcher.")) ?? lines.FirstOrDefault() ?? "";
            return own.Length > 160 ? own[..160] : own;
        }
        catch { return ""; }
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}";
        Sink?.Invoke(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Root);
                var f = Paths.LogFile;
                if (File.Exists(f) && new FileInfo(f).Length > 2 * 1024 * 1024)
                {
                    var old = f + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(f, old);
                }
                File.AppendAllText(f, line + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
    }
}
