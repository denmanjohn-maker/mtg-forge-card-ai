using System.Text;
using System.Text.Json;
using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Orchestrates the full deck generation pipeline.
/// Commander decks use a two-phase approach:
///   Phase 1 — mana base (lands + ramp, ~46 cards)
///   Phase 2 — spell suite (removal, draw, win cons, synergy, ~53 cards)
/// 60-card formats use a single-shot approach.
/// </summary>
public class DeckGenerationService
{
    private readonly CardSearchService _search;
    private readonly ILlmService _llm;
    private readonly MongoService _mongo;
    private readonly MetaSignalService _meta;
    private readonly ILogger<DeckGenerationService> _logger;

    // Cap how many missing top-meta cards we inject into the candidate pool
    // per generation. Small enough not to drown out the RAG results.
    private const int MaxMetaInjection = 20;

    public DeckGenerationService(
        CardSearchService search,
        ILlmService llm,
        MongoService mongo,
        MetaSignalService meta,
        ILogger<DeckGenerationService> logger)
    {
        _search = search;
        _llm    = llm;
        _mongo  = mongo;
        _meta   = meta;
        _logger = logger;
    }

    // ─── Public Entry Point ───────────────────────────────────────────────────

    public async Task<DeckResponse> GenerateDeckAsync(DeckRequest req, CancellationToken ct = default)
    {
        _logger.LogInformation("Generating {Format} | Theme: {Theme} | Budget: ${Budget}",
            req.Format, req.Theme, req.Budget);

        // Step 1: Multi-query candidate retrieval
        var candidates = await _search.GetDeckCandidatesAsync(req, ct);
        _logger.LogInformation("Retrieved {Count} candidate cards", candidates.Count);

        // Step 2: Fetch commander oracle text for richer prompting
        string? commanderOracleText = null;
        if (!string.IsNullOrWhiteSpace(req.Commander))
        {
            var commanderCard = await _mongo.GetCardByNameAsync(req.Commander, ct);
            commanderOracleText = commanderCard?.OracleText;
        }

        // Step 2b: Tournament meta signals (optional). Annotate existing candidates
        // and inject up to MaxMetaInjection top-meta cards that weren't retrieved.
        Dictionary<string, MetaSignal> metaSignals =
            new(StringComparer.OrdinalIgnoreCase);
        MetaSignalStats? metaStats = null;
        if (req.UseMetaSignals)
        {
            (candidates, metaSignals, metaStats) =
                await EnrichWithMetaSignalsAsync(req, candidates, ct);
        }

        var priceMap = candidates.ToDictionary(
            c => c.Name.ToLowerInvariant(), c => c, StringComparer.OrdinalIgnoreCase);

        // Step 3: Generate — two-phase for Commander, single-shot for 60-card formats
        var isCommander = req.Format.Equals("commander", StringComparison.OrdinalIgnoreCase);
        List<DeckSection> sections;
        string? resolvedCommander;
        string reasoning;

        if (isCommander)
        {
            (sections, resolvedCommander, reasoning) =
                await GenerateCommanderTwoPhaseAsync(
                    req, candidates, priceMap, commanderOracleText, metaSignals, metaStats, ct);
        }
        else
        {
            var sys  = BuildSystemPrompt(req.Format, !string.IsNullOrWhiteSpace(req.Commander));
            var user = BuildUserPrompt(req, candidates, commanderOracleText, metaSignals, metaStats);
            var raw  = await _llm.ChatAsync(sys, user, jsonMode: true, ct);
            _logger.LogInformation("LLM response ({Len} chars)", raw.Length);
            (sections, resolvedCommander, reasoning) = ParseDeckOutput(raw, req, priceMap);
        }

        // Step 4: Budget enforcement
        var totalCost = sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);
        if (req.Budget > 0 && totalCost > req.Budget)
        {
            _logger.LogWarning("Cost ${C:F2} exceeds budget ${B:F2} — enforcing", totalCost, req.Budget);
            sections  = EnforceBudget(sections, req.Budget, priceMap);
            totalCost = sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);
        }

        // Step 5: Post-generation validation
        var validation = DeckValidator.Validate(sections, req.Format);
        foreach (var w in validation.Warnings)
            _logger.LogWarning("Validation: {W}", w);

        var deck = new DeckResponse(
            Commander:          resolvedCommander,
            Theme:              req.Theme,
            Format:             req.Format,
            Sections:           sections,
            EstimatedCost:      totalCost,
            Reasoning:          reasoning,
            GeneratedAt:        DateTime.UtcNow,
            ValidationWarnings: validation.Warnings.Count > 0 ? validation.Warnings : null
        );

        // Step 6: Persist
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

    // ─── Meta Signal Enrichment ──────────────────────────────────────────────

    /// <summary>
    /// Looks up tournament meta signals for the retrieved candidates and, when
    /// top meta cards are missing from the candidate pool, hydrates them from
    /// MongoDB and appends them to the pool. Returns the (possibly expanded)
    /// candidate list, a name→signal map, and the format stats doc.
    ///
    /// All errors degrade silently — the deck pipeline must keep working when
    /// meta data is unavailable.
    /// </summary>
    private async Task<(List<CardSearchResult> Candidates, Dictionary<string, MetaSignal> Signals, MetaSignalStats? Stats)>
        EnrichWithMetaSignalsAsync(
            DeckRequest req,
            List<CardSearchResult> candidates,
            CancellationToken ct)
    {
        var emptyMap = new Dictionary<string, MetaSignal>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var (topSignals, stats) = await _meta.GetTopAsync(req.Format, limit: 500, ct: ct);
            if (topSignals.Count == 0)
                return (candidates, emptyMap, null);

            var signalByName = topSignals.ToDictionary(
                s => s.CardName, s => s, StringComparer.OrdinalIgnoreCase);

            // 1) Annotate existing candidates
            var candidateNames = new HashSet<string>(
                candidates.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var annotations = signalByName
                .Where(kv => candidateNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // 2) Inject up to MaxMetaInjection top-meta cards missing from the pool.
            //    Filter to the requested color identity when provided.
            var colorSet = req.ColorIdentity?.Count > 0
                ? new HashSet<string>(req.ColorIdentity, StringComparer.OrdinalIgnoreCase)
                : null;

            var missingTop = topSignals
                .Where(s => !candidateNames.Contains(s.CardName))
                .Take(MaxMetaInjection * 3) // fetch extra so color filter leaves enough
                .Select(s => s.CardName)
                .ToList();

            if (missingTop.Count > 0)
            {
                var fetched = await _mongo.GetCardsByNamesAsync(missingTop, ct);
                var added = 0;
                foreach (var card in fetched.OrderByDescending(c =>
                            signalByName.TryGetValue(c.Name, out var s) ? s.InclusionRate : 0.0))
                {
                    if (added >= MaxMetaInjection) break;

                    if (colorSet != null && card.ColorIdentity.Any(ci => !colorSet.Contains(ci)))
                        continue; // violates requested color identity

                    var sig = signalByName[card.Name];
                    candidates.Add(new CardSearchResult(
                        Name:        card.Name,
                        TypeLine:    card.TypeLine ?? "",
                        OracleText:  card.OracleText,
                        ManaCost:    card.ManaCost ?? "",
                        Cmc:         card.Cmc,
                        PriceUsd:    ParsePrice(card.Prices?.Usd),
                        ImageUri:    card.ImageUris?.Normal,
                        ScryfallUri: card.ScryfallUri,
                        Score:       0.0 // signals that this came from meta injection
                    ));
                    annotations[card.Name] = sig;
                    added++;
                }

                if (added > 0)
                    _logger.LogInformation(
                        "Meta signals: injected {Added} top-meta cards missing from RAG pool", added);
            }

            _logger.LogInformation(
                "Meta signals: {Anno} of {Cand} candidates are tournament-known (format={Format}, sample={Sample})",
                annotations.Count, candidates.Count, req.Format, stats?.SampleSize ?? 0);

            return (candidates, annotations, stats);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meta signal enrichment failed — continuing without");
            return (candidates, emptyMap, null);
        }
    }

    private static double ParsePrice(string? usd) =>
        double.TryParse(usd, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var p)
            ? p : 0.0;

    // ─── Two-Phase Commander Generation ──────────────────────────────────────

    private async Task<(List<DeckSection> Sections, string? Commander, string Reasoning)>
        GenerateCommanderTwoPhaseAsync(
            DeckRequest req,
            List<CardSearchResult> candidates,
            Dictionary<string, CardSearchResult> priceMap,
            string? commanderOracleText,
            Dictionary<string, MetaSignal> metaSignals,
            MetaSignalStats? metaStats,
            CancellationToken ct)
    {
        // Phase 1: Mana base (lands + ramp)
        const string phase1System = """
            You are an expert MTG deckbuilder. Build ONLY the mana base and ramp for a Commander deck.
            Return JSON with exactly two sections: "Ramp" (8-10 cards) and "Lands" (36-38 cards).
            All non-basic-land cards must have quantity 1. Basic lands may have quantity > 1.
            Respond ONLY with valid JSON: {"sections":[{"category":"Ramp","cards":[...]},{"category":"Lands","cards":[...]}]}
            """;

        var phase1User = BuildManaBaseUserPrompt(req, candidates, commanderOracleText, metaSignals);
        var phase1Raw  = await _llm.ChatAsync(phase1System, phase1User, jsonMode: true, ct);
        _logger.LogInformation("Phase 1 response ({Len} chars)", phase1Raw.Length);

        var phase1Sections = ParseRawToSections(phase1Raw, priceMap);
        RemoveHallucinations(phase1Sections, priceMap);

        var phase1Cost  = phase1Sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);
        var phase1Count = phase1Sections.SelectMany(s => s.Cards).Sum(c => c.Quantity);
        var remainingBudget = Math.Max(0, req.Budget - phase1Cost);
        var remainingSlots  = Math.Max(0, 99 - phase1Count); // Commander counts as the 100th card

        _logger.LogInformation("Phase 1: {Count} cards (${Cost:F2}). Phase 2: {Slots} slots, ${Budget:F2}",
            phase1Count, phase1Cost, remainingSlots, remainingBudget);

        // Phase 2: Spells
        const string phase2System = """
            You are an expert MTG deckbuilder. The mana base is locked in. Fill the remaining spell slots.
            Include the commander, removal, card draw, board wipes, win conditions, and synergy pieces.
            All cards must be singleton (quantity 1). Do NOT include any lands. Do NOT repeat mana base cards.
            Respond ONLY with valid JSON:
            {"commander":"Name","reasoning":"...","sections":[{"category":"...","cards":[{"name":"...","quantity":1}]}]}
            """;

        var phase2User = BuildSpellSuiteUserPrompt(
            req, candidates, phase1Sections, remainingSlots, remainingBudget,
            commanderOracleText, metaSignals, metaStats);
        var phase2Raw = await _llm.ChatAsync(phase2System, phase2User, jsonMode: true, ct);
        _logger.LogInformation("Phase 2 response ({Len} chars)", phase2Raw.Length);

        var parsed2        = TryDeserializeLlmOutput(phase2Raw);
        var phase2Sections = BuildSectionsFromParsed(parsed2, priceMap);

        // Remove hallucinations and deduplicate against phase 1
        RemoveHallucinations(phase2Sections, priceMap);
        var phase1Names = new HashSet<string>(
            phase1Sections.SelectMany(s => s.Cards).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in phase2Sections)
            s.Cards.RemoveAll(c => phase1Names.Contains(c.Name));

        // Resolve commander
        string? resolvedCommander = req.Commander;
        if (string.IsNullOrWhiteSpace(resolvedCommander))
        {
            resolvedCommander = parsed2?.Commander;
            if (!string.IsNullOrWhiteSpace(resolvedCommander) && !priceMap.ContainsKey(resolvedCommander))
            {
                _logger.LogWarning("Phase 2 commander '{Cmd}' not in pool; falling back", resolvedCommander);
                resolvedCommander = null;
            }
        }
        resolvedCommander ??= phase2Sections
            .FirstOrDefault(s => s.Category.Equals("Commander", StringComparison.OrdinalIgnoreCase))
            ?.Cards.FirstOrDefault()?.Name;

        var allSections = new List<DeckSection>(phase1Sections);
        allSections.AddRange(phase2Sections);
        var reasoning = parsed2?.Reasoning ?? $"Two-phase Commander: mana base + {req.Theme} spell suite";

        return (allSections, resolvedCommander, reasoning);
    }

    // ─── Phase Prompt Builders ────────────────────────────────────────────────

    private static string BuildManaBaseUserPrompt(
        DeckRequest req,
        List<CardSearchResult> candidates,
        string? commanderOracleText,
        Dictionary<string, MetaSignal>? metaSignals)
    {
        var sb = new StringBuilder();
        var colors         = req.ColorIdentity?.Count > 0 ? string.Join(", ", req.ColorIdentity) : "all colors";
        var manaBaseBudget = req.Budget * 0.35;
        var perCardBudget  = req.Budget / 70.0;

        sb.AppendLine($"Build the mana base and ramp for: {req.Commander ?? req.Theme}");
        sb.AppendLine($"Colors: {colors}  |  Mana base budget: ~${manaBaseBudget:F2}  |  Max/card: ${perCardBudget * 3:F2}");
        if (commanderOracleText != null)
        {
            sb.AppendLine($"Commander ability: \"{commanderOracleText}\"");
            sb.AppendLine("Prefer ramp that synergizes with the commander ability.");
        }
        sb.AppendLine();
        sb.AppendLine("Select EXACTLY: 36-38 lands and 8-10 ramp spells.");
        sb.AppendLine();
        AppendCandidates(sb, candidates, perCardBudget, prioritizeLands: true, excludeLands: false, metaSignals: metaSignals);
        sb.AppendLine("CRITICAL: Choose only from this list. Basic lands are always available for free.");
        return sb.ToString();
    }

    private static string BuildSpellSuiteUserPrompt(
        DeckRequest req,
        List<CardSearchResult> candidates,
        List<DeckSection> phase1Sections,
        int remainingSlots,
        double remainingBudget,
        string? commanderOracleText,
        Dictionary<string, MetaSignal>? metaSignals,
        MetaSignalStats? metaStats)
    {
        var sb      = new StringBuilder();
        var colors  = req.ColorIdentity?.Count > 0 ? string.Join(", ", req.ColorIdentity) : "all colors";
        var perCard = remainingBudget / Math.Max(remainingSlots, 1);

        sb.AppendLine($"Commander: {req.Commander ?? "TBD"}  |  Theme: {req.Theme}  |  Colors: {colors}  |  Power: {req.PowerLevel}/10");
        sb.AppendLine($"Budget remaining: ${remainingBudget:F2}  |  Slots to fill: {remainingSlots}  |  Avg/card: ${perCard:F2}");
        if (commanderOracleText != null)
            sb.AppendLine($"Commander ability: \"{commanderOracleText}\"");
        AppendMetaSummary(sb, metaSignals, metaStats);
        sb.AppendLine();
        sb.AppendLine("Mana base committed — do NOT include these:");
        foreach (var section in phase1Sections)
        {
            var names = string.Join(", ", section.Cards.Take(5).Select(c => c.Name));
            var extra = section.Cards.Count > 5 ? $" +{section.Cards.Count - 5} more" : "";
            sb.AppendLine($"  [{section.Category}] {names}{extra}");
        }
        sb.AppendLine();
        sb.AppendLine($"Fill {remainingSlots} singleton slots (prioritize commander synergies):");
        sb.AppendLine("  8-10 removal | 8-10 card draw | 4-6 board wipes | 8-12 win conditions/payoffs | rest: synergy/utility");
        sb.AppendLine();

        var committed = new HashSet<string>(
            phase1Sections.SelectMany(s => s.Cards).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
        var spellPool = candidates
            .Where(c => !committed.Contains(c.Name) &&
                        !c.TypeLine.Contains("Land", StringComparison.OrdinalIgnoreCase))
            .ToList();

        AppendCandidates(sb, spellPool, perCard, prioritizeLands: false, excludeLands: true, metaSignals: metaSignals);
        sb.AppendLine("CRITICAL: No lands. All quantities must be 1. Choose only from the list above.");
        return sb.ToString();
    }

    // ─── Format Rules & Single-Shot Prompts ──────────────────────────────────

    private sealed record FormatRules(
        int DeckSize, bool Singleton, bool RequiresCommander,
        int MaxCopies, string LandCountRange,
        string[] IncludeGuidance, string[] Categories);

    private static FormatRules GetFormatRules(string format) => format.ToLowerInvariant() switch
    {
        "commander" => new FormatRules(
            100, true, true, 1, "36-38",
            ["~10 ramp spells", "~10 card draw effects", "~8-10 removal spells", "~5 board wipes", "36-38 lands"],
            ["Commander", "Ramp", "Card Draw", "Removal", "Board Wipes", "Win Conditions", "Synergy Pieces", "Utility", "Lands"]),
        "pauper" => new FormatRules(
            60, false, false, 4, "20-24",
            ["up to 4 copies per card", "COMMONS ONLY — no uncommons, rares, or mythics", "20-24 lands"],
            ["Creatures", "Spells", "Removal", "Win Conditions", "Utility", "Lands"]),
        _ => new FormatRules(
            60, false, false, 4, "22-26",
            ["up to 4 copies of each non-basic card", "22-26 lands", "balanced mana curve"],
            ["Creatures", "Spells", "Removal", "Win Conditions", "Utility", "Lands"])
    };

    private static string BuildSystemPrompt(string format, bool commanderNameProvided = false)
    {
        var rules = GetFormatRules(format);
        var fmt   = format.ToUpperInvariant();

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

        var commanderSelectionNote = (rules.RequiresCommander && !commanderNameProvided)
            ? "\n- Select an appropriate legendary creature as commander based on the theme and color identity."
            : "";

        var pauperNote = format.Equals("pauper", StringComparison.OrdinalIgnoreCase)
            ? "\n- CRITICAL: Pauper format. ALL cards must be common rarity. No uncommons, rares, or mythics."
            : "";

        var jsonSchema = rules.RequiresCommander
            ? """{"commander":"Name","reasoning":"...","sections":[{"category":"...","cards":[{"name":"...","quantity":1}]}]}"""
            : """{"reasoning":"...","sections":[{"category":"...","cards":[{"name":"...","quantity":1}]}]}""";

        return $"""
            You are an expert Magic: The Gathering deckbuilder specializing in {fmt} format.

            Format requirements:
            - Build a deck with exactly {rules.DeckSize} cards total
            {commanderRules}{commanderSelectionNote}{pauperNote}
            - Balance the mana curve appropriately
            - Include: {string.Join(", ", rules.IncludeGuidance)}
            - Stay within the specified budget

            Categories should be: {string.Join(", ", rules.Categories)}
            Respond ONLY with valid JSON: {jsonSchema}
            """;
    }

    private static string BuildUserPrompt(
        DeckRequest req,
        List<CardSearchResult> candidates,
        string? commanderOracleText = null,
        Dictionary<string, MetaSignal>? metaSignals = null,
        MetaSignalStats? metaStats = null)
    {
        var sb            = new StringBuilder();
        var isCommander   = req.Format.Equals("commander", StringComparison.OrdinalIgnoreCase);
        var spellSlots    = isCommander ? 70.0 : 36.0;
        var perCardBudget = req.Budget / spellSlots;

        sb.AppendLine($"Build a {req.Format.ToUpperInvariant()} deck:");
        if (isCommander && req.Commander != null)
        {
            sb.AppendLine($"- Commander: {req.Commander}");
            if (commanderOracleText != null)
            {
                sb.AppendLine($"- Commander ability: \"{commanderOracleText}\"");
                sb.AppendLine("  Prioritize cards that synergize with this ability.");
            }
        }
        else if (isCommander)
            sb.AppendLine("- Choose an appropriate commander for the theme and color identity.");

        sb.AppendLine($"- Theme/Strategy: {req.Theme}");
        if (req.ColorIdentity?.Count > 0)
            sb.AppendLine($"- Color Identity: {string.Join(", ", req.ColorIdentity)}");
        sb.AppendLine($"- Budget: ${req.Budget:F2} HARD LIMIT");
        sb.AppendLine($"- Max price per card: ${perCardBudget * 4:F2} (avg target: ${perCardBudget:F2})");
        sb.AppendLine($"- Power Level: {req.PowerLevel}/10");
        if (!string.IsNullOrWhiteSpace(req.ExtraContext))
            sb.AppendLine($"- Notes: {req.ExtraContext}");
        AppendMetaSummary(sb, metaSignals, metaStats);
        sb.AppendLine();

        AppendCandidates(sb, candidates, perCardBudget, prioritizeLands: false, excludeLands: false, metaSignals: metaSignals);
        sb.AppendLine($"CRITICAL: Choose only from the list. Basic lands are free. Total ≤ ${req.Budget:F2}.");
        return sb.ToString();
    }

    // ─── Candidate Display ────────────────────────────────────────────────────

    private static void AppendCandidates(
        StringBuilder sb,
        List<CardSearchResult> candidates,
        double perCardBudget,
        bool prioritizeLands,
        bool excludeLands,
        Dictionary<string, MetaSignal>? metaSignals = null)
    {
        var hasMeta = metaSignals is { Count: > 0 };
        if (perCardBudget > 0)
        {
            var metaLegend = hasMeta ? ", 🏆/★/· = tournament meta tier" : "";
            sb.AppendLine($"Available cards — budget target ~${perCardBudget:F2}/card (cards marked ⚠ exceed 3× target{metaLegend}):");
        }
        else
        {
            sb.AppendLine("Available cards (choose ONLY from this list):");
        }
        sb.AppendLine();

        var grouped = candidates
            .Take(150)
            .GroupBy(c => GuessCategory(c.TypeLine))
            .OrderBy(g => g.Key == "Lands" ? (prioritizeLands ? 0 : 99) : (prioritizeLands ? 1 : 0))
            .ThenBy(g => g.Key);

        foreach (var group in grouped)
        {
            if (excludeLands && group.Key == "Lands") continue;
            sb.AppendLine($"[{group.Key}]");

            // Order by meta inclusion rate (desc) first, then by price. This
            // surfaces tournament-proven cards at the top of each category.
            var ordered = group
                .OrderByDescending(c => MetaRate(metaSignals, c.Name))
                .ThenBy(c => c.PriceUsd)
                .Take(30);

            foreach (var card in ordered)
            {
                var price     = card.PriceUsd > 0 ? $"${card.PriceUsd:F2}" : "free";
                var overBudget = perCardBudget > 0 && card.PriceUsd > perCardBudget * 3 ? " ⚠" : "";
                var text      = card.OracleText?.Length > 200
                    ? card.OracleText[..200] + "..."
                    : card.OracleText ?? "";

                var metaPrefix = "";
                if (metaSignals != null && metaSignals.TryGetValue(card.Name, out var sig))
                {
                    var tier = MetaSignalService.TierFor(sig.InclusionRate);
                    if (tier.Length > 0)
                        metaPrefix = $"{tier} {sig.InclusionRate * 100:F0}% ";
                }

                sb.AppendLine($"  - {metaPrefix}{card.Name} ({card.ManaCost}) [{price}{overBudget}]: {text}");
            }
            sb.AppendLine();
        }
    }

    private static double MetaRate(Dictionary<string, MetaSignal>? signals, string name)
        => signals != null && signals.TryGetValue(name, out var s) ? s.InclusionRate : 0.0;

    private static void AppendMetaSummary(
        StringBuilder sb,
        Dictionary<string, MetaSignal>? metaSignals,
        MetaSignalStats? metaStats)
    {
        if (metaSignals == null || metaSignals.Count == 0)
            return;

        var hits = metaSignals
            .Values
            .OrderByDescending(s => s.InclusionRate)
            .Take(10)
            .ToList();
        if (hits.Count == 0) return;

        var sample = metaStats?.SampleSize ?? hits[0].SampleSize;
        sb.AppendLine($"- Tournament meta (sample: {sample} decks) — {metaSignals.Count} candidates are proven in recent tournaments.");
        var topLine = string.Join(", ",
            hits.Take(6).Select(s => $"{s.CardName} {s.InclusionRate * 100:F0}%"));
        sb.AppendLine($"  Top meta cards in this pool: {topLine}.");
        sb.AppendLine("  Prefer these when they fit the strategy, but don't force them in at the expense of synergy.");
    }

    // ─── Parse Helpers ────────────────────────────────────────────────────────

    private (List<DeckSection>, string?, string) ParseDeckOutput(
        string raw,
        DeckRequest req,
        Dictionary<string, CardSearchResult> priceMap)
    {
        var parsed   = TryDeserializeLlmOutput(raw);
        var sections = BuildSectionsFromParsed(parsed, priceMap);
        RemoveHallucinations(sections, priceMap);

        if (string.IsNullOrWhiteSpace(req.Commander) &&
            !string.IsNullOrWhiteSpace(parsed?.Commander) &&
            !priceMap.ContainsKey(parsed.Commander))
        {
            _logger.LogWarning("LLM commander '{Cmd}' not in pool; falling back", parsed.Commander);
            parsed!.Commander = null;
        }

        string? resolvedCommander = req.Commander;
        if (string.IsNullOrWhiteSpace(resolvedCommander))
            resolvedCommander = parsed?.Commander;
        if (string.IsNullOrWhiteSpace(resolvedCommander))
            resolvedCommander = sections
                .FirstOrDefault(s => s.Category.Equals("Commander", StringComparison.OrdinalIgnoreCase))
                ?.Cards.FirstOrDefault()?.Name;

        return (sections, resolvedCommander, parsed?.Reasoning ?? raw);
    }

    private LlmDeckOutput? TryDeserializeLlmOutput(string raw)
    {
        var json  = raw.Replace("```json", "").Replace("```", "").Trim();
        var start = json.IndexOf('{');
        var end   = json.LastIndexOf('}');
        if (start >= 0 && end > start)
            json = json[start..(end + 1)];

        try
        {
            return JsonSerializer.Deserialize<LlmDeckOutput>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize LLM output — falling back to empty deck. Payload (truncated): {Payload}",
                raw.Length > 500 ? raw[..500] + "…" : raw);
            return null;
        }
    }

    private List<DeckSection> ParseRawToSections(
        string raw, Dictionary<string, CardSearchResult> priceMap) =>
        BuildSectionsFromParsed(TryDeserializeLlmOutput(raw), priceMap);

    private static List<DeckSection> BuildSectionsFromParsed(
        LlmDeckOutput? parsed,
        Dictionary<string, CardSearchResult> priceMap) =>
        parsed?.Sections?.Select(s => new DeckSection(
            Category: s.Category ?? "Uncategorized",
            Cards: s.Cards?.Select(c =>
            {
                priceMap.TryGetValue(c.Name ?? "", out var info);
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

    private void RemoveHallucinations(
        List<DeckSection> sections,
        Dictionary<string, CardSearchResult> priceMap)
    {
        foreach (var section in sections)
        {
            var before = section.Cards.Count;
            section.Cards.RemoveAll(c =>
                !MtgConstants.BasicLandNames.Contains(c.Name) && !priceMap.ContainsKey(c.Name));
            var removed = before - section.Cards.Count;
            if (removed > 0)
                _logger.LogWarning("Section '{Cat}': removed {N} hallucinated card(s)",
                    section.Category, removed);
        }
    }

    // ─── Category & Functional Role ───────────────────────────────────────────

    private static string GuessCategory(string? typeLine)
    {
        if (typeLine is null)                          return "Other";
        if (typeLine.Contains("Land"))                 return "Lands";
        if (typeLine.Contains("Creature"))             return "Creatures";
        if (typeLine.Contains("Planeswalker"))         return "Planeswalkers";
        if (typeLine.Contains("Instant"))              return "Instants";
        if (typeLine.Contains("Sorcery"))              return "Sorceries";
        if (typeLine.Contains("Enchantment"))          return "Enchantments";
        if (typeLine.Contains("Artifact"))             return "Artifacts";
        return "Other";
    }

    /// <summary>
    /// Infer the functional deck role from oracle text, enabling smarter budget swaps.
    /// Falls back to GuessCategory (card type) when oracle text provides no signal.
    /// </summary>
    private static string GetFunctionalRole(string? oracleText, string? typeLine)
    {
        if (oracleText is null) return GuessCategory(typeLine);
        var t = oracleText.ToLowerInvariant();

        if (t.Contains("add {") || t.Contains("add one mana") ||
            t.Contains("search your library for a basic land") ||
            t.Contains("search your library for up to") ||
            t.Contains("put a land") || t.Contains("untap up to"))
            return "Ramp";

        if (t.Contains("draw a card") || t.Contains("draw two cards") ||
            t.Contains("draw three cards") || t.Contains("draw x cards") ||
            t.Contains("draw cards equal") || t.Contains("look at the top"))
            return "Card Draw";

        if (t.Contains("destroy target") || t.Contains("exile target") ||
            t.Contains("counter target") ||
            (t.Contains("return target") && t.Contains("owner")))
            return "Removal";

        if (t.Contains("destroy all") || t.Contains("exile all creatures") ||
            t.Contains("each player sacrifices") || t.Contains("return all"))
            return "Board Wipe";

        return GuessCategory(typeLine);
    }

    // ─── Budget Enforcement ───────────────────────────────────────────────────

    private List<DeckSection> EnforceBudget(
        List<DeckSection> sections,
        double budget,
        Dictionary<string, CardSearchResult> priceMap)
    {
        var skipped = new HashSet<string>(["Lands", "Commander"], StringComparer.OrdinalIgnoreCase);
        var inDeck  = new HashSet<string>(
            sections.SelectMany(s => s.Cards).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        var cheapPool = priceMap.Values
            .Where(c => !inDeck.Contains(c.Name))
            .OrderBy(c => c.PriceUsd)
            .ToList();

        var replaceable = sections
            .Where(s => !skipped.Contains(s.Category))
            .SelectMany(s => s.Cards.Select(c => (Section: s, Card: c)))
            .Where(x => x.Card.PriceUsd > 0)
            .OrderByDescending(x => x.Card.PriceUsd * x.Card.Quantity)
            .ToList();

        var totalCost = sections.SelectMany(s => s.Cards).Sum(c => c.PriceUsd * c.Quantity);

        foreach (var (section, expensive) in replaceable)
        {
            if (totalCost <= budget) break;

            var idx = section.Cards.FindIndex(
                c => c.Name.Equals(expensive.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;

            var role = GetFunctionalRole(expensive.OracleText, expensive.TypeLine);

            var replacement = cheapPool
                .Where(c => c.PriceUsd < expensive.PriceUsd)
                .OrderByDescending(c => GetFunctionalRole(c.OracleText, c.TypeLine) == role)
                .ThenBy(c => c.PriceUsd)
                .FirstOrDefault();

            totalCost -= expensive.PriceUsd * expensive.Quantity;
            inDeck.Remove(expensive.Name);

            if (replacement != null)
            {
                section.Cards[idx] = new DeckCard(
                    Name:        replacement.Name,
                    Quantity:    expensive.Quantity,
                    PriceUsd:    replacement.PriceUsd,
                    OracleText:  replacement.OracleText,
                    ImageUri:    replacement.ImageUri,
                    ScryfallUri: replacement.ScryfallUri,
                    ManaCost:    replacement.ManaCost,
                    Cmc:         replacement.Cmc,
                    TypeLine:    replacement.TypeLine
                );
                totalCost += replacement.PriceUsd * expensive.Quantity;
                inDeck.Add(replacement.Name);
                cheapPool.Remove(replacement);
                _logger.LogDebug("Budget: replaced '{E}' (${EP:F2}) → '{R}' (${RP:F2})",
                    expensive.Name, expensive.PriceUsd, replacement.Name, replacement.PriceUsd);
            }
            else
            {
                section.Cards.RemoveAt(idx);
                _logger.LogDebug("Budget: removed '{E}' (${EP:F2}) — no cheaper option",
                    expensive.Name, expensive.PriceUsd);
            }
        }

        return sections;
    }

    // ─── LLM Output DTOs ─────────────────────────────────────────────────────

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
