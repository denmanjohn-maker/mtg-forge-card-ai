namespace MtgForgeAi.Services;

/// <summary>
/// Abstraction over embedding providers (Ollama, OpenAI-compatible, etc.).
/// Implementations must produce vectors that are compatible with the Qdrant
/// collection's vector size — if you switch providers and models you must
/// delete the Qdrant collection and re-run ingestion.
/// </summary>
public interface IEmbedService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
