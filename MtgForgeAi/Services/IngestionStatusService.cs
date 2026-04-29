using MtgForgeAi.Models;

namespace MtgForgeAi.Services;

/// <summary>
/// Singleton that tracks the state of the background card ingestion job.
/// Thread-safe via Interlocked / volatile for scalar fields and lock for
/// the mutable complex state.
/// </summary>
public class IngestionStatusService
{
    private readonly object _lock = new();

    // Scalar state — written under lock, read without lock for display only
    private volatile bool _isRunning;
    private DateTime? _lastStartedAt;
    private DateTime? _lastCompletedAt;
    private IngestionResult? _lastResult;

    // Live progress during an active run
    private int _cardsEmbedded;
    private int _totalToEmbed;

    public bool IsRunning => _isRunning;

    public void MarkStarted()
    {
        lock (_lock)
        {
            _isRunning       = true;
            _lastStartedAt   = DateTime.UtcNow;
            _lastCompletedAt = null;
            _lastResult      = null;
            _cardsEmbedded   = 0;
            _totalToEmbed    = 0;
        }
    }

    public void SetTotalToEmbed(int total)
    {
        lock (_lock) _totalToEmbed = total;
    }

    public void IncrementEmbedded(int count = 1)
    {
        Interlocked.Add(ref _cardsEmbedded, count);
    }

    public void MarkCompleted(IngestionResult result)
    {
        lock (_lock)
        {
            _isRunning       = false;
            _lastCompletedAt = DateTime.UtcNow;
            _lastResult      = result;
        }
        // Sync the counter to the authoritative result count using Interlocked
        // so it stays consistent with IncrementEmbedded, which also uses Interlocked.
        Interlocked.Exchange(ref _cardsEmbedded, result.QdrantVectorsUpserted);
    }

    public void MarkFailed()
    {
        lock (_lock)
        {
            _isRunning       = false;
            _lastCompletedAt = DateTime.UtcNow;
        }
    }

    public (bool isRunning, DateTime? lastStartedAt, DateTime? lastCompletedAt,
            IngestionResult? lastResult, int cardsEmbedded, int totalToEmbed) Snapshot()
    {
        lock (_lock)
        {
            return (_isRunning, _lastStartedAt, _lastCompletedAt,
                    _lastResult, _cardsEmbedded, _totalToEmbed);
        }
    }
}
