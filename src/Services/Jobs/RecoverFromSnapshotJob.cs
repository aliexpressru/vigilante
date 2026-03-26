using Vigilante.Constants;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

public sealed class RecoverFromSnapshotJob(
    IServiceProvider serviceProvider,
    ISnapshotService snapshotService,
    string collectionName,
    string snapshotName,
    string targetNodeUrl,
    SnapshotSource source,
    string? sourceCollectionName,
    Aer.QdrantClient.Http.Models.Shared.SnapshotPriority snapshotPriority) : IJob
{
    public string Key => $"snapshot-recovery-{collectionName}";
    public bool IsWaitingForReady => _waitingForReady;

    private bool _started;
    private bool _waitingForReady;
    private bool _timedOut;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitialReadyGracePeriod = TimeSpan.FromSeconds(20);
    private bool _seenActiveTransfers;
    private bool _baselineCaptured;
    private string? _initialShardsFingerprint;

    public async Task<bool?> CheckReadyAsync(CancellationToken cancellationToken)
    {
        if (!_waitingForReady)
            return null;

        if (DateTime.UtcNow - _startedAtUtc > CompletionTimeout)
        {
            _timedOut = true;
            _waitingForReady = false;
            return true;
        }

        var clusterManager = serviceProvider.GetRequiredService<IClusterManager>();
        var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, cancellationToken);
        var nodeCollection = collections.FirstOrDefault(c =>
            string.Equals(c.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.NodeUrl, targetNodeUrl, StringComparison.OrdinalIgnoreCase));

        if (nodeCollection is null)
            return false;

        var metrics = nodeCollection.Metrics;
        var hasActiveTransfers = metrics.OutgoingTransfers is { Count: > 0 };
        var currentShardsFingerprint = BuildShardsFingerprint(metrics.Shards);

        if (hasActiveTransfers)
        {
            _seenActiveTransfers = true;
            return false;
        }

        if (!_baselineCaptured)
        {
            _initialShardsFingerprint = currentShardsFingerprint;
            _baselineCaptured = true;
            return false;
        }

        var shardsChanged = !string.Equals(_initialShardsFingerprint, currentShardsFingerprint, StringComparison.Ordinal);
        if (shardsChanged)
            return true;

        if (_seenActiveTransfers)
            return true;

        // Recovery on an existing collection can report zero transfers for a short time
        // before actual replication starts. Avoid finishing the job too early.
        if (!_seenActiveTransfers && DateTime.UtcNow - _startedAtUtc < InitialReadyGracePeriod)
            return false;

        return true;
    }

    public void OnReady() => _waitingForReady = false;

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _started = true;
            var (success, error) = await snapshotService.RecoverFromSnapshotAsync(
                collectionName,
                snapshotName,
                targetNodeUrl,
                source,
                sourceCollectionName,
                snapshotPriority,
                waitForResult: false,
                cancellationToken);

            if (!success)
                return (false, false, error);

            _waitingForReady = true;
            return (true, true, null);
        }

        if (_timedOut)
            return (false, false, "Recovery did not complete within timeout");

        return (false, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        return new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = $"Recovering '{collectionName}' from '{snapshotName}'",
            [JobMetadataKeys.StartedAtUtc] = _startedAtUtc
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string BuildShardsFingerprint(IReadOnlyList<Vigilante.Models.ShardDetails>? shards)
    {
        if (shards is not { Count: > 0 })
            return "no-shards";

        return string.Join(
            "|",
            shards
                .OrderBy(s => s.ShardId)
                .Select(s => $"{s.ShardId}:{s.SizeBytes}:{s.VectorsSizeBytes}:{s.PayloadsSizeBytes}"));
    }
}
