using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Detects mentions of Universes Beyond / themed MTG products (e.g. Avatar: The
/// Last Airbender, Warhammer 40K, TMNT) in free-text deck request fields and
/// loads matching cards from MongoDB so the deck generator can feature them.
///
/// Matching is case-insensitive substring against a curated alias list, and
/// MongoDB lookups are by case-insensitive substring on the `set_name` field —
/// resilient to small Scryfall naming variations between releases.
/// </summary>
public class ThemedSetService
{
    private readonly MongoService _mongo;
    private readonly ILogger<ThemedSetService> _logger;

    public ThemedSetService(MongoService mongo, ILogger<ThemedSetService> logger)
    {
        _mongo  = mongo;
        _logger = logger;
    }

    private sealed record ThemedProduct(
        string DisplayName,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> SetNameSubstrings);

    // Aliases are matched as case-insensitive substrings against the user's
    // theme + extraContext. SetNameSubstrings are case-insensitive substrings
    // of Scryfall's `set_name` (covers main set, Commander decks, and
    // companion products without enumerating every set code).
    private static readonly IReadOnlyList<ThemedProduct> KnownThemes = new List<ThemedProduct>
    {
        new("Warhammer 40,000",
            ["warhammer", "40,000", "tyranid", "necron", "space marine", "imperium of man", "tau empire"],
            ["warhammer 40,000"]),
        new("The Lord of the Rings",
            ["lord of the rings", "lotr", "tolkien", "middle-earth", "middle earth", "frodo", "gandalf", "sauron", "aragorn"],
            ["lord of the rings", "tales of middle-earth"]),
        new("Doctor Who",
            ["doctor who", "dr who", "tardis", "dalek", "time lord"],
            ["doctor who"]),
        new("Final Fantasy",
            ["final fantasy", "ffxiv", "ff14", "ff7", "ffvii", "chocobo", "cloud strife", "sephiroth"],
            ["final fantasy"]),
        new("Avatar: The Last Airbender",
            ["avatar the last airbender", "the last airbender", "last airbender", "airbender", "aang", "korra", "appa", "zuko", "katara"],
            ["avatar"]),
        new("Marvel's Spider-Man",
            ["spider-man", "spiderman", "spider man", "peter parker"],
            ["spider-man"]),
        new("Fallout",
            ["fallout", "vault dweller", "wasteland survivor"],
            ["fallout"]),
        new("Jurassic World",
            ["jurassic park", "jurassic world", "jurassic"],
            ["jurassic"]),
        new("Stranger Things",
            ["stranger things", "demogorgon"],
            ["stranger things"]),
        new("Transformers",
            ["transformers", "optimus prime", "megatron"],
            ["transformers"]),
        new("Teenage Mutant Ninja Turtles",
            ["teenage mutant ninja turtles", "tmnt", "ninja turtles"],
            ["teenage mutant ninja", "tmnt"]),
        new("Assassin's Creed",
            ["assassin's creed", "assassins creed", "assassin creed", "ezio"],
            ["assassin's creed"]),
    };

    /// <summary>
    /// Returns the themed products mentioned across the supplied free-text
    /// fields (typically <c>Theme</c> and <c>ExtraContext</c>).
    /// </summary>
    public List<DetectedTheme> Detect(params string?[] texts)
    {
        var combined = string
            .Join(' ', texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            .ToLowerInvariant();
        if (combined.Length == 0)
            return new List<DetectedTheme>();

        var hits = new List<DetectedTheme>();
        foreach (var theme in KnownThemes)
        {
            if (theme.Aliases.Any(a => combined.Contains(a)))
                hits.Add(new DetectedTheme(theme.DisplayName, theme.SetNameSubstrings));
        }
        return hits;
    }

    /// <summary>
    /// Fetches up to <paramref name="limit"/> distinct cards from MongoDB
    /// whose <c>set_name</c> matches any of the substrings on the detected
    /// themes, filtered by color identity and format legality.
    /// </summary>
    public async Task<List<MtgCard>> LoadCandidateCardsAsync(
        IReadOnlyList<DetectedTheme> themes,
        IReadOnlyList<string>? colorIdentity,
        string format,
        int limit,
        CancellationToken ct = default)
    {
        if (themes.Count == 0)
            return new List<MtgCard>();

        var substrings = themes
            .SelectMany(t => t.SetNameSubstrings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<MtgCard> raw;
        try
        {
            // Pull a wide slice (4× limit) so color/legality filters still leave
            // plenty of room to choose from.
            raw = await _mongo.GetCardsBySetNameSubstringsAsync(substrings, limit * 4, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Themed set Mongo lookup failed for {Themes}",
                string.Join(", ", themes.Select(t => t.DisplayName)));
            return new List<MtgCard>();
        }

        if (raw.Count == 0)
            return raw;

        var colorSet = colorIdentity is { Count: > 0 }
            ? new HashSet<string>(colorIdentity, StringComparer.OrdinalIgnoreCase)
            : null;

        var legalityKey = LegalityKey(format);
        var seenNames   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked      = new List<MtgCard>();

        foreach (var card in raw)
        {
            if (picked.Count >= limit) break;
            if (!seenNames.Add(card.Name)) continue; // dedupe reprints

            if (colorSet != null && card.ColorIdentity.Any(ci => !colorSet.Contains(ci)))
                continue;

            if (card.Legalities.TryGetValue(legalityKey, out var legality) &&
                !legality.Equals("legal",      StringComparison.OrdinalIgnoreCase) &&
                !legality.Equals("restricted", StringComparison.OrdinalIgnoreCase))
                continue;

            picked.Add(card);
        }

        return picked;
    }

    private static string LegalityKey(string format) => format.ToLowerInvariant() switch
    {
        "standard" => "standard",
        "modern"   => "modern",
        "legacy"   => "legacy",
        "pioneer"  => "pioneer",
        "pauper"   => "pauper",
        "vintage"  => "vintage",
        _          => "commander"
    };
}

public record DetectedTheme(string DisplayName, IReadOnlyList<string> SetNameSubstrings);
