using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

public record DeckValidationResult(
    bool Passed,
    List<string> Warnings,
    List<string> AutoFixed
);

/// <summary>
/// Post-generation checks for deck correctness and category coverage.
/// Warnings are surfaced to callers; AutoFixed records changes made in-place.
/// </summary>
public static class DeckValidator
{

    public static DeckValidationResult Validate(List<DeckSection> sections, string format)
    {
        var warnings  = new List<string>();
        var autoFixed = new List<string>();
        var isCommander = format.Equals("commander", StringComparison.OrdinalIgnoreCase);
        var allCards = sections.SelectMany(s => s.Cards).ToList();

        if (isCommander)
        {
            CheckSingletonViolations(allCards, warnings);
            CheckCategoryCoverage(sections, allCards, warnings);
        }

        return new DeckValidationResult(
            Passed:    warnings.Count == 0,
            Warnings:  warnings,
            AutoFixed: autoFixed);
    }

    private static void CheckSingletonViolations(List<DeckCard> allCards, List<string> warnings)
    {
        var duplicates = allCards
            .Where(c => !MtgConstants.BasicLandNames.Contains(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Sum(c => c.Quantity) > 1);

        foreach (var dup in duplicates)
            warnings.Add(
                $"Singleton violation: '{dup.Key}' appears {dup.Sum(c => c.Quantity)} times");
    }

    private static void CheckCategoryCoverage(
        List<DeckSection> sections,
        List<DeckCard> allCards,
        List<string> warnings)
    {
        int CountSection(string keyword) =>
            sections
                .Where(s => s.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Cards.Count);

        // Use exact-match for draw to avoid "Draw" Contains matching "Card Draw" twice
        int CountDrawSections() =>
            sections
                .Where(s => s.Category.Equals("Draw", StringComparison.OrdinalIgnoreCase) ||
                            s.Category.Equals("Card Draw", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Cards.Count);

        var rampCount    = CountSection("Ramp");
        var removalCount = CountSection("Removal");
        var drawCount    = CountDrawSections();
        var landCount    = allCards
            .Where(c => c.TypeLine?.Contains("Land", StringComparison.OrdinalIgnoreCase) == true)
            .Sum(c => c.Quantity);

        if (rampCount    < 6)  warnings.Add($"Low ramp count ({rampCount}); recommend ≥10");
        if (removalCount < 5)  warnings.Add($"Low removal count ({removalCount}); recommend ≥8");
        if (drawCount    < 5)  warnings.Add($"Low card draw count ({drawCount}); recommend ≥10");
        if (landCount    < 30) warnings.Add($"Low land count ({landCount}); Commander needs 36-38");
    }
}
