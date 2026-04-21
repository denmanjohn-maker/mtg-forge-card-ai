using System.Text;
using System.Text.Json;
using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Orchestrates the full deck generation pipeline:
///   1. Retrieve candidate cards via Qdrant semantic search (RAG)
///   2. Build a structured prompt with card context + constraints
///   3. Generate deck via local Ollama LLM
///   4. Parse and return structured DeckResponse
/// </summary>
public class DeckGenerationService
{
    private readonly CardSearchService _search;
    private readonly ILlmService _llm;
    private readonly MongoService _mongo;
    private readonly ILogger<DeckGenerationService> _logger;

    public DeckGenerationService(
        CardSearchService search,
        ILlmService llm,
        MongoService mongo,
        ILogger<DeckGenerationService> logger)
    {
        _search = search;
        _llm = llm;
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<DeckResponse> GenerateDeckAsync(DeckRequest req, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Generating {Format} deck | Theme: {Theme} | Budget: ${Budget}",
            req.Format, req.Theme, req.Budget);

        // ── Step 1: Retrieve candidate cards ──────────────────────────────────
        var candidates = await _search.GetDeckCandidatesAsync(req, ct);
        _logger.LogInformation("Retrieved {Count} candidate cards from Qdrant", candidates.Count);

        // ── Step 2: Build the prompt ───────────────────────────────────────────
        var systemPrompt = BuildSystemPrompt(req.Format, !string.IsNullOrWhiteSpace(req.Commander));
        var userPrompt = BuildUserPrompt(req, candidates);

        // ── Step 3: Call Ollama ────────────────────────────────────────────────
        var rawResponse = await _llm.ChatAsync(systemPrompt, userPrompt, ct);
        _logger.LogInformation("Received LLM response ({Length} chars)", rawResponse.Length);

        // ── Step 4: Parse the response ─────────────────────────────────────────
        var deck = ParseDeckResponse(rawResponse, req, candidates);

        // ── Step 5: Save to MongoDB ────────────────────────────────────────────
        await _mongo.SaveDeckAsync(new SavedDeck
        {
            Commander     = deck.Commander ?? "",
            Theme         = deck.Theme,
            Format        = deck.Format,
            Sections      = deck.Sections,
            EstimatedCost = deck.EstimatedCost,
            Reasoning     = deck.Reasoning,
            PowerLevel    = req.PowerLevel,
            ColorIdentity = req.ColorIdentity ?? [],
            CreatedAt     = DateTime.UtcNow
        }, ct);

        return deck;
    }

    // ─── Format Rules ─────────────────────────────────────────────────────────

    private sealed record FormatRules(
        int DeckSize,
        bool Singleton,
        bool RequiresCommander,
        int MaxCopies,
        string LandCountRange,
        string[] IncludeGuidance,
        string[] Categories);

    private static FormatRules GetFormatRules(string format) => format.ToLowerInvariant() switch
    {
        "commander" => new FormatRules(
            DeckSize: 100,
            Singleton: true,
            RequiresCommander: true,
            MaxCopies: 1,
            LandCountRange: "36-38",
            IncludeGuidance: ["~10 ramp spells", "~10 card draw effects", "~8-10 removal spells", "~5 board wipes", "36-38 lands"],
            Categories: ["Commander", "Ramp", "Card Draw", "Removal", "Board Wipes", "Win Conditions", "Synergy Pieces", "Utility", "Lands"]
        ),
        "pauper" => new FormatRules(
            DeckSize: 60,
            Singleton: false,
            RequiresCommander: false,
            MaxCopies: 4,
            LandCountRange: "20-24",
            IncludeGuidance: ["up to 4 copies per card", "COMMONS ONLY — no uncommons, rares, or mythics", "20-24 lands"],
            Categories: ["Creatures", "Spells", "Removal", "Win Conditions", "Utility", "Lands"]
        ),
        _ => new FormatRules( // standard, modern, legacy, pioneer, vintage
            DeckSize: 60,
            Singleton: false,
            RequiresCommander: false,
            MaxCopies: 4,
            LandCountRange: "22-26",
            IncludeGuidance: ["up to 4 copies of each non-basic card", "22-26 lands", "balanced mana curve"],
            Categories: ["Creatures", "Spells", "Removal", "Win Conditions", "Utility", "Lands"]
        )
    };

