using Vigilante.Constants;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

public sealed class RecoverFromSnapshotJob(
    IServiceProvider serviceProvider,
    string collectionName,
    string targetNodeUrl,
    Aer.QdrantClient.Http.Models.Shared.SnapshotPriority snapshotPriority,
    SnapshotSource? source = null,
    string? snapshotName = null,
    string? sourceCollectionName = null,
    string? snapshotUrl = null,
    string? snapshotChecksum = null) : IJob
{
    private bool _started;
    private bool _timedOut;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    private static readonly TimeSpan _completionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _initialReadyGracePeriod = TimeSpan.FromSeconds(20);

    private bool _seenActiveTransfers;
    private bool _baselineCaptured;
    private string? _initialShardsFingerprint;

    public string Key => $"snapshot-recovery-{collectionName}";

    public bool IsWaitingForReady { get; private set; }

    public async Task<bool?> CheckReadyAsync(CancellationToken cancellationToken)
    {
        if (!IsWaitingForReady)
        {
            return null;
        }

        if (DateTime.UtcNow - _startedAtUtc > _completionTimeout)
        {
            _timedOut = true;
            IsWaitingForReady = false;
            return true;
        }

        var clusterManager = serviceProvider.GetRequiredService<IClusterManager>();
        var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, cancellationToken);
        var nodeCollection = collections.FirstOrDefault(c =>
            string.Equals(c.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.NodeUrl, targetNodeUrl, StringComparison.OrdinalIgnoreCase));

        if (nodeCollection is null)
        {
            return false;
        }

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
        {
            return true;
        }

        if (_seenActiveTransfers)
        {
            return true;
        }

        // Recovery on an existing collection can report zero transfers for a short time
        // before actual replication starts. Avoid finishing the job too early.
        if (!_seenActiveTransfers && DateTime.UtcNow - _startedAtUtc < _initialReadyGracePeriod)
        {
            return false;
        }

        return true;
    }

    public void OnReady() => IsWaitingForReady = false;

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            _started = true;
            var snapshotService = serviceProvider.GetRequiredService<ISnapshotService>();
            var (success, error) = await snapshotService.ExecuteRecoverAsync(
                collectionName,
                targetNodeUrl,
                snapshotPriority,
                waitForResult: false,
                cancellationToken,
                source,
                snapshotName,
                sourceCollectionName,
                snapshotUrl,
                snapshotChecksum);

            if (!success)
            {
                return (false, false, error);
            }

            IsWaitingForReady = true;
            return (true, true, null);
        }

        if (_timedOut)
        {
            return (false, false, "Recovery did not complete within timeout");
        }

        return (false, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        var action = !string.IsNullOrWhiteSpace(snapshotUrl)
            ? $"Recovering '{collectionName}' from URL"
            : $"Recovering '{collectionName}' from '{snapshotName}'";

        return new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = action,
            [JobMetadataKeys.StartedAtUtc] = _startedAtUtc
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string BuildShardsFingerprint(IReadOnlyList<Vigilante.Models.ShardDetails>? shards)
    {
        if (shards is not { Count: > 0 })
        {
            return "no-shards";
        }

        return string.Join(
            "|",
            shards
                .OrderBy(s => s.ShardId)
                .Select(s => $"{s.ShardId}:{s.VectorsSizeBytes}:{s.PayloadsSizeBytes}"));
    }
}
