using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Models.Snapshots;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Jobs.Snapshots;

/// <summary>
/// Job that completes when new snapshots for the collection appear after the create request.
/// Baseline = snapshot keys listed immediately before the request; success = new keys not in baseline
/// with S3 LastModified or parsed filename time at/after the request moment.
/// </summary>
public sealed class PendingSnapshotCreationJob : IJob
{
    public const string KeyPrefix = "snapshot-create-";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly string _collectionName;
    private readonly IReadOnlyList<NodeInfo> _requestedNodes;
    private readonly DateTime _requestedAtUtc;
    private readonly HashSet<string> _baselineSnapshotKeys;
    private readonly int? _retainLastNAfterVisible;
    private readonly IReadOnlySet<ulong>? _retentionClusterPeerIds;
    private readonly TimeSpan _timeout;

    public string Key => KeyPrefix + _collectionName;

    public bool IsWaitingForReady => false;

    public PendingSnapshotCreationJob(
        IServiceProvider serviceProvider,
        string collectionName,
        IReadOnlyList<NodeInfo> requestedNodes,
        DateTime requestedAtUtc,
        IReadOnlySet<string> baselineSnapshotKeys,
        int? retainLastNAfterVisible = null,
        IReadOnlySet<ulong>? retentionClusterPeerIds = null,
        TimeSpan? timeout = null)
    {
        _serviceProvider = serviceProvider;
        _collectionName = collectionName;
        _requestedNodes = requestedNodes;
        _requestedAtUtc = requestedAtUtc;
        _baselineSnapshotKeys = new HashSet<string>(baselineSnapshotKeys, StringComparer.Ordinal);
        _retainLastNAfterVisible = retainLastNAfterVisible;
        _retentionClusterPeerIds = retentionClusterPeerIds;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Stable identity for a snapshot row when comparing to the pre-request baseline.</summary>
    public static string BaselineKey(SnapshotInfo s)
    {
        if (s.Source == SnapshotSource.S3Storage
            || string.Equals(s.NodeUrl, S3Constants.StorageIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return "s3:" + s.SnapshotName;
        }

        return "n:" + s.NodeUrl + "|" + s.SnapshotName;
    }

    public Task<bool?> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);

    public void OnReady() { }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        var snapshotService = _serviceProvider.GetRequiredService<ISnapshotService>();
        var logger = _serviceProvider.GetRequiredService<ILogger<PendingSnapshotCreationJob>>();

        if (DateTime.UtcNow - _requestedAtUtc > _timeout)
        {
            var timeoutText = _timeout.TotalMinutes >= 1
                ? $"{(int)_timeout.TotalMinutes} minute(s)"
                : $"{(int)_timeout.TotalSeconds} second(s)";

            logger.LogWarning(
                "Snapshot creation job timed out for collection {CollectionName}: snapshots did not appear within {Timeout}",
                _collectionName, timeoutText);

            return (false, false, $"Snapshot did not appear within {timeoutText}");
        }

        IReadOnlyCollection<SnapshotInfo> snapshots;
        try
        {
            snapshots = await snapshotService.GetSnapshotsInfoAsync(
                cancellationToken,
                clearCache: true,
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

        var qualifiedNew = collectionSnapshots
            .Where(s => !_baselineSnapshotKeys.Contains(BaselineKey(s)) && IsAtOrAfterRequest(s))
            .ToList();

        var fromS3 = collectionSnapshots.Any(s =>
            s.Source == SnapshotSource.S3Storage
            || string.Equals(s.NodeUrl, S3Constants.StorageIdentifier, StringComparison.OrdinalIgnoreCase));

        if (fromS3)
        {
            if (qualifiedNew.Count >= _requestedNodes.Count)
            {
                logger.LogInformation(
                    "Snapshot creation job completed for collection {CollectionName}: {Count} new snapshot(s) after baseline (S3), expected {RequestedCount} node(s)",
                    _collectionName, qualifiedNew.Count, _requestedNodes.Count);
                return await CompleteSuccessAsync(snapshotService, logger, cancellationToken);
            }

            return (true, true, null);
        }

        var requestedUrls = _requestedNodes.Select(n => n.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requestedUrls
            .Where(url => !qualifiedNew.Any(s => string.Equals(s.NodeUrl, url, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count == 0 && qualifiedNew.Count > 0)
        {
            logger.LogInformation(
                "Snapshot creation job completed for collection {CollectionName}: new snapshots on all {Count} nodes",
                _collectionName, _requestedNodes.Count);
            return await CompleteSuccessAsync(snapshotService, logger, cancellationToken);
        }

        return (true, true, null);
    }

    /// <summary>True if snapshot timing is at/after the create request (S3 upload time or parsed name time).</summary>
    private bool IsAtOrAfterRequest(SnapshotInfo s)
    {
        var t0 = _requestedAtUtc.AddSeconds(-15);
        if (s.S3StorageModifiedUtc is { } sm && sm >= t0)
        {
            return true;
        }

        if (s.CreatedAt is { } ca && ca >= t0)
        {
            return true;
        }

        return false;
    }

    private async Task<(bool HasMore, bool Success, string? ErrorMessage)> CompleteSuccessAsync(
        ISnapshotService snapshotService,
        ILogger<PendingSnapshotCreationJob> logger,
        CancellationToken cancellationToken)
    {
        if (_retainLastNAfterVisible is > 0)
        {
            try
            {
                await snapshotService
                    .EnforceRetentionAsync(
                        _collectionName,
                        _retainLastNAfterVisible.Value,
                        cancellationToken,
                        _retentionClusterPeerIds);

                logger.LogInformation(
                    "Retention (last {N}) applied for {Collection} after snapshots became visible",
                    _retainLastNAfterVisible, _collectionName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention after snapshot visibility failed for {Collection}", _collectionName);
            }
        }

        return (false, true, null);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        return new Dictionary<string, object?>
        {
            [JobMetadataKeys.CurrentAction] = $"Waiting for snapshot: {_collectionName}",
            [JobMetadataKeys.StartedAtUtc] = _requestedAtUtc
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
