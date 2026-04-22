using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MtgForgeAi.Models;

// ─── Request / Response DTOs ──────────────────────────────────────────────────

public record DeckRequest(
    string Format,           // "commander", "standard", "modern", "legacy", "pioneer", "pauper"
    string Theme,
    double Budget,
    int PowerLevel,          // 1–10
    string? Commander = null,            // Optional — if omitted for Commander format, the LLM will choose an appropriate commander
    List<string>? ColorIdentity = null,  // Required for Commander; optional color filter for others
    string? ExtraContext = null          // e.g. "focus on combo", "avoid infinite loops"
);

public record DeckResponse(
    string? Commander,
    string Theme,
    string Format,
    List<DeckSection> Sections,
    double EstimatedCost,
    string Reasoning,
    DateTime GeneratedAt,
    List<string>? ValidationWarnings = null
);

public record DeckSection(
    string Category,       // Ramp, Card Draw, Removal, Win Conditions, Lands, etc.
    List<DeckCard> Cards
);

public record DeckCard(
    string Name,
    int Quantity,
    double PriceUsd,
    string? OracleText,
    string? ImageUri,
    string? ScryfallUri,
    string? ManaCost = null,
    double Cmc = 0,
    string? TypeLine = null
);

// ─── Card Search ──────────────────────────────────────────────────────────────

public record CardSearchRequest(
    string Query,
    List<string>? Colors = null,
    double? MaxPrice = null,
    int Limit = 20,
    string Format = "commander"
);

public record CardSearchResult(
    string Name,
    string TypeLine,
    string? OracleText,
    string ManaCost,
    double Cmc,
    double PriceUsd,
    string? ImageUri,
    string? ScryfallUri,
    double Score  // Similarity score from Qdrant
);

// ─── Admin ─────────────────────────────────────────────────────────────────

public record IngestionRequest(
    bool MongoOnly = false,
    bool QdrantOnly = false,
    int? Limit = null
);

public class MtgCard
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? MongoId { get; set; }

    [BsonElement("id")]
    public string ScryfallId { get; set; } = "";

    [BsonElement("name")]
    public string Name { get; set; } = "";

    [BsonElement("mana_cost")]
    public string? ManaCost { get; set; }

    [BsonElement("cmc")]
    public double Cmc { get; set; }

    [BsonElement("type_line")]
    public string? TypeLine { get; set; }

    [BsonElement("oracle_text")]
    public string? OracleText { get; set; }

    [BsonElement("colors")]
    public List<string> Colors { get; set; } = new();

    [BsonElement("color_identity")]
    public List<string> ColorIdentity { get; set; } = new();

    [BsonElement("keywords")]
    public List<string> Keywords { get; set; } = new();

    [BsonElement("power")]
    public string? Power { get; set; }

    [BsonElement("toughness")]
    public string? Toughness { get; set; }

    [BsonElement("rarity")]
    public string? Rarity { get; set; }

    [BsonElement("set_name")]
    public string? SetName { get; set; }

    [BsonElement("prices")]
    public CardPrices? Prices { get; set; }

    [BsonElement("image_uris")]
    public CardImageUris? ImageUris { get; set; }

    [BsonElement("scryfall_uri")]
    public string? ScryfallUri { get; set; }

    [BsonElement("legalities")]
    public Dictionary<string, string> Legalities { get; set; } = new();
}

public class CardPrices
{
    [BsonElement("usd")]
    public string? Usd { get; set; }

    [BsonElement("usd_foil")]
    public string? UsdFoil { get; set; }
}

public class CardImageUris
{
    [BsonElement("normal")]
    public string? Normal { get; set; }

    [BsonElement("small")]
    public string? Small { get; set; }

    [BsonElement("art_crop")]
    public string? ArtCrop { get; set; }
}

// ─── Saved Deck ───────────────────────────────────────────────────────────────

public class SavedDeck
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string Commander { get; set; } = "";
    public string Theme { get; set; } = "";
    public string Format { get; set; } = "commander";
    public List<DeckSection> Sections { get; set; } = new();
    public double EstimatedCost { get; set; }
    public string Reasoning { get; set; } = "";
    public int PowerLevel { get; set; }
    public List<string> ColorIdentity { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
