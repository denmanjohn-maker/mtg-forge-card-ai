using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Read-only interface over the tournament meta signal data store.
/// Implemented by <see cref="MongoService"/>; also used as a seam for testing.
/// </summary>
public interface IMetaSignalRepository
{
    Task<List<MetaSignal>> GetTopMetaSignalsAsync(
        string format, int limit = 100, CancellationToken ct = default);

    Task<MetaSignalStats?> GetMetaSignalStatsAsync(
        string format, CancellationToken ct = default);
}
