using MongoDB.Driver;
using MtgForgeLocal.Models;

namespace MtgForgeLocal.Services;

public class MongoService
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

    public async Task<List<MtgCard>> SearchCardsByColorAsync(
        List<string> colorIdentity,
        CancellationToken ct = default)
    {
        var col = _db.GetCollection<MtgCard>("cards");
        var filter = Builders<MtgCard>.Filter.All(c => c.ColorIdentity, colorIdentity);
        return await col.Find(filter).Limit(500).ToListAsync(ct);
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

    // ─── Card Ingestion ───────────────────────────────────────────────────────

    private bool _indexesEnsured;

    /// <summary>Ensures card indexes exist. Called once per application lifetime.</summary>
    private async Task EnsureCardIndexesAsync(CancellationToken ct)
    {
        if (_indexesEnsured) return;

        var collection = _db.GetCollection<MtgCard>("cards");
        var indexKeys = Builders<MtgCard>.IndexKeys;
        await collection.Indexes.CreateManyAsync([
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.ScryfallId),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.Name)),
            new CreateIndexModel<MtgCard>(indexKeys.Ascending(c => c.ColorIdentity))
        ], ct);

        _indexesEnsured = true;
    }

    public async Task<int> BulkUpsertCardsAsync(List<MtgCard> cards, CancellationToken ct = default)
    {
        var collection = _db.GetCollection<MtgCard>("cards");

        // Ensure indexes (idempotent — MongoDB skips existing indexes)
        await EnsureCardIndexesAsync(ct);

        int upserted = 0;
        const int batchSize = 50;
        for (int i = 0; i < cards.Count; i += batchSize)
        {
            var batch = cards.Skip(i).Take(batchSize).ToList();
            var ops = batch.Select(card =>
                new ReplaceOneModel<MtgCard>(
                    Builders<MtgCard>.Filter.Eq(c => c.ScryfallId, card.ScryfallId),
                    card)
                { IsUpsert = true }
            ).ToList();

            try
            {
                var result = await collection.BulkWriteAsync(ops, cancellationToken: ct);
                upserted += (int)(result.Upserts.Count + result.ModifiedCount);
            }
            catch (MongoBulkWriteException<MtgCard> ex)
            {
                _logger.LogWarning("MongoDB batch write warning: {Message}", ex.Message);
                upserted += batch.Count;
            }
        }

        return upserted;
    }
}
