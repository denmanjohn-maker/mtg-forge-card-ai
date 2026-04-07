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
}
