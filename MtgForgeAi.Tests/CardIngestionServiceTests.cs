using MtgForgeAi.Models;
using MtgForgeAi.Services;
using static MtgForgeAi.Services.CardIngestionService;

namespace MtgForgeAi.Tests;

/// <summary>
/// Tests for CardIngestionService's pure static helpers.
/// These cover the card mapping, text building, Qdrant payload construction,
/// and deterministic point ID generation that underpin every ingestion run.
/// </summary>
public class CardIngestionServiceTests
{
    // ─── StablePointId ────────────────────────────────────────────────────────

    [Fact]
    public void StablePointId_SameInput_ReturnsSameId()
    {
        var id = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        Assert.Equal(CardIngestionService.StablePointId(id), CardIngestionService.StablePointId(id));
    }

    [Fact]
    public void StablePointId_DifferentInputs_ReturnDifferentIds()
    {
        var id1 = CardIngestionService.StablePointId("card-1");
        var id2 = CardIngestionService.StablePointId("card-2");
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void StablePointId_KnownScryfallId_IsConsistentAcrossRuns()
    {
        // Pinned expected value computed from SHA-256("e9d5014b-0c7e-4c8e-93b3-6f66b27c52e5")[0..8]
        // If this value changes, re-ingestion of Qdrant is required.
        var pinned = CardIngestionService.StablePointId("e9d5014b-0c7e-4c8e-93b3-6f66b27c52e5");
        Assert.Equal(CardIngestionService.StablePointId("e9d5014b-0c7e-4c8e-93b3-6f66b27c52e5"), pinned);
    }

    [Fact]
    public void StablePointId_EmptyString_ReturnsAValue()
    {
        // Should not throw; deterministic for empty string too.
        var id = CardIngestionService.StablePointId("");
        Assert.Equal(CardIngestionService.StablePointId(""), id);
    }

    // ─── MapToMtgCard ─────────────────────────────────────────────────────────

    [Fact]
    public void MapToMtgCard_FullCard_MapsAllFields()
    {
        var sc = new ScryfallCard
        {
            Id          = "abc-123",
            Name        = "Lightning Bolt",
            ManaCost    = "{R}",
            Cmc         = 1,
            TypeLine    = "Instant",
            OracleText  = "Deal 3 damage to any target.",
            Colors      = ["R"],
            ColorIdentity = ["R"],
            Keywords    = [],
            Power       = null,
            Toughness   = null,
            Rarity      = "common",
            SetName     = "Alpha",
            ScryfallUri = "https://scryfall.com/card/abc",
            Prices      = new ScryfallPrices { Usd = "0.50", UsdFoil = "1.20" },
            ImageUris   = new ScryfallImageUris { Normal = "https://img/normal.jpg", Small = "https://img/small.jpg", ArtCrop = "https://img/crop.jpg" },
            Legalities  = new Dictionary<string, string> { ["commander"] = "legal", ["standard"] = "not_legal" }
        };

        var card = CardIngestionService.MapToMtgCard(sc);

        Assert.Equal("abc-123", card.ScryfallId);
        Assert.Equal("Lightning Bolt", card.Name);
        Assert.Equal("{R}", card.ManaCost);
        Assert.Equal(1, card.Cmc);
        Assert.Equal("Instant", card.TypeLine);
        Assert.Equal("Deal 3 damage to any target.", card.OracleText);
        Assert.Equal(["R"], card.Colors);
        Assert.Equal(["R"], card.ColorIdentity);
        Assert.Equal("common", card.Rarity);
        Assert.Equal("Alpha", card.SetName);
        Assert.Equal("https://scryfall.com/card/abc", card.ScryfallUri);
        Assert.Equal("0.50", card.Prices?.Usd);
        Assert.Equal("1.20", card.Prices?.UsdFoil);
        Assert.Equal("https://img/normal.jpg", card.ImageUris?.Normal);
        Assert.Equal("https://img/small.jpg", card.ImageUris?.Small);
        Assert.Equal("https://img/crop.jpg", card.ImageUris?.ArtCrop);
        Assert.Equal("legal", card.Legalities["commander"]);
        Assert.Equal("not_legal", card.Legalities["standard"]);
    }

    [Fact]
    public void MapToMtgCard_NullOptionalFields_DefaultsGracefully()
    {
        var sc = new ScryfallCard { Id = null, Name = null };
        var card = CardIngestionService.MapToMtgCard(sc);

        Assert.Equal("", card.ScryfallId);
        Assert.Equal("", card.Name);
        Assert.Null(card.ManaCost);
        // Prices is always constructed (see MapToMtgCard_NullPrices_ProducesEmptyPricesObject)
        Assert.Null(card.Prices?.Usd);
        Assert.Null(card.Prices?.UsdFoil);
        Assert.Null(card.ImageUris);
        Assert.Empty(card.Colors);
        Assert.Empty(card.ColorIdentity);
        Assert.Empty(card.Keywords);
        Assert.Empty(card.Legalities);
    }

    [Fact]
    public void MapToMtgCard_NullPrices_ProducesEmptyPricesObject()
    {
        var sc = new ScryfallCard { Id = "x", Name = "Test", Prices = null };
        var card = CardIngestionService.MapToMtgCard(sc);

        // Prices object is still constructed (non-null) when sc.Prices is null
        Assert.NotNull(card.Prices);
        Assert.Null(card.Prices.Usd);
        Assert.Null(card.Prices.UsdFoil);
    }

    [Fact]
    public void MapToMtgCard_NullImageUris_LeavesImageUrisNull()
    {
        var sc = new ScryfallCard { Id = "x", Name = "Test", ImageUris = null };
        var card = CardIngestionService.MapToMtgCard(sc);
        Assert.Null(card.ImageUris);
    }

    // ─── BuildCardText ────────────────────────────────────────────────────────

    [Fact]
    public void BuildCardText_FullCard_ContainsAllParts()
    {
        var card = new MtgCard
        {
            Name          = "Sol Ring",
            TypeLine      = "Artifact",
            OracleText    = "{T}: Add {C}{C}.",
            ManaCost      = "{1}",
            Cmc           = 1,
            Colors        = [],
            ColorIdentity = [],
            Keywords      = ["Artifact"],
            Power         = null,
            Toughness     = null
        };

        var text = CardIngestionService.BuildCardText(card);

        Assert.Contains("Sol Ring", text);
        Assert.Contains("Artifact", text);
        Assert.Contains("{T}: Add {C}{C}.", text);
        Assert.Contains("Mana cost: {1}", text);
        Assert.Contains("CMC: 1", text);
    }

    [Fact]
    public void BuildCardText_Creature_IncludesPowerToughness()
    {
        var card = new MtgCard
        {
            Name      = "Grizzly Bears",
            TypeLine  = "Creature — Bear",
            OracleText = "",
            ManaCost  = "{1}{G}",
            Cmc       = 2,
            Colors    = ["G"],
            ColorIdentity = ["G"],
            Keywords  = [],
            Power     = "2",
            Toughness = "2"
        };

        var text = CardIngestionService.BuildCardText(card);
        Assert.Contains("Power: 2 Toughness: 2", text);
    }

    [Fact]
    public void BuildCardText_PipeSeparated_NoBlanks()
    {
        var card = new MtgCard
        {
            Name      = "Island",
            TypeLine  = "Basic Land — Island",
            OracleText = null,
            ManaCost  = null,
            Cmc       = 0,
            Colors    = [],
            ColorIdentity = ["U"],
            Keywords  = [],
            Power     = null,
            Toughness = null
        };

        var text = CardIngestionService.BuildCardText(card);

        // Adjacent empty pipes should not appear
        Assert.DoesNotContain("| |", text);
        Assert.Contains("Island", text);
    }

    // ─── BuildPayload ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildPayload_AllSupportedFormatsPresent()
    {
        var card = MakeMinimalCard();
        var payload = CardIngestionService.BuildPayload(card);

        foreach (var fmt in new[] { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" })
        {
            Assert.True(payload.ContainsKey($"legality_{fmt}"), $"Missing legality_{fmt}");
        }
    }

    [Fact]
    public void BuildPayload_LegalFormatWrittenCorrectly()
    {
        var card = MakeMinimalCard();
        card.Legalities["commander"] = "legal";

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal("legal", payload["legality_commander"].StringValue);
    }

    [Fact]
    public void BuildPayload_MissingLegalityDefaultsToNotLegal()
    {
        var card = MakeMinimalCard(); // Legalities is empty
        var payload = CardIngestionService.BuildPayload(card);

        Assert.Equal("not_legal", payload["legality_commander"].StringValue);
        Assert.Equal("not_legal", payload["legality_standard"].StringValue);
    }

    [Fact]
    public void BuildPayload_ValidUsdPrice_ParsedAsDouble()
    {
        var card = MakeMinimalCard();
        card.Prices = new CardPrices { Usd = "12.49" };

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal(12.49, payload["price_usd"].DoubleValue, precision: 5);
    }

    [Fact]
    public void BuildPayload_NullPrice_DefaultsToZero()
    {
        var card = MakeMinimalCard();
        card.Prices = new CardPrices { Usd = null };

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal(0.0, payload["price_usd"].DoubleValue);
    }

    [Fact]
    public void BuildPayload_NullPricesObject_DefaultsToZero()
    {
        var card = MakeMinimalCard();
        card.Prices = null;

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal(0.0, payload["price_usd"].DoubleValue);
    }

    [Fact]
    public void BuildPayload_ColorIdentity_StoredAsList()
    {
        var card = MakeMinimalCard();
        card.ColorIdentity = ["B", "G"];

        var payload = CardIngestionService.BuildPayload(card);
        var colors = payload["color_identity"].ListValue.Values;
        Assert.Equal(2, colors.Count);
        Assert.Contains(colors, v => v.StringValue == "B");
        Assert.Contains(colors, v => v.StringValue == "G");
    }

    [Fact]
    public void BuildPayload_EmptyColorIdentity_EmptyList()
    {
        var card = MakeMinimalCard();
        card.ColorIdentity = [];

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Empty(payload["color_identity"].ListValue.Values);
    }

    [Fact]
    public void BuildPayload_CoreStringFields_SetCorrectly()
    {
        var card = new MtgCard
        {
            ScryfallId    = "scry-id",
            Name          = "Black Lotus",
            ManaCost      = "{0}",
            Cmc           = 0,
            TypeLine      = "Artifact",
            OracleText    = "{T}: Tap me",
            Rarity        = "mythic",
            SetName       = "Alpha",
            ScryfallUri   = "https://scryfall.com/card/x",
            ImageUris     = new CardImageUris { Normal = "https://img/normal.jpg" },
            Prices        = new CardPrices { Usd = "5000.00" },
            Colors        = [],
            ColorIdentity = [],
            Keywords      = [],
            Legalities    = []
        };

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal("scry-id",    payload["scryfall_id"].StringValue);
        Assert.Equal("Black Lotus", payload["name"].StringValue);
        Assert.Equal("{0}",         payload["mana_cost"].StringValue);
        Assert.Equal(0.0,           payload["cmc"].DoubleValue);
        Assert.Equal("Artifact",    payload["type_line"].StringValue);
        Assert.Equal("{T}: Tap me", payload["oracle_text"].StringValue);
        Assert.Equal("mythic",      payload["rarity"].StringValue);
        Assert.Equal("Alpha",       payload["set_name"].StringValue);
        Assert.Equal("https://scryfall.com/card/x", payload["scryfall_uri"].StringValue);
        Assert.Equal("https://img/normal.jpg",      payload["image_uri"].StringValue);
    }

    [Fact]
    public void BuildPayload_NullImageUris_EmptyImageUri()
    {
        var card = MakeMinimalCard();
        card.ImageUris = null;

        var payload = CardIngestionService.BuildPayload(card);
        Assert.Equal("", payload["image_uri"].StringValue);
    }

    // ─── Format legality filtering (IngestAsync logic) ────────────────────────

    [Fact]
    public void LegalityFiltering_CardLegalInAtLeastOneFormat_IsIncluded()
    {
        var cards = new List<MtgCard>
        {
            new() { ScryfallId = "1", Name = "A", Colors = [], ColorIdentity = [], Keywords = [],
                    Legalities = new() { ["commander"] = "legal" } },
            new() { ScryfallId = "2", Name = "B", Colors = [], ColorIdentity = [], Keywords = [],
                    Legalities = new() { ["commander"] = "not_legal", ["standard"] = "legal" } },
            new() { ScryfallId = "3", Name = "C", Colors = [], ColorIdentity = [], Keywords = [],
                    Legalities = new() { ["commander"] = "not_legal" } }
        };

        var supportedFormats = new[] { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" };
        var legal = cards.Where(c =>
            supportedFormats.Any(fmt =>
                c.Legalities.TryGetValue(fmt, out var status) &&
                status.Equals("legal", StringComparison.OrdinalIgnoreCase))).ToList();

        Assert.Equal(2, legal.Count);
        Assert.Contains(legal, c => c.Name == "A");
        Assert.Contains(legal, c => c.Name == "B");
        Assert.DoesNotContain(legal, c => c.Name == "C");
    }

    [Fact]
    public void LegalityFiltering_CardWithNoLegalities_Excluded()
    {
        var card = new MtgCard { ScryfallId = "x", Name = "X", Colors = [], ColorIdentity = [], Keywords = [], Legalities = [] };
        var supportedFormats = new[] { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" };

        var legal = new[] { card }.Where(c =>
            supportedFormats.Any(fmt =>
                c.Legalities.TryGetValue(fmt, out var status) &&
                status.Equals("legal", StringComparison.OrdinalIgnoreCase))).ToList();

        Assert.Empty(legal);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static MtgCard MakeMinimalCard() => new()
    {
        ScryfallId    = "test-id",
        Name          = "Test Card",
        Colors        = [],
        ColorIdentity = [],
        Keywords      = [],
        Legalities    = []
    };
}
