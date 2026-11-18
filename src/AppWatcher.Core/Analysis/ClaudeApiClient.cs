using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AppWatcher.Core.Learning;

namespace AppWatcher.Core.Analysis;

/// <summary>The analyst prompt shared by the /analyze command and the optional nightly API call.</summary>
public static class AnalystPrompt
{
    public static string Text
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("analyst-prompt.md");
            if (s is null) return "You are a software architect. Read the memory files and write architecture-reviewed.md.";
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
    }

    /// <summary>Exactly the user message that goes to the API: the prompt plus the memory/*.md files, nothing else.</summary>
    public static string BuildUserMessage(string memoryDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Text.Trim());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("The memory files follow. They are the only input; no raw events or values are included.");
        sb.AppendLine();
        foreach (var name in MemoryWriter.Files.Concat(new[] { "changelog.md" }))
        {
            var p = Path.Combine(memoryDir, name);
            if (!File.Exists(p)) continue;
            sb.AppendLine($"<file name=\"memory/{name}\">");
            sb.AppendLine(File.ReadAllText(p).Trim());
            sb.AppendLine("</file>");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>Minimal Claude Messages API client. Only used when the client turns the nightly analysis on.</summary>
public sealed class ClaudeApiClient
{
    private readonly ApiSettings _settings;
    private readonly string _apiKey;

    public ClaudeApiClient(ApiSettings settings, string apiKey)
    {
        _settings = settings;
        _apiKey = apiKey;
    }

    public async Task<string> AnalyzeAsync(string memoryDir, CancellationToken ct = default)
    {
        var user = AnalystPrompt.BuildUserMessage(memoryDir);
        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            system = "You are a senior software architect reverse-engineering a business application from observed evidence. Answer in Markdown.",
            messages = new[] { new { role = "user", content = user } },
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AppWatcher", "0.1"));
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(_settings.Endpoint, content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"API returned {(int)resp.StatusCode}: {Truncate(json)}");
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text") sb.Append(block.GetProperty("text").GetString());
        return sb.ToString();
    }

    /// <summary>Runs the analysis and writes memory/architecture-reviewed.md. Returns a one-line status.</summary>
    public async Task<string> AnalyzeAndSaveAsync(string memoryDir, CancellationToken ct = default)
    {
        var text = await AnalyzeAsync(memoryDir, ct);
        var header = $"# Architecture reviewed by Claude API ({_settings.Model}) on {DateTime.Now:yyyy-MM-dd HH:mm}\n\n";
        File.WriteAllText(Path.Combine(memoryDir, "architecture-reviewed.md"), header + text.Trim() + "\n");
        return $"ok, {text.Length} characters written";
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
