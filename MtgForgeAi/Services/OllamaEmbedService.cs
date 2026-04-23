using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MtgForgeAi.Services;

/// <summary>
/// Generates text embeddings using Ollama's local embedding endpoint.
/// Uses all-minilm which produces 384-dim vectors (matching the Python
/// ingestion script's all-MiniLM-L6-v2).
///
/// IMPORTANT: The embedding model must match between ingestion and search.
/// If you change the embed model, you must delete the Qdrant collection and
/// re-run ingestion. Both the ingestion pipeline and this service must
/// produce the same vector dimensions.
///
/// Current alignment:
///   - Python ingestion: all-MiniLM-L6-v2 (384-dim)
///   - Admin API ingestion: Ollama all-minilm (384-dim)
///   - This service (search): Ollama all-minilm (384-dim)
/// </summary>
public class OllamaEmbedService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OllamaEmbedService> _logger;

    public OllamaEmbedService(
        HttpClient http,
        IConfiguration config,
        ILogger<OllamaEmbedService> logger)
    {
        _http = http;
        _model = config["Ollama:EmbedModel"] ?? "all-minilm";
        _logger = logger;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(baseUrl);

        var timeoutSeconds = int.TryParse(config["Ollama:EmbedTimeoutSeconds"], out var t) && t > 0 ? t : 120;
        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = new { model = _model, input = text };
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/api/embed", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Ollama embed error: {err}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct)
            ?? throw new InvalidOperationException("Null embed response");

        return result.Embeddings?[0]
            ?? throw new InvalidOperationException("No embeddings in response");
    }

    private class OllamaEmbedResponse
    {
        public float[][]? Embeddings { get; set; }
    }
}
