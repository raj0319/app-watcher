using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AppWatcher.Core.Learning;
using Microsoft.Win32;

namespace AppWatcher.Core.Discovery;

/// <summary>
/// Finds where the app's data lives without reading any of it: connection strings in config files next to
/// the exe and under ProgramData/AppData, ODBC data sources in the registry, and the TCP connections the
/// running process holds (host and port only). Passwords are stripped before anything is kept.
/// </summary>
public static partial class BackendDetector
{
    [GeneratedRegex(@"(?i)(?:data source|server|host|hostname|dsn|provider|driver|database|initial catalog|port|jdbc:[a-z]+|url)\s*=\s*([^;""'\s<]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)(password|pwd|pass|secret|token|apikey|api_key)\s*=\s*[^;""'\s<]+")]
    private static partial Regex Secret();

    private static readonly string[] ConfigExtensions = { ".config", ".ini", ".udl", ".xml", ".json", ".conf", ".cfg", ".properties", ".dsn", ".txt" };

    public static readonly Dictionary<int, string> KnownPorts = new()
    {
        [1433] = "sqlserver", [1434] = "sqlserver", [3306] = "mysql", [3307] = "mysql", [5432] = "postgresql", [5433] = "postgresql", [1521] = "oracle", [1522] = "oracle",
        [1583] = "pervasive", [3351] = "pervasive", [31438] = "universe", [31439] = "universe", [27017] = "mongodb", [50000] = "db2", [1972] = "cache",
        [5000] = "rest", [8080] = "rest", [8443] = "rest", [443] = "rest", [80] = "rest", [3050] = "firebird", [2638] = "sqlanywhere", [9092] = "h2", [6379] = "redis",
    };

    public static List<BackendInfo> Detect(string? exePath, string processName, int? pid, string? displayName = null)
    {
        var list = new List<BackendInfo>();
        try { list.AddRange(FromConfigFiles(exePath, processName, displayName)); } catch (Exception ex) { Log.Warn("backend config scan failed: " + ex.Message); }
        try { list.AddRange(FromOdbc()); } catch (Exception ex) { Log.Warn("backend ODBC scan failed: " + ex.Message); }
        if (pid is not null) { try { list.AddRange(FromTcp(pid.Value)); } catch (Exception ex) { Log.Warn("backend TCP scan failed: " + ex.Message); } }
        return Dedupe(list);
    }

    private static List<BackendInfo> Dedupe(List<BackendInfo> list)
    {
        var result = new List<BackendInfo>();
        foreach (var b in list.OrderBy(b => b.Kind == "unknown").ThenBy(b => b.Where))
        {
            var dup = result.FirstOrDefault(x => x.Kind == b.Kind && string.Equals(x.Host, b.Host, StringComparison.OrdinalIgnoreCase) && x.Port == b.Port && string.Equals(x.Database, b.Database, StringComparison.OrdinalIgnoreCase));
            if (dup is null) result.Add(b);
            else if (!dup.Where.Contains(b.Where)) dup.Where += "; " + b.Where;
        }
        return result;
    }

