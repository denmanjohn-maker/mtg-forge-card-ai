using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MtgForgeAi.Telemetry;

internal static class AppTelemetry
{
    internal const string ServiceName = "mtg-forge-ai";

    internal static readonly string ServiceVersion =
        typeof(AppTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    internal static readonly ActivitySource Activities = new(ServiceName);
    internal static readonly Meter Meter = new(ServiceName);

    // ─── GenAI semantic convention attributes ─────────────────────────────────
    // https://opentelemetry.io/docs/specs/semconv/gen-ai/

    internal const string GenAiSystem             = "gen_ai.system";
    internal const string GenAiOperationName      = "gen_ai.operation.name";
    internal const string GenAiRequestModel       = "gen_ai.request.model";
    internal const string GenAiRequestMaxTokens   = "gen_ai.request.max_tokens";
    internal const string GenAiRequestTemperature = "gen_ai.request.temperature";
    internal const string GenAiUsageInputTokens   = "gen_ai.usage.input_tokens";
    internal const string GenAiUsageOutputTokens  = "gen_ai.usage.output_tokens";

    // Well-known gen_ai.system values
    internal const string SystemTogetherAi = "together_ai";
    internal const string SystemOllama     = "ollama";

    // ─── Counters ─────────────────────────────────────────────────────────────

    internal static readonly Counter<long> DeckGenerations =
        Meter.CreateCounter<long>("mtg.deck.generations",
            description: "Total deck generation requests");

    internal static readonly Counter<long> SearchRequests =
        Meter.CreateCounter<long>("mtg.card.searches",
            description: "Total card search requests");

    internal static readonly Counter<long> LlmRequests =
        Meter.CreateCounter<long>("mtg.llm.requests",
            description: "Total LLM API calls");

    // ─── Histograms ───────────────────────────────────────────────────────────

    internal static readonly Histogram<double> DeckDuration =
        Meter.CreateHistogram<double>("mtg.deck.generation.duration", unit: "ms",
            description: "Deck generation latency in milliseconds");

    internal static readonly Histogram<double> SearchDuration =
        Meter.CreateHistogram<double>("mtg.card.search.duration", unit: "ms",
            description: "Card search latency in milliseconds");

    internal static readonly Histogram<double> LlmLatency =
        Meter.CreateHistogram<double>("mtg.llm.request.duration", unit: "ms",
            description: "LLM request latency in milliseconds");

    internal static readonly Histogram<double> EmbeddingLatency =
        Meter.CreateHistogram<double>("mtg.embed.request.duration", unit: "ms",
            description: "Embedding request latency in milliseconds");

    // ─── Ingestion ────────────────────────────────────────────────────────────

    internal static readonly Counter<long> CardsDownloaded =
        Meter.CreateCounter<long>("mtg.ingestion.cards_downloaded", unit: "cards",
            description: "Cards downloaded from Scryfall");

    internal static readonly Counter<long> CardsUpsertedMongo =
        Meter.CreateCounter<long>("mtg.ingestion.cards_upserted_mongo", unit: "cards",
            description: "Cards upserted into MongoDB");

    internal static readonly Counter<long> VectorsUpsertedQdrant =
        Meter.CreateCounter<long>("mtg.ingestion.vectors_upserted", unit: "vectors",
            description: "Vectors upserted into Qdrant");

    internal static readonly Histogram<double> IngestionDuration =
        Meter.CreateHistogram<double>("mtg.ingestion.duration", unit: "s",
            description: "Total ingestion pipeline duration in seconds");
}
