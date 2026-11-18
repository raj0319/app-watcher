using System.Text;
using System.Text.Json;

namespace AppWatcher.Core.Analysis;

/// <summary>
/// Runs the same analyst prompt against an OpenAI-compatible endpoint on the client's own machine
/// (Ollama, LM Studio, vLLM...). Off by default. Sends only memory/*.md, never raw events.
/// </summary>
public sealed class LocalLlmClient
{
    private readonly LocalAnalysisSettings _settings;

    public LocalLlmClient(LocalAnalysisSettings settings) => _settings = settings;

    public async Task<string> AnalyzeAsync(string memoryDir, CancellationToken ct = default)
    {
        var user = AnalystPrompt.BuildUserMessage(memoryDir);
        var body = new
        {
            model = _settings.Model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = "You are a senior software architect reverse-engineering a business application from observed evidence. Answer in Markdown." },
                new { role = "user", content = user },
            },
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var key = Dpapi.Unprotect(_settings.ApiKeyProtected);
        if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{url} returned {(int)resp.StatusCode}: {(json.Length > 300 ? json[..300] : json)}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async Task<string> AnalyzeAndSaveAsync(string memoryDir, string fileName = "architecture-reviewed.md", CancellationToken ct = default)
    {
        var text = await AnalyzeAsync(memoryDir, ct);
        var header = $"# Architecture reviewed by a local model ({_settings.Model} at {_settings.Endpoint}) on {DateTime.Now:yyyy-MM-dd HH:mm}\n\n";
        File.WriteAllText(Path.Combine(memoryDir, fileName), header + text.Trim() + "\n");
        return $"ok, {text.Length} characters written to {fileName}";
    }
}
