using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs;

/// <summary>
/// Job that completes when snapshots created for the given collection (on requested nodes) have appeared.
/// Uses the node list passed at creation; if a node becomes unavailable, the job fails with an error.
/// Fails with timeout error if snapshots do not appear within <see cref="Timeout"/>.
/// Resolves ISnapshotService and ILogger from the service provider when needed.
/// Registered by SnapshotService when a manual snapshot is requested (waitForResult: false).
/// </summary>
public sealed class PendingSnapshotCreationJob : IJob
{
    public const string KeyPrefix = "snapshot-create-";
    public const string MetadataCurrentAction = "CurrentAction";

    /// <summary>
    /// After this many minutes without snapshots appearing, the job fails with a timeout error.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly string _collectionName;
    private readonly IReadOnlyList<NodeInfo> _requestedNodes;
    private readonly DateTime _requestedAtUtc;

    public string Key => KeyPrefix + _collectionName;
    public bool IsWaitingForReady => false;

    public PendingSnapshotCreationJob(
        IServiceProvider serviceProvider,
        string collectionName,
        IReadOnlyList<NodeInfo> requestedNodes,
        DateTime requestedAtUtc)
    {
        _serviceProvider = serviceProvider;
        _collectionName = collectionName;
        _requestedNodes = requestedNodes;
        _requestedAtUtc = requestedAtUtc;
    }

    public Task<bool?> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);
    public void OnReady() { }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        var snapshotService = _serviceProvider.GetRequiredService<ISnapshotService>();
        var logger = _serviceProvider.GetRequiredService<ILogger<PendingSnapshotCreationJob>>();

        var elapsed = DateTime.UtcNow - _requestedAtUtc;
        if (elapsed > Timeout)
        {
            logger.LogWarning(
                "Snapshot creation job timed out for collection {CollectionName}: snapshots did not appear within {Minutes} minutes",
                _collectionName, (int)Timeout.TotalMinutes);
            return (false, false, $"Snapshot did not appear within {(int)Timeout.TotalMinutes} minutes");
        }

        IReadOnlyList<SnapshotInfo> snapshots;
        try
        {
            snapshots = await snapshotService.GetSnapshotsInfoAsync(
                clearCache: true,
                cancellationToken,
                nodesToUse: _requestedNodes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Snapshot creation job failed for collection {CollectionName}: error while fetching snapshots (e.g. node unavailable)", _collectionName);
            return (false, false, ex.Message);
        }

        var collectionSnapshots = snapshots
            .Where(s => string.Equals(s.CollectionName, _collectionName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Consider a node "done" if it has a snapshot created at or after request time (with 1s tolerance for clock skew)
        var cutoff = _requestedAtUtc.AddSeconds(-1);
        var newSnapshots = collectionSnapshots.Where(s => s.CreatedAt >= cutoff).ToList();

        // When snapshots come from S3, GetSnapshotsInfoAsync returns NodeUrl = S3Constants.StorageIdentifier for all;
        // we cannot match by node URL. Treat as complete when we have at least N new snapshots (N = requested nodes).
        var fromS3 = newSnapshots.Any(s => s.Source == SnapshotSource.S3Storage || string.Equals(s.NodeUrl, S3Constants.StorageIdentifier, StringComparison.OrdinalIgnoreCase));
        if (fromS3)
        {
            if (newSnapshots.Count >= _requestedNodes.Count)
            {
                logger.LogInformation(
                    "Snapshot creation job completed for collection {CollectionName}: {Count} new snapshots visible in S3 (requested {RequestedCount} nodes)",
                    _collectionName, newSnapshots.Count, _requestedNodes.Count);
                return (false, true, null);
            }
            return (true, true, null);
        }

        var nodeUrlsWithNewSnapshot = newSnapshots
            .Select(s => s.NodeUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedUrls = _requestedNodes.Select(n => n.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requestedUrls.Where(url => !nodeUrlsWithNewSnapshot.Contains(url)).ToList();

        if (missing.Count == 0)
        {
            logger.LogInformation(
                "Snapshot creation job completed for collection {CollectionName}: snapshots visible on all {Count} nodes",
                _collectionName, _requestedNodes.Count);
            return (false, true, null);
        }

        return (true, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        return new Dictionary<string, object?>
        {
            [MetadataCurrentAction] = $"Creating snapshot: {_collectionName}"
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
