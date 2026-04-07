using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MtgForgeLocal.Services;

/// <summary>
/// Generates text embeddings using Ollama's local embedding endpoint.
/// Uses nomic-embed-text which produces 768-dim vectors and runs fast on M2.
///
/// IMPORTANT: The ingestion pipeline (Python) uses all-MiniLM-L6-v2 (384-dim).
/// If you change the embed model here you must re-run the ingestion script.
/// Both models must produce the same vector dimensions.
///
/// To use nomic-embed-text with the Python script, update EMBED_MODEL in
/// ingest_cards.py to match and re-run ingestion.
///
/// Easiest approach: keep Python using all-MiniLM-L6-v2 and use
/// Ollama's version of the same model: "ollama pull all-minilm"
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
        _http.Timeout = TimeSpan.FromSeconds(30);
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
