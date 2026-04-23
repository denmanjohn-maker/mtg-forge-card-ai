using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MtgForgeAi.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MtgForgeAi.Services;

/// <summary>
/// Handles card data ingestion from Scryfall into MongoDB and Qdrant.
/// Replaces the need for the Python ingestion script at runtime.
///
/// Pipeline:
///   1. Fetch Scryfall bulk-data index → get oracle_cards download URI
///   2. Stream-download and deserialize all oracle cards
///   3. Upsert cards into MongoDB (via MongoService)
///   4. Embed card text via Ollama and upsert vectors into Qdrant
/// </summary>
public class CardIngestionService
{
    private readonly MongoService _mongo;
    private readonly QdrantClient _qdrant;
    private readonly OllamaEmbedService _embedder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CardIngestionService> _logger;

    private const string QdrantCollection = "mtg_cards";
    private const int BatchSize = 50; // Smaller batch for embedding via Ollama (slower than local model)

    private static readonly string[] SupportedFormats =
        ["commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage"];

    public CardIngestionService(
        MongoService mongo,
        QdrantClient qdrant,
        OllamaEmbedService embedder,
        IHttpClientFactory httpClientFactory,
        ILogger<CardIngestionService> logger)
    {
        _mongo = mongo;
        _qdrant = qdrant;
        _embedder = embedder;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Run the full ingestion pipeline. Returns a summary of what was processed.
    /// </summary>
    public async Task<IngestionResult> IngestAsync(
        bool mongoOnly = false,
        bool qdrantOnly = false,
        int? limit = null,
        CancellationToken ct = default)
    {
        var result = new IngestionResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Download cards from Scryfall
        _logger.LogInformation("Fetching Scryfall oracle cards...");
        var cards = await DownloadScryfallCardsAsync(ct);
        result.TotalCardsDownloaded = cards.Count;
        _logger.LogInformation("Downloaded {Count} cards from Scryfall", cards.Count);

        if (limit.HasValue && limit.Value > 0)
        {
            cards = cards.Take(limit.Value).ToList();
            _logger.LogInformation("Limited to {Count} cards for testing", cards.Count);
        }

        // Step 2: MongoDB ingestion
        if (!qdrantOnly)
        {
            _logger.LogInformation("Upserting {Count} cards into MongoDB...", cards.Count);
            result.MongoCardsUpserted = await _mongo.BulkUpsertCardsAsync(cards, ct);
            _logger.LogInformation("MongoDB: {Count} cards upserted", result.MongoCardsUpserted);
        }

        // Step 3: Qdrant ingestion (embed + upsert)
        if (!mongoOnly)
        {
            // Filter to cards legal in at least one supported format
            var legalCards = cards.Where(c =>
            {
                var legalities = c.Legalities;
                return SupportedFormats.Any(fmt =>
                    legalities.TryGetValue(fmt, out var status) &&
                    status.Equals("legal", StringComparison.OrdinalIgnoreCase));
            }).ToList();

            _logger.LogInformation("Embedding {Count} format-legal cards into Qdrant...", legalCards.Count);
            result.QdrantVectorsUpserted = await EmbedAndUpsertToQdrantAsync(legalCards, ct);
            _logger.LogInformation("Qdrant: {Count} vectors upserted", result.QdrantVectorsUpserted);
        }

        sw.Stop();
        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    // ─── Scryfall Download ────────────────────────────────────────────────────

    private async Task<List<MtgCard>> DownloadScryfallCardsAsync(CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("Scryfall");

        // Get bulk data index
        var bulkDataResponse = await http.GetAsync("https://api.scryfall.com/bulk-data", ct);
        if (!bulkDataResponse.IsSuccessStatusCode)
        {
            var errorBody = await bulkDataResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Scryfall bulk-data returned {Status}: {Body}",
                (int)bulkDataResponse.StatusCode, errorBody);
            bulkDataResponse.EnsureSuccessStatusCode();
        }

        var bulkResponse = await bulkDataResponse.Content.ReadFromJsonAsync<ScryfallBulkDataResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize Scryfall bulk data index");

        var oracleEntry = bulkResponse.Data?.FirstOrDefault(e =>
            e.Type.Equals("oracle_cards", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Could not find oracle_cards in Scryfall bulk data");

        _logger.LogInformation("Downloading from {Uri}...", oracleEntry.DownloadUri);

        // Download and deserialize all cards
        var downloadResponse = await http.GetAsync(oracleEntry.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            var errorBody = await downloadResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Scryfall download returned {Status}: {Body}",
                (int)downloadResponse.StatusCode, errorBody);
            downloadResponse.EnsureSuccessStatusCode();
        }

        var stream = await downloadResponse.Content.ReadAsStreamAsync(ct);
        var scryfallCards = await JsonSerializer.DeserializeAsync<List<ScryfallCard>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            ct) ?? [];

        // Convert to our model
        return scryfallCards.Select(MapToMtgCard).ToList();
    }

    private static MtgCard MapToMtgCard(ScryfallCard sc) => new()
    {
        ScryfallId = sc.Id ?? "",
        Name = sc.Name ?? "",
        ManaCost = sc.ManaCost,
        Cmc = sc.Cmc,
        TypeLine = sc.TypeLine,
        OracleText = sc.OracleText,
        Colors = sc.Colors ?? [],
        ColorIdentity = sc.ColorIdentity ?? [],
        Keywords = sc.Keywords ?? [],
        Power = sc.Power,
        Toughness = sc.Toughness,
        Rarity = sc.Rarity,
        SetName = sc.SetName,
        Prices = new CardPrices
        {
            Usd = sc.Prices?.Usd,
            UsdFoil = sc.Prices?.UsdFoil
        },
        ImageUris = sc.ImageUris != null ? new CardImageUris
        {
            Normal = sc.ImageUris.Normal,
            Small = sc.ImageUris.Small,
            ArtCrop = sc.ImageUris.ArtCrop
        } : null,
        ScryfallUri = sc.ScryfallUri,
        Legalities = sc.Legalities ?? new Dictionary<string, string>()
    };

    // ─── Qdrant Embed + Upsert ────────────────────────────────────────────────

    /// <summary>
    /// Generates a stable, collision-resistant point ID from a Scryfall UUID string.
    /// Uses SHA-256 to produce a deterministic 64-bit hash, avoiding collisions
    /// that can occur with GetHashCode() % modulo approaches.
    /// </summary>
    private static ulong StablePointId(string scryfallId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scryfallId));
        return BitConverter.ToUInt64(hash, 0);
    }

    private async Task<int> EmbedAndUpsertToQdrantAsync(List<MtgCard> cards, CancellationToken ct)
    {
        // Ensure collection exists — get vector dimension from a test embedding
        await EnsureQdrantCollectionAsync(ct);

        int upserted = 0;
        for (int i = 0; i < cards.Count; i += BatchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = cards.Skip(i).Take(BatchSize).ToList();
            var points = new List<PointStruct>();

            foreach (var card in batch)
            {
                if (ct.IsCancellationRequested) break;

                var text = BuildCardText(card);
                float[] vector;
                try
                {
                    vector = await _embedder.EmbedAsync(text, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to embed card '{Name}': {Error}", card.Name, ex.Message);
                    if (ct.IsCancellationRequested) break;
                    continue;
                }

                var pointId = StablePointId(card.ScryfallId);
                var payload = BuildPayload(card);

                points.Add(new PointStruct
                {
                    Id = new PointId { Num = pointId },
                    Vectors = new Vectors { Vector = new Vector { Data = { vector } } },
                    Payload = { payload }
                });
            }

            // Flush any already-embedded points in this batch.
            // CancellationToken.None is intentional: avoid losing successfully-embedded
            // cards just because cancellation was requested mid-batch.
            if (points.Count > 0)
            {
                await _qdrant.UpsertAsync(QdrantCollection, points, cancellationToken: CancellationToken.None);
                upserted += points.Count;
            }

            if (i % (BatchSize * 10) == 0 && i > 0)
                _logger.LogInformation("Qdrant progress: {Count}/{Total} cards embedded", i, cards.Count);        }

        return upserted;
    }

    private async Task EnsureQdrantCollectionAsync(CancellationToken ct)
    {
        try
        {
            await _qdrant.GetCollectionInfoAsync(QdrantCollection, ct);
            _logger.LogInformation("Qdrant collection '{Collection}' exists, upserting...", QdrantCollection);
        }
        catch
        {
            // Collection doesn't exist — create it
            // Get vector dimension from a test embedding
            var testVector = await _embedder.EmbedAsync("test card", ct);
            var vectorSize = (ulong)testVector.Length;

            _logger.LogInformation(
                "Creating Qdrant collection '{Collection}' (dim={Dim})...",
                QdrantCollection, vectorSize);

            await _qdrant.CreateCollectionAsync(
                QdrantCollection,
                new VectorParams { Size = vectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    private static string BuildCardText(MtgCard card)
    {
        var parts = new[]
        {
            card.Name,
            card.TypeLine ?? "",
            card.OracleText ?? "",
            $"Mana cost: {card.ManaCost ?? ""}",
            $"CMC: {card.Cmc}",
            $"Colors: {string.Join(" ", card.Colors)}",
            $"Color identity: {string.Join(" ", card.ColorIdentity)}",
            $"Keywords: {string.Join(" ", card.Keywords)}",
            $"Power: {card.Power ?? ""} Toughness: {card.Toughness ?? ""}"
        };
        return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p.Trim(' ', '|'))));
    }

    private static Dictionary<string, Value> BuildPayload(MtgCard card)
    {
        var priceUsd = double.TryParse(card.Prices?.Usd, out var p) ? p : 0.0;

        var payload = new Dictionary<string, Value>
        {
            ["scryfall_id"] = new Value { StringValue = card.ScryfallId },
            ["name"] = new Value { StringValue = card.Name },
            ["mana_cost"] = new Value { StringValue = card.ManaCost ?? "" },
            ["cmc"] = new Value { DoubleValue = card.Cmc },
            ["type_line"] = new Value { StringValue = card.TypeLine ?? "" },
            ["oracle_text"] = new Value { StringValue = card.OracleText ?? "" },
            ["price_usd"] = new Value { DoubleValue = priceUsd },
            ["scryfall_uri"] = new Value { StringValue = card.ScryfallUri ?? "" },
            ["image_uri"] = new Value { StringValue = card.ImageUris?.Normal ?? "" },
            ["set_name"] = new Value { StringValue = card.SetName ?? "" },
            ["rarity"] = new Value { StringValue = card.Rarity ?? "" }
        };

        // Color identity as list
        var colorList = new ListValue();
        foreach (var c in card.ColorIdentity)
            colorList.Values.Add(new Value { StringValue = c });
        payload["color_identity"] = new Value { ListValue = colorList };

        // Legality fields for all supported formats
        foreach (var fmt in SupportedFormats)
        {
            var status = card.Legalities.TryGetValue(fmt, out var s) ? s : "not_legal";
            payload[$"legality_{fmt}"] = new Value { StringValue = status };
        }

        return payload;
    }

    // ─── Scryfall DTOs ────────────────────────────────────────────────────────

    private class ScryfallBulkDataResponse
    {
        public List<ScryfallBulkDataEntry>? Data { get; set; }
    }

    private class ScryfallBulkDataEntry
    {
        public string Type { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("download_uri")]
        public string DownloadUri { get; set; } = "";
    }

    private class ScryfallCard
    {
        public string? Id { get; set; }
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mana_cost")]
        public string? ManaCost { get; set; }

        public double Cmc { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type_line")]
        public string? TypeLine { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("oracle_text")]
        public string? OracleText { get; set; }

        public List<string>? Colors { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("color_identity")]
        public List<string>? ColorIdentity { get; set; }

        public List<string>? Keywords { get; set; }
        public string? Power { get; set; }
        public string? Toughness { get; set; }
        public string? Rarity { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("set_name")]
        public string? SetName { get; set; }

        public ScryfallPrices? Prices { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("image_uris")]
        public ScryfallImageUris? ImageUris { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("scryfall_uri")]
        public string? ScryfallUri { get; set; }

        public Dictionary<string, string>? Legalities { get; set; }
    }

    private class ScryfallPrices
    {
        public string? Usd { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("usd_foil")]
        public string? UsdFoil { get; set; }
    }

    private class ScryfallImageUris
    {
        public string? Normal { get; set; }
        public string? Small { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("art_crop")]
        public string? ArtCrop { get; set; }
    }
}

/// <summary>Result summary from the card ingestion pipeline.</summary>
public record IngestionResult
{
    public int TotalCardsDownloaded { get; set; }
    public int MongoCardsUpserted { get; set; }
    public int QdrantVectorsUpserted { get; set; }
    public double ElapsedSeconds { get; set; }
}
