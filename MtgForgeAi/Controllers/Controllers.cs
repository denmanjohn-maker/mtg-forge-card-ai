using Microsoft.AspNetCore.Mvc;
using MtgForgeAi.Models;
using MtgForgeAi.Services;
using Qdrant.Client;

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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly MetaSignalService _meta;
    private readonly IngestionStatusService _ingestionStatus;
    private readonly MongoService _mongo;
    private readonly QdrantClient _qdrant;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminController> _logger;

    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase)
        { "commander", "standard", "modern", "legacy", "pioneer", "pauper", "vintage" };

    public AdminController(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime appLifetime,
        MetaSignalService meta,
        IngestionStatusService ingestionStatus,
        MongoService mongo,
        QdrantClient qdrant,
        IConfiguration config,
        ILogger<AdminController> logger)
    {
        _scopeFactory    = scopeFactory;
        _appLifetime     = appLifetime;
        _meta            = meta;
        _ingestionStatus = ingestionStatus;
        _mongo           = mongo;
        _qdrant          = qdrant;
        _config          = config;
        _logger          = logger;
    }

    /// <summary>
    /// Starts card ingestion from Scryfall into MongoDB and Qdrant in the background.
    /// Returns 202 Accepted immediately. Ingestion is a long-running operation
    /// (5-30+ minutes) and is decoupled from the HTTP connection so that client
    /// disconnects or proxy timeouts do not abort it mid-run.
    /// </summary>
    [HttpPost("ingest")]
    public IActionResult Ingest([FromBody] IngestionRequest? req)
    {
        var mongoOnly  = req?.MongoOnly  ?? false;
        var qdrantOnly = req?.QdrantOnly ?? false;
        var limit      = req?.Limit;

        _logger.LogInformation(
            "Queuing card ingestion (mongoOnly={MongoOnly}, qdrantOnly={QdrantOnly}, limit={Limit})",
            mongoOnly, qdrantOnly, limit);

        _ingestionStatus.MarkStarted();

        // Run ingestion in the background, independent of the HTTP request lifetime.
        // Use the application-stopping token so the job is cancelled cleanly on shutdown,
        // but will not be interrupted by a client disconnect or proxy timeout.
        var appStopping = _appLifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var ingestion   = scope.ServiceProvider.GetRequiredService<CardIngestionService>();
            try
            {
                var result = await ingestion.IngestAsync(mongoOnly, qdrantOnly, limit, appStopping);
                _ingestionStatus.MarkCompleted(result);
                _logger.LogInformation(
                    "Ingestion complete: {Downloaded} downloaded, {Mongo} mongo, {Qdrant} qdrant, {Elapsed:F1}s",
                    result.TotalCardsDownloaded, result.MongoCardsUpserted,
                    result.QdrantVectorsUpserted, result.ElapsedSeconds);
            }
            catch (OperationCanceledException)
            {
                _ingestionStatus.MarkFailed();
                _logger.LogWarning("Card ingestion cancelled (application shutting down)");
            }
            catch (Exception ex)
            {
                _ingestionStatus.MarkFailed();
                _logger.LogError(ex, "Card ingestion failed");
            }
        }, appStopping);

        return Accepted(new { message = "Ingestion started in the background. Check application logs for progress and results." });
    }

    /// <summary>
    /// Returns the current state of the card ingestion pipeline:
    /// whether a job is running, last-run timestamps and stats,
    /// live embedding progress, MongoDB card count, and Qdrant vector count.
    /// Also accessible via GET /api/admin/ingest for backwards compatibility.
    /// </summary>
    [HttpGet("ingest")]
    [HttpGet("ingest-status")]
    public async Task<ActionResult<IngestionStatusResponse>> IngestStatus(CancellationToken ct)
    {
        var (isRunning, lastStartedAt, lastCompletedAt, lastResult, cardsEmbedded, totalToEmbed)
            = _ingestionStatus.Snapshot();

        long mongoCount  = 0;
        long qdrantCount = 0;

        try { mongoCount  = await _mongo.GetCardCountAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not fetch MongoDB card count"); }

        try
        {
            var info  = await _qdrant.GetCollectionInfoAsync("mtg_cards", ct);
            qdrantCount = (long)info.VectorsCount;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not fetch Qdrant vector count"); }

        var embedModel = _config["Ollama:EmbedModel"] ?? "all-minilm";

        return Ok(new IngestionStatusResponse(
            IsRunning:        isRunning,
            LastStartedAt:    lastStartedAt,
            LastCompletedAt:  lastCompletedAt,
            LastResult:       lastResult,
            CurrentProgress:  new IngestionProgress(cardsEmbedded, totalToEmbed),
            MongoCardCount:   mongoCount,
            QdrantVectorCount: qdrantCount,
            EmbedModel:       embedModel
        ));
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
