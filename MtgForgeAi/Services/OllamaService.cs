using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgForgeAi.Telemetry;

namespace MtgForgeAi.Services;

/// <summary>
/// LLM provider backed by a locally running Ollama instance.
/// Ollama exposes a custom /api/chat endpoint (not OpenAI-compatible).
/// On Apple Silicon, Ollama uses Metal GPU acceleration natively.
/// </summary>
public class OllamaLlmService : ILlmService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OllamaLlmService> _logger;

    public OllamaLlmService(HttpClient http, IConfiguration config, ILogger<OllamaLlmService> logger)
    {
        _http = http;
        _model = config["Ollama:Model"] ?? "llama3.1:8b";
        _logger = logger;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromMinutes(10); // CPU inference can be slow, especially on first load
    }

    /// <summary>
    /// Send a chat completion request to Ollama and return the response with token usage.
    /// </summary>
    public async Task<ChatResult> ChatAsync(
        string systemPrompt,
        string userMessage,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        using var activity = AppTelemetry.Activities.StartActivity("gen_ai.chat");
        activity?.SetTag(AppTelemetry.GenAiSystem, AppTelemetry.SystemOllama);
        activity?.SetTag(AppTelemetry.GenAiOperationName, "chat");
        activity?.SetTag(AppTelemetry.GenAiRequestModel, _model);
        activity?.SetTag(AppTelemetry.GenAiRequestMaxTokens, 4096);
        activity?.SetTag(AppTelemetry.GenAiRequestTemperature, 0.4);

        var sw = Stopwatch.StartNew();
        var status = "success";
        try
        {
            var payload = new OllamaChatRequest
            {
                Model = _model,
                Stream = false,
                Format = jsonMode ? "json" : null,
                Messages =
                [
                    new OllamaMessage { Role = "system", Content = systemPrompt },
                    new OllamaMessage { Role = "user",   Content = userMessage  }
                ],
                Options = new OllamaOptions
                {
                    Temperature = 0.4,
                    NumCtx = 8192,      // Deck prompts are large; need room for candidates + JSON output
                    NumPredict = 4096   // Full deck JSON can be lengthy
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

            if (result.PromptEvalCount > 0)
                activity?.SetTag(AppTelemetry.GenAiUsageInputTokens, result.PromptEvalCount);
            if (result.EvalCount > 0)
                activity?.SetTag(AppTelemetry.GenAiUsageOutputTokens, result.EvalCount);

            return new ChatResult(result.Message?.Content ?? "", result.PromptEvalCount, result.EvalCount);
        }
        catch
        {
            status = "error";
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            AppTelemetry.LlmRequests.Add(1, new TagList { { "model", _model }, { "status", status } });
            AppTelemetry.LlmLatency.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "model", _model } });
            _logger.LogInformation(
                "metric: mtg.llm.requests +1 | mtg.llm.request.duration {DurationMs:F0}ms | model={Model} status={Status}",
                sw.Elapsed.TotalMilliseconds, _model, status);
        }
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

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
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
        public string? Format { get; set; }
        public OllamaOptions? Options { get; set; }
    }

    private class OllamaMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private class OllamaOptions
    {
        public double Temperature { get; set; } = 0.4;
        public int NumCtx { get; set; } = 4096;
        public int NumPredict { get; set; } = 2048;
    }

    private class OllamaChatResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
        public int PromptEvalCount { get; set; }
        public int EvalCount { get; set; }
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
