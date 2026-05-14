using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

/// <summary>
/// One-shot per tick: loads collections and snapshots, deletes snapshots below
/// <see cref="SnapshotConfiguration.MinSnapshotSizePercentOfCollection"/> vs on-disk collection size. Does not create snapshots.
/// </summary>
public sealed class UndersizedSnapshotCleanupJob : IJob
{
    public const string JobKey = "undersized-snapshot-cleanup";

    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<NodeInfo> _nodes;
    private readonly DynamicConfig _config;
    private readonly DateTime _startedAtUtc;

    public string Key => JobKey;
    public bool IsWaitingForReady => false;

    public UndersizedSnapshotCleanupJob(
        IServiceProvider serviceProvider,
        IReadOnlyList<NodeInfo> nodes,
        DynamicConfig config)
    {
        _serviceProvider = serviceProvider;
        _nodes = nodes;
        _config = config;
        _startedAtUtc = DateTime.UtcNow;
    }

    public Task<bool?> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);

    public void OnReady() { }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        var minPercent = _config.Snapshot.MinSnapshotSizePercentOfCollection;
        if (minPercent < 1m || minPercent > 100m)
            return (false, true, null);

        var clusterManager = _serviceProvider.GetRequiredService<IClusterManager>();
        var snapshotService = _serviceProvider.GetRequiredService<ISnapshotService>();
        var logger = _serviceProvider.GetRequiredService<ILogger<UndersizedSnapshotCleanupJob>>();

        IReadOnlyList<CollectionInfo> collections;
        try
        {
            collections = await clusterManager
                .GetCollectionsInfoAsync(clearCache: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Undersized snapshot cleanup skipped: failed to load collections");
            return (false, false, ex.Message);
        }

        var byCollection = collections
            .GroupBy(c => c.CollectionName)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CollectionInfo>)g.ToList(), StringComparer.Ordinal);

        IReadOnlyList<SnapshotInfo> snapshots;
        try
        {
            snapshots = await snapshotService
                .GetSnapshotsInfoAsync(clearCache: true, cancellationToken, nodesToUse: _nodes)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Undersized snapshot cleanup skipped: failed to load snapshots");
            return (false, false, ex.Message);
        }

        var snapshotsByCollection = snapshots
            .GroupBy(s => s.CollectionName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var percent = minPercent;
        foreach (var (collectionName, snaps) in snapshotsByCollection)
        {
            if (!byCollection.TryGetValue(collectionName, out var infos))
                continue;

            foreach (var s in snaps.ToList())
            {
                if (!IsSnapshotUndersized(s, infos, percent))
                    continue;

                try
                {
                    await snapshotService.DeleteSnapshotAsync(
                        collectionName,
                        s.SnapshotName,
                        s.Source,
                        nodeUrl: s.NodeUrl,
                        podName: s.PodName,
                        podNamespace: s.PodNamespace,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "Deleted undersized snapshot {Snapshot} for collection {Collection} (source={Source}, size={SizeBytes} B)",
                        s.SnapshotName,
                        collectionName,
                        s.Source,
                        s.SizeBytes);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to delete undersized snapshot {Snapshot} for collection {Collection}",
                        s.SnapshotName,
                        collectionName);
                }
            }
        }

        return (false, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata() =>
        new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = "Undersized snapshot cleanup",
            [JobMetadataKeys.StartedAtUtc] = _startedAtUtc
        };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool IsMinSizeThresholdEnabled(decimal percent) =>
        percent is >= 1m and <= 100m;

    private static bool IsS3Snapshot(SnapshotInfo s) =>
        s.Source == SnapshotSource.S3Storage
        || string.Equals(s.NodeUrl, S3Constants.StorageIdentifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>API snapshot: that node's collection size. S3: sum of per-node collection sizes (cluster total on disk).</summary>
    private static long? GetReferenceBytesForMinSize(SnapshotInfo s, IReadOnlyList<CollectionInfo> collectionInfosSameName)
    {
        if (collectionInfosSameName.Count == 0)
            return null;

        if (IsS3Snapshot(s))
        {
            long sum = 0;
            foreach (var c in collectionInfosSameName)
            {
                if (c.Metrics.SizeBytes is { } sb and > 0)
                    sum += sb;
            }

            return sum > 0 ? sum : null;
        }

        var row = collectionInfosSameName.FirstOrDefault(ci =>
            string.Equals(ci.NodeUrl, s.NodeUrl, StringComparison.OrdinalIgnoreCase));
        return row?.Metrics.SizeBytes is { } b and > 0 ? b : null;
    }

    private static long? GetMinimumSnapshotBytes(long referenceBytes, decimal percent)
    {
        if (referenceBytes <= 0)
            return null;
        var min = (long)(referenceBytes * (double)percent / 100.0);
        return min < 1 ? 1 : min;
    }

    /// <summary>True when snapshot file size is below the configured ratio of collection on-disk size. Unknown sizes (≤0) are not undersized.</summary>
    private static bool IsSnapshotUndersized(SnapshotInfo s, IReadOnlyList<CollectionInfo> collectionInfos, decimal percent)
    {
        if (!IsMinSizeThresholdEnabled(percent) || s.SizeBytes <= 0)
            return false;

        var reference = GetReferenceBytesForMinSize(s, collectionInfos);
        if (reference is null or <= 0)
            return false;

        var minBytes = GetMinimumSnapshotBytes(reference.Value, percent);
        return minBytes is { } min && s.SizeBytes < min;
    }
}
