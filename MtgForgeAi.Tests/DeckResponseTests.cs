using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MtgForgeAi.Controllers;
using MtgForgeAi.Models;

namespace MtgForgeAi.Tests;

public class DeckResponseTests
{
    [Fact]
    public void Serialization_OmitsUsageFieldsFromDeckPayload()
    {
        var deck = BuildDeck(inputTokens: 123, outputTokens: 456);

        var json = JsonSerializer.Serialize(deck, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("inputTokens", json, StringComparison.Ordinal);
        Assert.DoesNotContain("outputTokens", json, StringComparison.Ordinal);
        Assert.Equal(123, deck.InputTokens);
        Assert.Equal(456, deck.OutputTokens);
    }

    [Fact]
    public void ApplyGenerationUsageHeaders_WritesPositiveUsageCounts()
    {
        var response = new DefaultHttpContext().Response;
        var deck = BuildDeck(inputTokens: 123, outputTokens: 456);

        DecksController.ApplyGenerationUsageHeaders(response, deck);

        Assert.Equal("123", response.Headers[DecksController.InputTokenHeaderName].ToString());
        Assert.Equal("456", response.Headers[DecksController.OutputTokenHeaderName].ToString());
    }

    [Fact]
    public void ApplyGenerationUsageHeaders_SkipsZeroUsageCounts()
    {
        var response = new DefaultHttpContext().Response;
        var deck = BuildDeck(inputTokens: 0, outputTokens: 0);

        DecksController.ApplyGenerationUsageHeaders(response, deck);

        Assert.False(response.Headers.ContainsKey(DecksController.InputTokenHeaderName));
        Assert.False(response.Headers.ContainsKey(DecksController.OutputTokenHeaderName));
    }

    private static DeckResponse BuildDeck(int inputTokens, int outputTokens) => new(
        Commander: "Meren of Clan Nel Toth",
        Theme: "sacrifice",
        Format: "commander",
        Sections:
        [
            new DeckSection("Ramp", [new DeckCard("Sol Ring", 1, 1.50, null, null, null)])
        ],
        EstimatedCost: 1.50,
        Reasoning: "Test deck",
        GeneratedAt: DateTime.UtcNow,
        ValidationWarnings: null,
        InputTokens: inputTokens,
        OutputTokens: outputTokens
    );
}
