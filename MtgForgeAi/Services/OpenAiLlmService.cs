using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgForgeAi.Services;

/// <summary>
/// LLM provider for Together.ai and any OpenAI-compatible chat completions API.
/// Uses the standard /v1/chat/completions endpoint with Bearer token auth.
/// </summary>
public class OpenAiLlmService : ILlmService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OpenAiLlmService> _logger;

    public OpenAiLlmService(HttpClient http, IConfiguration config, ILogger<OpenAiLlmService> logger)
    {
        _http = http;
        _model = config["LLM:Model"] ?? "meta-llama/Llama-3.3-70B-Instruct-Turbo";
        _logger = logger;

        var baseUrl = config["LLM:BaseUrl"] ?? "https://api.together.xyz";
        var apiKey = config["LLM:ApiKey"]
            ?? throw new InvalidOperationException("LLM:ApiKey is required for OpenAI-compatible providers");

        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<string> ChatAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default)
    {
        var payload = new ChatCompletionRequest
        {
            Model = _model,
            Stream = false,
            MaxTokens = 4096,
            Temperature = 0.7,
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user",   Content = userMessage  }
            ]
        };

        _logger.LogInformation("Sending request to OpenAI-compatible API (model={Model})", _model);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/v1/chat/completions", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"LLM API error {response.StatusCode}: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Null response from LLM API");

        return result.Choices?.FirstOrDefault()?.Message?.Content ?? "";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new ChatCompletionRequest
        {
            Model = _model,
            Stream = true,
            MaxTokens = 4096,
            Temperature = 0.7,
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user",   Content = userMessage  }
            ]
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data, JsonOptions);
            var token = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // Together.ai and most OpenAI-compatible APIs support GET /v1/models
            var response = await _http.GetAsync("/v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM API health check failed");
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ─── OpenAI-compatible DTOs ──────────────────────────────────────────────

    private class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatMessage> Messages { get; set; } = [];
        public bool Stream { get; set; }
        public int MaxTokens { get; set; } = 4096;
        public double Temperature { get; set; } = 0.7;
    }

    private class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private class ChatCompletionResponse
    {
        public List<ChatChoice>? Choices { get; set; }
    }

    private class ChatChoice
    {
        public ChatMessage? Message { get; set; }
    }

    private class ChatCompletionChunk
    {
        public List<ChatChunkChoice>? Choices { get; set; }
    }

    private class ChatChunkChoice
    {
        public ChatDelta? Delta { get; set; }
    }

    private class ChatDelta
    {
        public string? Content { get; set; }
    }
}