    private static string BuildSystemPrompt(string format, bool commanderNameProvided = false)
    {
        var rules = GetFormatRules(format);
        var fmt = format.ToUpperInvariant();

        var commanderRules = rules.RequiresCommander
            ? """
              - Include exactly 1 commander (a legendary creature or planeswalker with commander ability)
              - All cards must match the commander's color identity — no cards outside those colors
              - Every card must have quantity 1 (singleton format). Basic lands may have multiple copies.
              """
            : $"""
              - Up to {rules.MaxCopies} copies of each non-basic land card are allowed
              - No commander required; build a coherent 1-3 color strategy
              """;

        var shouldSelectCommander = rules.RequiresCommander && !commanderNameProvided;

        var commanderSelectionNote = shouldSelectCommander
            ? "\n- Select an appropriate legendary creature or planeswalker as commander based on the theme and color identity. Include its name in the commander field of the JSON response."
            : "";

        var pauperNote = format.Equals("pauper", StringComparison.OrdinalIgnoreCase)
            ? "\n- CRITICAL: Pauper format. ALL cards must be common rarity. Absolutely no uncommons, rares, or mythics."
            : "";

        var jsonSchema = rules.RequiresCommander
            ? """
            {
              "commander": "Chosen Commander Name",
              "reasoning": "Brief explanation of the deck strategy and key synergies",
              "sections": [
                {
                  "category": "Category Name",
                  "cards": [
                    { "name": "Card Name", "quantity": 1 }
                  ]
                }
              ]
            }
            """
            : """
            {
              "reasoning": "Brief explanation of the deck strategy and key synergies",
              "sections": [
                {
                  "category": "Category Name",
                  "cards": [
                    { "name": "Card Name", "quantity": 1 }
                  ]
                }
              ]
            }
            """;

        return $"""
            You are an expert Magic: The Gathering deckbuilder specializing in {fmt} format.

            Format requirements:
            - Build a deck with exactly {rules.DeckSize} cards total
            {commanderRules}{commanderSelectionNote}{pauperNote}
            - Balance the mana curve appropriately
            - Include: {string.Join(", ", rules.IncludeGuidance)}
            - Stay within the specified budget

            Always respond in the following JSON format:
            {jsonSchema}

            Categories should be: {string.Join(", ", rules.Categories)}

            Respond ONLY with valid JSON. No markdown, no explanation outside JSON.
            """;
    }

