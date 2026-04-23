using Microsoft.Extensions.Logging.Abstractions;
using MtgForgeAi.Models;
using MtgForgeAi.Services;
using Moq;

namespace MtgForgeAi.Tests;

/// <summary>
/// Tests for MetaSignalService covering TierFor, delegation to the repository,
/// filtering by card name, availability checks, and cache behaviour.
/// </summary>
public class MetaSignalServiceTests
{
    // ─── TierFor ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.40, "🏆")]
    [InlineData(0.80, "🏆")]
    [InlineData(1.00, "🏆")]
    public void TierFor_AboveOrAt40Percent_ReturnsTrophy(double rate, string expected)
        => Assert.Equal(expected, MetaSignalService.TierFor(rate));

    [Theory]
    [InlineData(0.20, "★")]
    [InlineData(0.25, "★")]
    [InlineData(0.39, "★")]
    public void TierFor_Between20And40Percent_ReturnsStar(double rate, string expected)
        => Assert.Equal(expected, MetaSignalService.TierFor(rate));

    [Theory]
    [InlineData(0.10, "·")]
    [InlineData(0.15, "·")]
    [InlineData(0.19, "·")]
    public void TierFor_Between10And20Percent_ReturnsDot(double rate, string expected)
        => Assert.Equal(expected, MetaSignalService.TierFor(rate));

    [Theory]
    [InlineData(0.09)]
    [InlineData(0.00)]
    [InlineData(-0.01)]
    public void TierFor_Below10Percent_ReturnsEmpty(double rate)
        => Assert.Equal("", MetaSignalService.TierFor(rate));

    // ─── GetTopAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTopAsync_DelegatesToRepository()
    {
        var signals = new List<MetaSignal>
        {
            new() { CardName = "Sol Ring", Format = "commander", InclusionRate = 0.9 },
            new() { CardName = "Counterspell", Format = "commander", InclusionRate = 0.5 }
        };

        var repo  = BuildRepo(signals, null);
        var svc   = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var (result, stats) = await svc.GetTopAsync("commander");

        Assert.Equal(2, result.Count);
        Assert.Null(stats);
    }

    [Fact]
    public async Task GetTopAsync_RespectsLimit()
    {
        var signals = Enumerable.Range(1, 10)
            .Select(i => new MetaSignal { CardName = $"Card{i}", Format = "commander", InclusionRate = i / 10.0 })
            .ToList();

        var repo = BuildRepo(signals, null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var (result, _) = await svc.GetTopAsync("commander", limit: 3);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetTopAsync_NormalizesFormatToLowercase()
    {
        var repo = new Mock<IMetaSignalRepository>();
        repo.Setup(r => r.GetTopMetaSignalsAsync("commander", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetaSignal { CardName = "Sol Ring", Format = "commander" }]);
        repo.Setup(r => r.GetMetaSignalStatsAsync("commander", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MetaSignalStats?)null);

        var svc = new MetaSignalService(repo.Object, NullLogger<MetaSignalService>.Instance);
        var (result, _) = await svc.GetTopAsync("COMMANDER");

        Assert.Single(result);
        repo.Verify(r => r.GetTopMetaSignalsAsync("commander", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTopAsync_ReturnsStats()
    {
        var stats = new MetaSignalStats { Format = "modern", SampleSize = 500, UniqueCards = 100 };
        var repo  = BuildRepo([], stats);
        var svc   = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var (_, returnedStats) = await svc.GetTopAsync("modern");
        Assert.NotNull(returnedStats);
        Assert.Equal(500, returnedStats!.SampleSize);
    }

    // ─── GetSignalsForCardsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetSignalsForCardsAsync_ReturnsMatchingCards()
    {
        var signals = new List<MetaSignal>
        {
            new() { CardName = "Sol Ring",    Format = "commander", InclusionRate = 0.9 },
            new() { CardName = "Counterspell", Format = "commander", InclusionRate = 0.5 },
            new() { CardName = "Doom Blade",  Format = "commander", InclusionRate = 0.1 }
        };

        var repo = BuildRepo(signals, null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var lookup = await svc.GetSignalsForCardsAsync("commander", ["Sol Ring", "Doom Blade"]);

        Assert.Equal(2, lookup.Count);
        Assert.True(lookup.ContainsKey("Sol Ring"));
        Assert.True(lookup.ContainsKey("Doom Blade"));
        Assert.False(lookup.ContainsKey("Counterspell"));
    }

    [Fact]
    public async Task GetSignalsForCardsAsync_IsCaseInsensitive()
    {
        var signals = new List<MetaSignal>
        {
            new() { CardName = "Sol Ring", Format = "commander", InclusionRate = 0.9 }
        };

        var repo = BuildRepo(signals, null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var lookup = await svc.GetSignalsForCardsAsync("commander", ["sol ring"]);
        Assert.True(lookup.ContainsKey("Sol Ring"));
    }

    [Fact]
    public async Task GetSignalsForCardsAsync_EmptySignals_ReturnsEmptyDictionary()
    {
        var repo = BuildRepo([], null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        var lookup = await svc.GetSignalsForCardsAsync("commander", ["Sol Ring"]);
        Assert.Empty(lookup);
    }

    // ─── IsAvailableAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task IsAvailableAsync_WithSignals_ReturnsTrue()
    {
        var repo = BuildRepo([new MetaSignal { CardName = "X", Format = "standard" }], null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        Assert.True(await svc.IsAvailableAsync("standard"));
    }

    [Fact]
    public async Task IsAvailableAsync_NoSignals_ReturnsFalse()
    {
        var repo = BuildRepo([], null);
        var svc  = new MetaSignalService(repo, NullLogger<MetaSignalService>.Instance);

        Assert.False(await svc.IsAvailableAsync("standard"));
    }

    // ─── Cache behaviour ─────────────────────────────────────────────────────

    [Fact]
    public async Task Cache_MultipleCallsSameFormat_OnlyHitsRepositoryOnce()
    {
        var repo = new Mock<IMetaSignalRepository>();
        repo.Setup(r => r.GetTopMetaSignalsAsync("commander", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MetaSignal { CardName = "Sol Ring", Format = "commander" }]);
        repo.Setup(r => r.GetMetaSignalStatsAsync("commander", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MetaSignalStats?)null);

        var svc = new MetaSignalService(repo.Object, NullLogger<MetaSignalService>.Instance);

        await svc.GetTopAsync("commander");
        await svc.GetTopAsync("commander");
        await svc.GetTopAsync("commander");

        // Repository should only have been consulted once (second and third calls served from cache)
        repo.Verify(r => r.GetTopMetaSignalsAsync("commander", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cache_DifferentFormats_HitsRepositoryForEach()
    {
        var repo = new Mock<IMetaSignalRepository>();
        repo.Setup(r => r.GetTopMetaSignalsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repo.Setup(r => r.GetMetaSignalStatsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MetaSignalStats?)null);

        var svc = new MetaSignalService(repo.Object, NullLogger<MetaSignalService>.Instance);

        await svc.GetTopAsync("commander");
        await svc.GetTopAsync("modern");
        await svc.GetTopAsync("standard");

        repo.Verify(r => r.GetTopMetaSignalsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static IMetaSignalRepository BuildRepo(List<MetaSignal> signals, MetaSignalStats? stats)
    {
        var mock = new Mock<IMetaSignalRepository>();
        mock.Setup(r => r.GetTopMetaSignalsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signals);
        mock.Setup(r => r.GetMetaSignalStatsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);
        return mock.Object;
    }
}
