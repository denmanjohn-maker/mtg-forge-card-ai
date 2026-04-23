using MtgForgeAi.Models;
using MtgForgeAi.Services;

namespace MtgForgeAi.Tests;

/// <summary>
/// Tests for DeckValidator covering singleton violations, category coverage
/// checks, and the pass/fail logic in Validate().
/// </summary>
public class DeckValidatorTests
{
    // ─── Validate pass/fail ───────────────────────────────────────────────────

    [Fact]
    public void Validate_Commander_WellFormedDeck_Passes()
    {
        var sections = BuildCommanderSections(
            ramp: 10, removal: 8, draw: 10, landCount: 36);

        var result = DeckValidator.Validate(sections, "commander");

        Assert.True(result.Passed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_Standard_SkipsCategoryAndSingletonChecks()
    {
        // Standard format deck with no category coverage — validator should not warn
        var sections = new List<DeckSection>
        {
            new("Creatures", [new("Lightning Bolt", 4, 0.50, null, null, null)])
        };

        var result = DeckValidator.Validate(sections, "standard");

        Assert.True(result.Passed);
        Assert.Empty(result.Warnings);
    }

    // ─── Singleton violations ─────────────────────────────────────────────────

    [Fact]
    public void SingletonViolation_DuplicateNonBasicCard_ProducesWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 36);
        // Inject a duplicate non-basic
        sections[0] = sections[0] with
        {
            Cards = sections[0].Cards
                .Append(new DeckCard("Sol Ring", 1, 0, null, null, null))
                .ToList()
        };
        // Ensure Sol Ring already exists
        sections[0] = sections[0] with
        {
            Cards = [new DeckCard("Sol Ring", 1, 0, null, null, null), ..sections[0].Cards]
        };

        var result = DeckValidator.Validate(sections, "commander");
        Assert.Contains(result.Warnings, w => w.Contains("Sol Ring") && w.Contains("Singleton violation"));
    }

    [Fact]
    public void SingletonViolation_BasicLandsDuplicated_NoWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 36);
        // Extra basic lands should not trigger singleton warning
        sections.Add(new DeckSection("Extra Basics", [
            new DeckCard("Island",   10, 0, null, null, null, TypeLine: "Basic Land — Island"),
            new DeckCard("Mountain",  5, 0, null, null, null, TypeLine: "Basic Land — Mountain"),
        ]));

        var result = DeckValidator.Validate(sections, "commander");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Island") || w.Contains("Mountain"));
    }

    // ─── Category coverage warnings ───────────────────────────────────────────

    [Fact]
    public void CategoryCoverage_LowRamp_ProducesWarning()
    {
        var sections = BuildCommanderSections(ramp: 3, removal: 8, draw: 10, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.Contains(result.Warnings, w => w.Contains("ramp"));
    }

    [Fact]
    public void CategoryCoverage_SufficientRamp_NoRampWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("ramp"));
    }

    [Fact]
    public void CategoryCoverage_LowRemoval_ProducesWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 2, draw: 10, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.Contains(result.Warnings, w => w.Contains("removal"));
    }

    [Fact]
    public void CategoryCoverage_LowCardDraw_ProducesWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 2, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.Contains(result.Warnings, w => w.Contains("draw"));
    }

    [Fact]
    public void CategoryCoverage_LowLandCount_ProducesWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 20);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.Contains(result.Warnings, w => w.Contains("land"));
    }

    [Fact]
    public void CategoryCoverage_SufficientLands_NoLandWarning()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("land"));
    }

    // ─── AutoFixed list ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_ReturnsEmptyAutoFixed_WhenNothingFixed()
    {
        var sections = BuildCommanderSections(ramp: 10, removal: 8, draw: 10, landCount: 36);
        var result = DeckValidator.Validate(sections, "commander");
        Assert.Empty(result.AutoFixed);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a minimal Commander deck structure with configurable counts
    /// for ramp, removal, draw, and lands so individual thresholds can be targeted.
    /// </summary>
    private static List<DeckSection> BuildCommanderSections(
        int ramp, int removal, int draw, int landCount)
    {
        static List<DeckCard> MakeCards(string prefix, int count, string? typeLine = null) =>
            Enumerable.Range(1, count)
                .Select(i => new DeckCard($"{prefix} {i}", 1, 0, null, null, null, TypeLine: typeLine))
                .ToList();

        return
        [
            new DeckSection("Ramp",      MakeCards("Ramp",    ramp)),
            new DeckSection("Removal",   MakeCards("Removal", removal)),
            new DeckSection("Card Draw", MakeCards("Draw",    draw)),
            new DeckSection("Lands",     MakeCards("Plains", landCount, "Basic Land — Plains")),
        ];
    }
}