    private static string BuildUserPrompt(DeckRequest req, List<CardSearchResult> candidates)
    {
        var sb = new StringBuilder();
        var isCommander = req.Format.Equals("commander", StringComparison.OrdinalIgnoreCase);
        var rules = GetFormatRules(req.Format);

        // Per-card price cap: budget spread across non-land slots.
        // Commander assumes ~30 lands leaving ~70 spell slots; 60-card formats assume ~24 lands leaving ~36.
        var spellSlots = isCommander ? 70.0 : 36.0;
        var perCardBudget = req.Budget / spellSlots;
        var maxCardPrice = perCardBudget * 4;

        sb.AppendLine($"Build a {req.Format.ToUpperInvariant()} deck with the following requirements:");
        if (isCommander && req.Commander != null)
            sb.AppendLine($"- Commander: {req.Commander}");
        else if (isCommander)
            sb.AppendLine("- Choose an appropriate commander for the theme and color identity.");
        sb.AppendLine($"- Theme/Strategy: {req.Theme}");
        if (req.ColorIdentity?.Count > 0)
            sb.AppendLine($"- Color Identity: {string.Join(", ", req.ColorIdentity)}");
        sb.AppendLine($"- Budget: ${req.Budget:F2} HARD LIMIT — the total price of all cards must not exceed this");
        sb.AppendLine($"- Max price per card: ${maxCardPrice:F2} (average target: ${perCardBudget:F2}/card)");
        sb.AppendLine($"- Power Level: {req.PowerLevel}/10");

        if (!string.IsNullOrWhiteSpace(req.ExtraContext))
            sb.AppendLine($"- Additional notes: {req.ExtraContext}");

        sb.AppendLine();
        sb.AppendLine("Available cards (choose ONLY from this list — do not add cards not shown here):");
        sb.AppendLine();

        // Group candidates by rough category, sorted cheapest-first so the LLM sees budget options prominently
        var grouped = candidates
            .Take(80) // Keep prompt compact for CPU inference
            .GroupBy(c => GuessCategory(c.TypeLine))
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var card in group.OrderBy(c => c.PriceUsd).Take(20))
            {
                var price = card.PriceUsd > 0 ? $"${card.PriceUsd:F2}" : "free";
                var text = card.OracleText?.Length > 80
                    ? card.OracleText[..80] + "..."
                    : card.OracleText ?? "";
                sb.AppendLine($"  - {card.Name} ({card.ManaCost}) [{price}]: {text}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"CRITICAL: Select only cards from the list above. Basic lands (Plains, Island, Swamp, Mountain, Forest) may be added freely.");
        sb.AppendLine($"CRITICAL: Keep the total deck cost under ${req.Budget:F2}. Prefer cards priced under ${perCardBudget:F2} each.");

        return sb.ToString();
    }

    private static string GuessCategory(string? typeLine)
    {
        if (typeLine is null) return "Other";
        if (typeLine.Contains("Land"))       return "Lands";
        if (typeLine.Contains("Creature"))   return "Creatures";
        if (typeLine.Contains("Planeswalker")) return "Planeswalkers";
        if (typeLine.Contains("Instant"))    return "Instants";
        if (typeLine.Contains("Sorcery"))    return "Sorceries";
        if (typeLine.Contains("Enchantment")) return "Enchantments";
        if (typeLine.Contains("Artifact"))   return "Artifacts";
        return "Other";
    }

    private DeckResponse ParseDeckResponse(
        string raw,
        DeckRequest req,
        List<CardSearchResult> candidates)
    {
        // Strip markdown fences if present
        var json = raw
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

        // Try to find JSON object in case LLM added preamble
        var start = json.IndexOf('{');
        var end   = json.LastIndexOf('}');
        if (start >= 0 && end > start)
            json = json[start..(end + 1)];

        LlmDeckOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LlmDeckOutput>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM JSON response, returning raw");
            parsed = null;
        }

        // Build card lookup from candidates for price enrichment
        var priceMap = candidates.ToDictionary(
            c => c.Name.ToLowerInvariant(),
            c => c,
            StringComparer.OrdinalIgnoreCase);

        var sections = parsed?.Sections?.Select(s => new DeckSection(
            Category: s.Category ?? "Uncategorized",
            Cards: s.Cards?.Select(c =>
            {
                priceMap.TryGetValue(c.Name?.ToLowerInvariant() ?? "", out var info);
                return new DeckCard(
                    Name:        c.Name ?? "",
                    Quantity:    c.Quantity,
                    PriceUsd:    info?.PriceUsd ?? 0,
                    OracleText:  info?.OracleText,
                    ImageUri:    info?.ImageUri,
                    ScryfallUri: info?.ScryfallUri,
                    ManaCost:    info?.ManaCost,
                    Cmc:         info?.Cmc ?? 0,
                    TypeLine:    info?.TypeLine
                );
            }).ToList() ?? []
        )).ToList() ?? [];

        var totalCost = sections
            .SelectMany(s => s.Cards)
            .Sum(c => c.PriceUsd * c.Quantity);

        // Post-generation budget enforcement: replace/remove expensive cards if over budget
        if (req.Budget > 0 && totalCost > req.Budget)
        {
            _logger.LogWarning(
                "Generated deck cost ${Cost:F2} exceeds budget ${Budget:F2} — applying budget enforcement",
                totalCost, req.Budget);
            sections = EnforceBudget(sections, req.Budget, priceMap);
            totalCost = sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);
            _logger.LogInformation("After budget enforcement, deck cost is ${Cost:F2}", totalCost);
        }

        // Resolve commander: caller-supplied → LLM JSON field → legendary creature in Commander section
        string? resolvedCommander = req.Commander;
        if (string.IsNullOrWhiteSpace(resolvedCommander))
            resolvedCommander = parsed?.Commander;
        if (string.IsNullOrWhiteSpace(resolvedCommander))
        {
            var commanderSection = parsed?.Sections?
                .FirstOrDefault(s => s.Category?.Equals("Commander", StringComparison.OrdinalIgnoreCase) == true);
            resolvedCommander = commanderSection?.Cards?.FirstOrDefault()?.Name;
        }