    /// <summary>Config files next to the exe and in the usual data folders that mention the program.</summary>
    public static List<BackendInfo> FromConfigFiles(string? exePath, string processName, string? displayName)
    {
        var found = new List<BackendInfo>();
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) && !DataSourceScanner.IsSharedFolder(Path.GetDirectoryName(exePath)!)) dirs.Add(Path.GetDirectoryName(exePath)!);
        var names = new[] { processName, displayName ?? "" }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.ToLowerInvariant()).Distinct().ToList();
        foreach (var sf in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var root = Environment.GetFolderPath(sf);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root))
                {
                    var n = Path.GetFileName(d).ToLowerInvariant();
                    if (names.Any(x => n.Contains(x) || x.Contains(n) && n.Length > 3)) dirs.Add(d);
                }
            }
            catch { }
        }
        var budget = 400;
        foreach (var dir in dirs.Distinct())
            foreach (var f in Walk(dir, 3))
            {
                if (--budget <= 0) return found;
                if (!ConfigExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 2 * 1024 * 1024) continue;
                    var text = File.ReadAllText(f);
                    found.AddRange(ParseText(text, f));
                }
                catch { }
            }
        return found;
    }

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { yield break; }
        foreach (var e in entries)
        {
            if (Directory.Exists(e)) { if (depth > 0) foreach (var x in Walk(e, depth - 1)) yield return x; }
            else yield return e;
        }
    }

    /// <summary>Connection strings and URLs in any text; secrets are stripped, only kind/host/port/database are kept.</summary>
    public static List<BackendInfo> ParseText(string text, string where)
    {
        var list = new List<BackendInfo>();
        if (string.IsNullOrEmpty(text)) return list;
        var safe = Secret().Replace(text, "$1=***");
        // connection strings: a line or attribute value with Data Source / Server / Provider / Driver / DSN
        foreach (Match m in Regex.Matches(safe, @"(?i)(?:connectionString|value|url|dsn|ConnectString|DataSource)\s*=\s*[""']([^""']{6,500})[""']"))
            Add(list, m.Groups[1].Value, where);
        foreach (var line in safe.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length < 8 || l.Length > 600) continue;
            if (Regex.IsMatch(l, @"(?i)\b(data source|server|provider|driver|dsn|initial catalog|jdbc:)\b") && l.Contains('=')) Add(list, l, where);
            else if (Regex.IsMatch(l, @"(?i)^(host|hostname|server|dbserver|db_host|port|dbport|database|dbname|db_name)\s*[=:]")) Add(list, l, where);
            else if (Regex.IsMatch(l, @"(?i)https?://[\w.-]+(:\d+)?/")) { var u = Regex.Match(l, @"(?i)https?://(?<h>[\w.-]+)(:(?<p>\d+))?/"); list.Add(new BackendInfo { Kind = "rest", Host = u.Groups["h"].Value, Port = u.Groups["p"].Success ? int.Parse(u.Groups["p"].Value) : null, Where = Path.GetFileName(where) }); }
        }
        return list;
    }

    private static void Add(List<BackendInfo> list, string cs, string where)
    {
        var b = Classify(cs);
        if (b is null) return;
        b.Where = Path.GetFileName(where);
        list.Add(b);
    }

    /// <summary>Classifies one connection string into kind, host, port, database (no credentials).</summary>
    public static BackendInfo? Classify(string cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var s = Secret().Replace(cs, "$1=***");
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in s.Split(';'))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            kv[part[..i].Trim().ToLowerInvariant()] = part[(i + 1)..].Trim().Trim('"', '\'');
        }
        var lower = s.ToLowerInvariant();
        var b = new BackendInfo();
        string? Get(params string[] keys) { foreach (var k in keys) if (kv.TryGetValue(k, out var v) && v.Length > 0) return v; return null; }
        var provider = Get("provider") ?? "";
        var driver = Get("driver")?.Trim('{', '}') ?? "";
        var source = Get("data source", "server", "host", "hostname", "addr", "address", "network address", "dbserver", "db_host") ?? "";
        var database = Get("database", "initial catalog", "dbname", "db_name", "dbq", "catalog");
        var port = Get("port", "dbport");
        if (lower.Contains("jdbc:"))
        {
            var m = Regex.Match(s, @"(?i)jdbc:(?<k>\w+):(?://)?(?<h>[\w.-]+)?(?::(?<p>\d+))?(?:[/;:](?<d>[\w.-]+))?");
            if (m.Success) { b.Kind = m.Groups["k"].Value.ToLowerInvariant(); b.Host = m.Groups["h"].Success ? m.Groups["h"].Value : null; if (m.Groups["p"].Success) b.Port = int.Parse(m.Groups["p"].Value); b.Database = m.Groups["d"].Success ? m.Groups["d"].Value : null; }
            if (b.Kind == "sqlserver" || b.Kind == "jtds") b.Kind = "sqlserver";
            return b;
        }
        if (provider.Length > 0 || driver.Length > 0 || source.Length > 0 || database is not null)
        {
            var hint = (provider + " " + driver).ToLowerInvariant();
            b.Driver = driver.Length > 0 ? driver : provider.Length > 0 ? provider : null;
            if (hint.Contains("sqlncli") || hint.Contains("sqloledb") || hint.Contains("msoledbsql") || hint.Contains("sql server") || hint.Contains("sqlclient")) b.Kind = "sqlserver";
            else if (hint.Contains("ace.oledb") || hint.Contains("jet.oledb") || hint.Contains("access")) b.Kind = "access";
            else if (hint.Contains("sqlite")) b.Kind = "sqlite";
            else if (hint.Contains("mysql") || hint.Contains("mariadb")) b.Kind = "mysql";
            else if (hint.Contains("postgres") || hint.Contains("psql")) b.Kind = "postgresql";
            else if (hint.Contains("oracle") || hint.Contains("oraoledb")) b.Kind = "oracle";
            else if (hint.Contains("pervasive") || hint.Contains("actian") || hint.Contains("btrieve") || hint.Contains("zen")) b.Kind = "pervasive";
            else if (hint.Contains("universe") || hint.Contains("unidata") || hint.Contains("uvodbc") || hint.Contains("u2 ")) b.Kind = "universe";
            else if (hint.Contains("firebird") || hint.Contains("interbase")) b.Kind = "firebird";
            else if (hint.Contains("db2")) b.Kind = "db2";
            else if (hint.Contains("progress") || hint.Contains("openedge")) b.Kind = "progress";
            else if (hint.Contains("anywhere") || hint.Contains("sybase")) b.Kind = "sqlanywhere";
            else if (kv.ContainsKey("dsn")) b.Kind = "odbc";
            else if (source.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase)) b.Kind = "access";
            else if (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) b.Kind = "sqlite";
            else if (kv.ContainsKey("initial catalog") || kv.ContainsKey("integrated security") || kv.ContainsKey("trusted_connection") || kv.ContainsKey("trustservercertificate") || kv.ContainsKey("user id") || kv.ContainsKey("multipleactiveresultsets") || kv.ContainsKey("encrypt")) b.Kind = "sqlserver";
            else if (source.Length == 0 && database is null) return null;
            if (source.Length > 0)
            {
                var hp = source.Split(',', ':');
                if (b.Kind is "sqlite" or "access") b.Database = Path.GetFileName(source);
                else { b.Host = hp[0]; if (hp.Length > 1 && int.TryParse(hp[1], out var p)) b.Port = p; }
            }
            if (port is not null && int.TryParse(port, out var pp)) b.Port = pp;
            if (b.Kind == "unknown" && b.Port is not null && KnownPorts.TryGetValue(b.Port.Value, out var byPort)) b.Kind = byPort;
            if (database is not null && b.Database is null) b.Database = Path.GetFileName(database);
            if (kv.TryGetValue("dsn", out var dsn)) b.Database ??= "DSN " + dsn;
            return b;
        }
        // "host = x" style lines
        var hm = Regex.Match(s, @"(?i)^(host|hostname|server|dbserver|db_host)\s*[=:]\s*(?<v>[\w.-]+)");
        if (hm.Success) { b.Kind = "unknown"; b.Host = hm.Groups["v"].Value; return b; }
        var pm = Regex.Match(s, @"(?i)^(port|dbport)\s*[=:]\s*(?<v>\d+)");
        if (pm.Success) { var p = int.Parse(pm.Groups["v"].Value); b.Port = p; b.Kind = KnownPorts.GetValueOrDefault(p, "unknown"); return b; }
        var dm = Regex.Match(s, @"(?i)^(database|dbname|db_name)\s*[=:]\s*(?<v>[\w.-]+)");
        if (dm.Success) { b.Kind = "unknown"; b.Database = dm.Groups["v"].Value; return b; }
        return null;
    }

    /// <summary>User and machine ODBC data sources: name and driver only.</summary>
    public static List<BackendInfo> FromOdbc()
    {
        var list = new List<BackendInfo>();
        foreach (var (root, label) in new[] { (Registry.CurrentUser, "user ODBC DSN"), (Registry.LocalMachine, "system ODBC DSN") })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\ODBC\ODBC.INI\ODBC Data Sources");
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var driver = key.GetValue(name)?.ToString() ?? "";
                    var b = Classify($"DSN={name};Driver={{{driver}}}") ?? new BackendInfo { Kind = "odbc" };
                    b.Kind = b.Kind == "unknown" ? "odbc" : b.Kind;
                    b.Driver = driver; b.Database = "DSN " + name; b.Where = label;
                    try
                    {
                        using var dsnKey = root.OpenSubKey(@"Software\ODBC\ODBC.INI\" + name);
                        var server = dsnKey?.GetValue("Server")?.ToString() ?? dsnKey?.GetValue("Host")?.ToString() ?? dsnKey?.GetValue("ServerName")?.ToString();
                        if (!string.IsNullOrEmpty(server)) b.Host = server;
                        var port = dsnKey?.GetValue("Port")?.ToString();
                        if (int.TryParse(port, out var p)) b.Port = p;
                        var db = dsnKey?.GetValue("Database")?.ToString() ?? dsnKey?.GetValue("DBQ")?.ToString();
                        if (!string.IsNullOrEmpty(db)) b.Database = $"DSN {name} ({Path.GetFileName(db)})";
                    }
                    catch { }
                    list.Add(b);
                }
            }
            catch { }
        }
        return list;
    }

    // ---- TCP connections of one process (iphlpapi), host and port only ----

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid { public uint State; public uint LocalAddr; public uint LocalPort; public uint RemoteAddr; public uint RemotePort; public uint OwningPid; }

    public static List<BackendInfo> FromTcp(int pid)
    {
        var list = new List<BackendInfo>();
        if (!OperatingSystem.IsWindows()) return list;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 5 /* TCP_TABLE_OWNER_PID_ALL */, 0);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0) return list;
            var count = Marshal.ReadInt32(buf);
            var rowPtr = buf + 4;
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(rowPtr + i * rowSize);
                if (row.OwningPid != (uint)pid || row.State != 5 /* ESTABLISHED */) continue;
                var port = (int)(((row.RemotePort & 0xFF) << 8) | ((row.RemotePort >> 8) & 0xFF));
                var addr = new System.Net.IPAddress(row.RemoteAddr).ToString();
                if (addr.StartsWith("127.") && port < 1024) continue;
                list.Add(new BackendInfo { Kind = KnownPorts.GetValueOrDefault(port, "unknown"), Host = addr, Port = port, Where = "tcp connection" });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}
