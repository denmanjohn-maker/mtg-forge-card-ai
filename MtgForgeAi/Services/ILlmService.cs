namespace MtgForgeAi.Services;

/// <summary>
/// Abstraction over LLM providers (Ollama, Together.ai, OpenAI-compatible APIs).
/// Embeddings are handled separately by OllamaEmbedService.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Send a chat completion request and return the full response text.
    /// </summary>
    Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Stream tokens as they arrive from the LLM.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Check that the LLM provider is reachable and the model is available.
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}
