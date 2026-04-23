using Microsoft.AspNetCore.Mvc;
using MtgForgeAi.Models;
using MtgForgeAi.Services;

namespace MtgForgeAi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DecksController : ControllerBase
{
    private readonly DeckGenerationService _generator;
    private readonly MongoService _mongo;

    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase)
        { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" };

    public DecksController(DeckGenerationService generator, MongoService mongo)
    {
        _generator = generator;
        _mongo = mongo;
    }

    /// <summary>Generate a new deck</summary>
    [HttpPost("generate")]
    public async Task<ActionResult<DeckResponse>> Generate(
        [FromBody] DeckRequest req,
        CancellationToken ct)
    {
        var format = req.Format?.Trim().ToLowerInvariant() ?? "";
        if (!ValidFormats.Contains(format))
            return BadRequest($"Invalid format '{req.Format}'. Valid formats: {string.Join(", ", ValidFormats)}");

        var deck = await _generator.GenerateDeckAsync(req, ct);
        return Ok(deck);
    }

    /// <summary>Get all saved decks</summary>
    [HttpGet]
    public async Task<ActionResult<List<SavedDeck>>> GetAll(CancellationToken ct)
    {
        var decks = await _mongo.GetAllDecksAsync(ct);
        return Ok(decks);
    }

    /// <summary>Get a specific saved deck</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<SavedDeck>> GetById(string id, CancellationToken ct)
    {
        var deck = await _mongo.GetDeckByIdAsync(id, ct);
        return deck is null ? NotFound() : Ok(deck);
    }

    /// <summary>Delete a saved deck</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var deleted = await _mongo.DeleteDeckAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}

[ApiController]
[Route("api/[controller]")]
public class CardsController : ControllerBase
{
    private readonly CardSearchService _search;

    public CardsController(CardSearchService search) => _search = search;

    /// <summary>Semantic card search</summary>
    [HttpPost("search")]
    public async Task<ActionResult<List<CardSearchResult>>> Search(
        [FromBody] CardSearchRequest req,
        CancellationToken ct)
    {
        var results = await _search.SearchAsync(req, ct);
        return Ok(results);
    }
}

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly ILlmService _llm;

    public HealthController(ILlmService llm) => _llm = llm;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var llmOk = await _llm.IsHealthyAsync(ct);
        return Ok(new
        {
            status   = llmOk ? "healthy" : "degraded",
            llm      = llmOk ? "ok" : "unavailable",
            mongodb  = "ok",
            qdrant   = "ok",
            timestamp = DateTime.UtcNow
        });
    }
}

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly CardIngestionService _ingestion;
    private readonly MetaSignalService _meta;
    private readonly ILogger<AdminController> _logger;

    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase)
        { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" };

    public AdminController(
        CardIngestionService ingestion,
        MetaSignalService meta,
        ILogger<AdminController> logger)
    {
        _ingestion = ingestion;
        _meta      = meta;
        _logger    = logger;
    }

    /// <summary>
    /// Ingest cards from Scryfall into MongoDB and Qdrant.
    /// This is a long-running operation (5-30+ minutes depending on card count and embedding speed).
    /// </summary>
    [HttpPost("ingest")]
    public async Task<ActionResult<IngestionResult>> Ingest(
        [FromBody] IngestionRequest? req,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Starting card ingestion (mongoOnly={MongoOnly}, qdrantOnly={QdrantOnly}, limit={Limit})",
            req?.MongoOnly ?? false, req?.QdrantOnly ?? false, req?.Limit);

        try
        {
            var result = await _ingestion.IngestAsync(
                mongoOnly: req?.MongoOnly ?? false,
                qdrantOnly: req?.QdrantOnly ?? false,
                limit: req?.Limit,
                ct: ct);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Card ingestion failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Inspect the tournament meta signals currently stored for a format.
    /// Populated by scripts/compute_meta_signals.py (external data feed — MTGTop8).
    /// </summary>
    [HttpGet("meta")]
    public async Task<ActionResult<MetaSignalsResponse>> GetMetaSignals(
        [FromQuery] string format,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        var fmt = format?.Trim().ToLowerInvariant() ?? "";
        if (!ValidFormats.Contains(fmt))
            return BadRequest($"Invalid format '{format}'. Valid formats: {string.Join(", ", ValidFormats)}");

        if (limit <= 0) limit = 50;
        if (limit > 500) limit = 500;

        var (signals, stats) = await _meta.GetTopAsync(fmt, limit, ct);
        return Ok(new MetaSignalsResponse(fmt, stats, signals));
    }
}
