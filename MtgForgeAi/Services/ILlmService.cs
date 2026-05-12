namespace MtgForgeAi.Services;

/// <summary>Token usage returned alongside the LLM response text.</summary>
public record ChatResult(string Content, int InputTokens = 0, int OutputTokens = 0);

/// <summary>
/// Abstraction over LLM providers (Ollama, DeepInfra, OpenAI-compatible APIs).
/// Embeddings are handled separately by OllamaEmbedService.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Send a chat completion request and return the response with token usage.
    /// Set jsonMode to true to enable provider-level JSON output enforcement.
    /// </summary>
    Task<ChatResult> ChatAsync(string systemPrompt, string userMessage, bool jsonMode = false, CancellationToken ct = default);

    /// <summary>
    /// Stream tokens as they arrive from the LLM.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Check that the LLM provider is reachable and the model is available.
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}