        return new DeckResponse(
            Commander:     resolvedCommander,
            Theme:         req.Theme,
            Format:        req.Format,
            Sections:      sections,
            EstimatedCost: totalCost,
            Reasoning:     parsed?.Reasoning ?? raw,
            GeneratedAt:   DateTime.UtcNow
        );
    }

    /// <summary>
    /// Iteratively replaces the most expensive non-land, non-commander cards with cheaper
    /// candidates from the search pool until the deck fits within budget.
    /// Cards with unknown price ($0) that aren't in the candidate pool are left in place;
    /// they appear as $0 in the cost total so they cannot cause an over-budget reading.
    /// </summary>
    private List<DeckSection> EnforceBudget(
        List<DeckSection> sections,
        double budget,
        Dictionary<string, CardSearchResult> priceMap)
    {
        var skipped = new HashSet<string>(["Lands", "Commander"], StringComparer.OrdinalIgnoreCase);

        // Track names already in the deck to avoid duplicates
        var inDeck = new HashSet<string>(
            sections.SelectMany(s => s.Cards).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        // Build an ascending-price pool of candidates not already in the deck
        var cheapPool = priceMap.Values
            .Where(c => !inDeck.Contains(c.Name))
            .OrderBy(c => c.PriceUsd)
            .ToList();

        // Snapshot of replaceable cards sorted most-expensive-first.
        // Cards may be replaced/removed during iteration; FindIndex re-checks the live section list
        // each time, so idx < 0 guards against stale snapshot entries safely.
        var replaceable = sections
            .Where(s => !skipped.Contains(s.Category))
            .SelectMany(s => s.Cards.Select(c => (Section: s, Card: c)))
            .Where(x => x.Card.PriceUsd > 0)
            .OrderByDescending(x => x.Card.PriceUsd * x.Card.Quantity)
            .ToList();

        var totalCost = sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);

        foreach (var (section, expensiveCard) in replaceable)
        {
            if (totalCost <= budget) break;

            // Find current index (card may have already been replaced in an earlier iteration)
            var idx = section.Cards.FindIndex(
                c => c.Name.Equals(expensiveCard.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;

            var cardType = GuessCategory(expensiveCard.TypeLine);

            // Prefer same-type replacement first, then any cheaper candidate — single pass with priority sort
            var replacement = cheapPool
                .Where(c => c.PriceUsd < expensiveCard.PriceUsd)
                .OrderByDescending(c => GuessCategory(c.TypeLine) == cardType)
                .ThenBy(c => c.PriceUsd)
                .FirstOrDefault();

            totalCost -= expensiveCard.PriceUsd * expensiveCard.Quantity;
            inDeck.Remove(expensiveCard.Name);

            if (replacement != null)
            {
                var newCard = new DeckCard(
                    Name:        replacement.Name,
                    Quantity:    expensiveCard.Quantity,
                    PriceUsd:    replacement.PriceUsd,
                    OracleText:  replacement.OracleText,
                    ImageUri:    replacement.ImageUri,
                    ScryfallUri: replacement.ScryfallUri,
                    ManaCost:    replacement.ManaCost,
                    Cmc:         replacement.Cmc,
                    TypeLine:    replacement.TypeLine
                );
                section.Cards[idx] = newCard;
                totalCost += newCard.PriceUsd * newCard.Quantity;
                inDeck.Add(replacement.Name);
                cheapPool.Remove(replacement);
                _logger.LogDebug(
                    "Budget enforcement: replaced '{Expensive}' (${OldPrice:F2}) with '{Cheap}' (${NewPrice:F2})",
                    expensiveCard.Name, expensiveCard.PriceUsd, replacement.Name, replacement.PriceUsd);
            }
            else
            {
                // No cheaper replacement available — remove the card entirely
                section.Cards.RemoveAt(idx);
                _logger.LogDebug(
                    "Budget enforcement: removed '{Expensive}' (${Price:F2}) — no cheaper replacement found",
                    expensiveCard.Name, expensiveCard.PriceUsd);
            }
        }

        return sections;
    }

    // ─── LLM Output Shape ─────────────────────────────────────────────────────

    private class LlmDeckOutput
    {
        public string? Commander { get; set; }
        public string? Reasoning { get; set; }
        public List<LlmSection>? Sections { get; set; }
    }

    private class LlmSection
    {
        public string? Category { get; set; }
        public List<LlmCard>? Cards { get; set; }
    }

    private class LlmCard
    {
        public string? Name { get; set; }
        public int Quantity { get; set; } = 1;
    }
}
