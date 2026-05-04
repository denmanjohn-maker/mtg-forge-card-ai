using MongoDB.Bson;
using MongoDB.Driver;
using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

public class MongoService : IMetaSignalRepository
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<MongoService> _logger;

    public MongoService(IConfiguration config, ILogger<MongoService> logger)
    {
        _logger = logger;
        var connStr = config["MongoDB:ConnectionString"]
            ?? "mongodb://admin:password@localhost:27017";
        var dbName  = config["MongoDB:DatabaseName"] ?? "mtgforge";

        var client = new MongoClient(connStr);
        _db = client.GetDatabase(dbName);
    }

    // ─── Cards ────────────────────────────────────────────────────────────────

    public async Task<MtgCard?> GetCardByNameAsync(string name, CancellationToken ct = default)
    {
        var col = _db.GetCollection<MtgCard>("cards");
        return await col
            .Find(c => c.Name == name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<MtgCard>> GetCardsByNamesAsync(
        IEnumerable<string> names, CancellationToken ct = default)
    {
        var distinct = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return new List<MtgCard>();

        var col    = _db.GetCollection<MtgCard>("cards");
        var filter = Builders<MtgCard>.Filter.In(c => c.Name, distinct);
        return await col.Find(filter).ToListAsync(ct);
    }

    public async Task<List<MtgCard>> SearchCardsByColorAsync(
        List<string> colorIdentity,
        CancellationToken ct = default)
    {
        var col = _db.GetCollection<MtgCard>("cards");
        var filter = Builders<MtgCard>.Filter.All(c => c.ColorIdentity, colorIdentity);
        return await col.Find(filter).Limit(500).ToListAsync(ct);
    }

    /// <summary>
    /// Returns cards whose <c>set_name</c> contains any of the given substrings
    /// (case-insensitive). Used by themed-set enrichment to pull cards from
    /// Universes Beyond products without needing exact set names. Empty input
    /// returns an empty list.
    /// </summary>
    public async Task<List<MtgCard>> GetCardsBySetNameSubstringsAsync(
        IEnumerable<string> substrings,
        int limit,
        CancellationToken ct = default)
    {
        var terms = substrings
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0) return new List<MtgCard>();

        var col       = _db.GetCollection<MtgCard>("cards");
        var orFilters = terms.Select(t =>
            Builders<MtgCard>.Filter.Regex(
                c => c.SetName,
                new MongoDB.Bson.BsonRegularExpression(
                    System.Text.RegularExpressions.Regex.Escape(t), "i")));
        var filter = Builders<MtgCard>.Filter.Or(orFilters);
        return await col.Find(filter).Limit(limit).ToListAsync(ct);
    }

    // ─── Decks ────────────────────────────────────────────────────────────────

    public async Task SaveDeckAsync(SavedDeck deck, CancellationToken ct = default)
    {
        var col = _db.GetCollection<SavedDeck>("decks");
        await col.InsertOneAsync(deck, cancellationToken: ct);
        _logger.LogInformation("Saved deck '{Commander}' with id {Id}", deck.Commander, deck.Id);
    }

    public async Task<List<SavedDeck>> GetAllDecksAsync(CancellationToken ct = default)
    {
        var col = _db.GetCollection<SavedDeck>("decks");
        return await col
            .Find(_ => true)
            .SortByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<SavedDeck?> GetDeckByIdAsync(string id, CancellationToken ct = default)
    {
        var col = _db.GetCollection<SavedDeck>("decks");
        return await col.Find(d => d.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> DeleteDeckAsync(string id, CancellationToken ct = default)
    {
        var col = _db.GetCollection<SavedDeck>("decks");
        var result = await col.DeleteOneAsync(d => d.Id == id, ct);
        return result.DeletedCount > 0;
    }

    // ─── Tournament Meta Signals ──────────────────────────────────────────────

    private IMongoCollection<MetaSignal>      MetaSignals      => _db.GetCollection<MetaSignal>("meta_signals");
    private IMongoCollection<MetaSignalStats> MetaSignalsStats => _db.GetCollection<MetaSignalStats>("meta_signal_stats");

    /// <summary>
    /// Returns the top N meta signals for a format, ordered by inclusion rate.
    /// Safe when the collection is missing or empty — returns an empty list.
    /// </summary>
    public async Task<List<MetaSignal>> GetTopMetaSignalsAsync(
        string format, int limit = 100, CancellationToken ct = default)
    {
        var filter = Builders<MetaSignal>.Filter.Eq(s => s.Format, format.ToLowerInvariant());
        var sort   = Builders<MetaSignal>.Sort.Descending(s => s.InclusionRate);

        try
        {
            return await MetaSignals.Find(filter).Sort(sort).Limit(limit).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read meta_signals for {Format}", format);
            return new List<MetaSignal>();
        }
    }

    /// <summary>
    /// Returns meta signals for an explicit set of card names (case-insensitive match on card_name_lc).
    /// Used to enrich existing candidates without fetching the full top-N list.
    /// </summary>
    public async Task<Dictionary<string, MetaSignal>> GetMetaSignalsForCardsAsync(
        string format, IEnumerable<string> cardNames, CancellationToken ct = default)
    {
        var normalized = cardNames
            .Select(n => n?.Trim().ToLowerInvariant() ?? "")
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
            return new Dictionary<string, MetaSignal>(StringComparer.OrdinalIgnoreCase);

        var filter = Builders<MetaSignal>.Filter.And(
            Builders<MetaSignal>.Filter.Eq(s => s.Format, format.ToLowerInvariant()),
            Builders<MetaSignal>.Filter.In(s => s.CardNameLc, normalized)
        );

        try
        {
            var docs = await MetaSignals.Find(filter).ToListAsync(ct);
            return docs.ToDictionary(d => d.CardName, d => d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read meta_signals subset for {Format}", format);
            return new Dictionary<string, MetaSignal>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<MetaSignalStats?> GetMetaSignalStatsAsync(string format, CancellationToken ct = default)
    {
        try
        {
            return await MetaSignalsStats
                .Find(s => s.Format == format.ToLowerInvariant())
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read meta_signal_stats for {Format}", format);
            return null;
        }
    }

    // ─── Card Ingestion ───────────────────────────────────────────────────────

    private bool _indexesEnsured;

    /// <summary>Ensures card indexes exist. Called once per application lifetime.</summary>
    private async Task EnsureCardIndexesAsync(CancellationToken ct)
    {
        if (_indexesEnsured) return;

        var collection = _db.GetCollection<MtgCard>("cards");
        var indexKeys = Builders<MtgCard>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.Name)),
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.ColorIdentity)),
            // Supports themed-set lookups in GetCardsBySetNameSubstringsAsync.
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.SetName))
        ], ct);

        _indexesEnsured = true;
    }

    public async Task<long> GetCardCountAsync(CancellationToken ct = default)
    {
        var col = _db.GetCollection<MtgCard>("cards");
        return await col.CountDocumentsAsync(FilterDefinition<MtgCard>.Empty, cancellationToken: ct);
    }

    public async Task<int> BulkUpsertCardsAsync(List<MtgCard> cards, CancellationToken ct = default)
    {
        // Use BsonDocument collection for writes so we can explicitly stamp _id = ScryfallId,
        // bypassing any BSON class-map ambiguity that previously produced _id:null on upsert.
        var rawCollection = _db.GetCollection<BsonDocument>("cards");

        // Ensure indexes (idempotent — MongoDB skips existing indexes)
        await EnsureCardIndexesAsync(ct);

        int upserted = 0;
        const int batchSize = 50;
        for (int i = 0; i < cards.Count; i += batchSize)
        {
            var batch = cards.Skip(i).Take(batchSize).ToList();

            var ops = new List<WriteModel<BsonDocument>>(batch.Count);
            foreach (var card in batch)
            {
                if (string.IsNullOrEmpty(card.ScryfallId))
                {
                    _logger.LogWarning("Skipping card '{Name}': null/empty ScryfallId reached upsert", card.Name);
                    continue;
                }

                var doc = card.ToBsonDocument();
                // Unconditionally overwrite _id with the ScryfallId string so the BSON
                // class-map's own serialization of [BsonId] cannot interfere.
                doc["_id"] = new BsonString(card.ScryfallId);

                ops.Add(new ReplaceOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq("_id", card.ScryfallId),
                    doc)
                { IsUpsert = true });
            }

            if (ops.Count == 0) continue;

            try
            {
                // ordered=false: a single failing op does not abort the rest of the batch.
                var result = await rawCollection.BulkWriteAsync(
                    ops,
                    new BulkWriteOptions { IsOrdered = false },
                    cancellationToken: ct);
                upserted += (int)(result.Upserts.Count + result.MatchedCount);
            }
            catch (MongoBulkWriteException<BsonDocument> ex)
            {
                _logger.LogWarning(
                    "MongoDB batch write partial failure: {WriteErrors} of {Total} ops failed: {Message}",
                    ex.WriteErrors.Count, ops.Count, ex.Message);
                upserted += (int)(ex.Result.Upserts.Count + ex.Result.MatchedCount);
            }
        }

        return upserted;
    }
}
