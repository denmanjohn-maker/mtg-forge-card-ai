using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Reads tournament meta signals from MongoDB and surfaces them to the deck
/// generation pipeline as lightweight annotations. The external data feed
/// (MTGTop8 via scripts/compute_meta_signals.py) is treated as optional —
/// when the collection is missing or empty, every method degrades cleanly.
///
/// A short per-format cache avoids hammering MongoDB when several deck
/// generations run back to back.
/// </summary>
public class MetaSignalService
{
    private readonly MongoService _mongo;
    private readonly ILogger<MetaSignalService> _logger;

    // Per-format cache: (expiresAt, topSignalsByName)
    private readonly Dictionary<string, (DateTime ExpiresAt, List<MetaSignal> Signals, MetaSignalStats? Stats)> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const int DefaultCacheLimit = 500;

    public MetaSignalService(MongoService mongo, ILogger<MetaSignalService> logger)
    {
        _mongo  = mongo;
        _logger = logger;
    }

    /// <summary>
    /// Returns top meta signals for a format. Cached for <see cref="CacheTtl"/>.
    /// </summary>
    public async Task<(List<MetaSignal> Signals, MetaSignalStats? Stats)> GetTopAsync(
        string format, int limit = 200, CancellationToken ct = default)
    {
        format = format.ToLowerInvariant();
        var (signals, stats) = await GetOrLoadAsync(format, ct);
        return (signals.Take(limit).ToList(), stats);
    }

    /// <summary>
    /// Builds a lookup of {cardName → MetaSignal} for the given candidate names.
    /// Returns an empty dictionary when no data is available for the format.
    /// </summary>
    public async Task<Dictionary<string, MetaSignal>> GetSignalsForCardsAsync(
        string format, IEnumerable<string> cardNames, CancellationToken ct = default)
    {
        format = format.ToLowerInvariant();
        var (signals, _) = await GetOrLoadAsync(format, ct);
        if (signals.Count == 0)
            return new Dictionary<string, MetaSignal>(StringComparer.OrdinalIgnoreCase);

        var want = new HashSet<string>(cardNames, StringComparer.OrdinalIgnoreCase);
        return signals
            .Where(s => want.Contains(s.CardName))
            .ToDictionary(s => s.CardName, s => s, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps a card's inclusion rate to a small display tier used in prompts.
    /// 🏆 ≥ 40%, ★ ≥ 20%, · ≥ 10%, otherwise empty (no annotation).
    /// </summary>
    public static string TierFor(double inclusionRate) => inclusionRate switch
    {
        >= 0.40 => "🏆",
        >= 0.20 => "★",
        >= 0.10 => "·",
        _       => ""
    };

    /// <summary>
    /// Quick check: does this format have usable meta signals?
    /// </summary>
    public async Task<bool> IsAvailableAsync(string format, CancellationToken ct = default)
    {
        var (signals, _) = await GetOrLoadAsync(format.ToLowerInvariant(), ct);
        return signals.Count > 0;
    }

    // ─── Internal cache ──────────────────────────────────────────────────────

    private async Task<(List<MetaSignal> Signals, MetaSignalStats? Stats)> GetOrLoadAsync(
        string format, CancellationToken ct)
    {
        if (_cache.TryGetValue(format, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return (cached.Signals, cached.Stats);

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(format, out cached) && cached.ExpiresAt > DateTime.UtcNow)
                return (cached.Signals, cached.Stats);

            var signalsTask = _mongo.GetTopMetaSignalsAsync(format, DefaultCacheLimit, ct);
            var statsTask   = _mongo.GetMetaSignalStatsAsync(format, ct);
            await Task.WhenAll(signalsTask, statsTask);

            var signals = signalsTask.Result;
            var stats   = statsTask.Result;

            _cache[format] = (DateTime.UtcNow + CacheTtl, signals, stats);

            if (signals.Count > 0)
                _logger.LogInformation(
                    "Meta signals loaded for {Format}: {Count} cards (sample_size={Sample})",
                    format, signals.Count, stats?.SampleSize ?? 0);
            else
                _logger.LogDebug("No meta signals available for {Format}", format);

            return (signals, stats);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
