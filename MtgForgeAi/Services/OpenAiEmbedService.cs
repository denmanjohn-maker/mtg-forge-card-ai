using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MtgForgeAi.Telemetry;

namespace MtgForgeAi.Services;

/// <summary>
/// Generates text embeddings using any OpenAI-compatible /v1/embeddings endpoint,
/// including Together.ai.
///
/// IMPORTANT: The embedding model must match between ingestion and search.
/// If you change the embed model (LLM:EmbedModel), you must delete the Qdrant
/// collection and re-run ingestion so that stored and query vectors have the
/// same dimensions.
///
/// Current default:
///   - LLM:EmbedModel: BAAI/bge-base-en-v1.5 (768-dim)
/// </summary>
public class OpenAiEmbedService : IEmbedService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OpenAiEmbedService> _logger;

    public OpenAiEmbedService(
        HttpClient http,
        IConfiguration config,
        ILogger<OpenAiEmbedService> logger)
    {
        _http = http;
        _model = config["LLM:EmbedModel"] ?? "BAAI/bge-base-en-v1.5";
        _logger = logger;

        var baseUrl = config["LLM:BaseUrl"] ?? "https://api.together.xyz";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        var apiKey = config["LLM:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM:ApiKey is required for OpenAiEmbedService.");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var timeoutSeconds = int.TryParse(config["LLM:EmbedTimeoutSeconds"], out var t) && t > 0 ? t : 30;
        _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        using var activity = AppTelemetry.Activities.StartActivity("openai.embed");
        activity?.SetTag("model", _model);

        var sw = Stopwatch.StartNew();
        try
        {
            var payload = new { model = _model, input = text };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("v1/embeddings", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Embed API error {(int)response.StatusCode}: {err}");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenAiEmbedResponse>(ct)
                ?? throw new InvalidOperationException("Null embed response");

            var vector = result.Data?[0]?.Embedding
                ?? throw new InvalidOperationException("No embedding in response");

            activity?.SetTag("vector.dimensions", vector.Length);
            return vector;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            AppTelemetry.EmbeddingLatency.Record(sw.Elapsed.TotalMilliseconds,
                new TagList { { "model", _model } });
            _logger.LogDebug(
                "metric: mtg.embed.request.duration {DurationMs:F1}ms | model={Model}",
                sw.Elapsed.TotalMilliseconds, _model);
        }
    }

    private class OpenAiEmbedResponse
    {
        public List<EmbeddingData>? Data { get; set; }
    }

    private class EmbeddingData
    {
        public float[]? Embedding { get; set; }
    }
}
