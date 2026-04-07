using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgForgeLocal.Services;

/// <summary>
/// Talks to a locally running Ollama instance (http://localhost:11434).
/// Ollama exposes an OpenAI-compatible /api/chat endpoint.
/// On Apple Silicon, Ollama uses Metal GPU acceleration natively.
/// </summary>
public class OllamaService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(HttpClient http, IConfiguration config, ILogger<OllamaService> logger)
    {
        _http = http;
        _model = config["Ollama:Model"] ?? "llama3.1:8b";
        _logger = logger;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromMinutes(5); // LLM can be slow on first token
    }

    /// <summary>
    /// Send a chat completion request to Ollama and return the full response text.
    /// </summary>
    public async Task<string> ChatAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default)
    {
        var payload = new OllamaChatRequest
        {
            Model = _model,
            Stream = false,
            Messages =
            [
                new OllamaMessage { Role = "system", Content = systemPrompt },
                new OllamaMessage { Role = "user",   Content = userMessage  }
            ],
            Options = new OllamaOptions
            {
                Temperature = 0.7,
                NumCtx = 4096,      // Context window — keep reasonable for 16GB RAM
                NumPredict = 2048   // Max output tokens
            }
        };

        _logger.LogInformation("Sending request to Ollama (model={Model})", _model);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/api/chat", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ollama error {response.StatusCode}: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Null response from Ollama");

        return result.Message?.Content ?? "";
    }

    /// <summary>
    /// Stream tokens from Ollama and yield them as they arrive.
    /// Useful for real-time UI updates.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new OllamaChatRequest
        {
            Model = _model,
            Stream = true,
            Messages =
            [
                new OllamaMessage { Role = "system", Content = systemPrompt },
                new OllamaMessage { Role = "user",   Content = userMessage  }
            ]
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
            if (chunk?.Message?.Content is { } token)
                yield return token;

            if (chunk?.Done == true) break;
        }
    }

    /// <summary>
    /// Check that Ollama is reachable and the required model is available.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOptions, ct);
            var available = tags?.Models?.Any(m => m.Name.StartsWith(_model.Split(':')[0])) ?? false;

            if (!available)
                _logger.LogWarning(
                    "Model '{Model}' not found in Ollama. Run: ollama pull {Model}", _model, _model);

            return available;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama health check failed — is Ollama running?");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── Internal DTOs ────────────────────────────────────────────────────────

    private class OllamaChatRequest
    {
        public string Model { get; set; } = "";
        public List<OllamaMessage> Messages { get; set; } = new();
        public bool Stream { get; set; }
        public OllamaOptions? Options { get; set; }
    }

    private class OllamaMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private class OllamaOptions
    {
        public double Temperature { get; set; } = 0.7;
        public int NumCtx { get; set; } = 4096;
        public int NumPredict { get; set; } = 2048;
    }

    private class OllamaChatResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private class OllamaTagsResponse
    {
        public List<OllamaModelInfo>? Models { get; set; }
    }

    private class OllamaModelInfo
    {
        public string Name { get; set; } = "";
    }
}
