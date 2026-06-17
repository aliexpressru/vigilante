using System.Collections.Concurrent;
using System.Globalization;
using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Models.Snapshots;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs.Snapshots;

namespace Vigilante.Services.Snapshots;

/// <summary>
/// Service for managing collection snapshots across the Qdrant cluster
/// </summary>
public partial class SnapshotService(
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    IS3SnapshotService s3SnapshotService,
    IPodCommandExecutor? commandExecutor,
    IOptions<QdrantOptions> options,
    IDynamicConfigService dynamicConfigService,
    IJobRegistry jobRegistry,
    IServiceProvider serviceProvider,
    ILogger<SnapshotService> logger
) : ISnapshotService
{
    private sealed record ParsedSnapshotInfo(string CollectionName, ulong PeerId, DateTime? CreatedAt);

    private readonly QdrantOptions _options = options.Value;
    private IReadOnlyCollection<SnapshotInfo>? _snapshotsCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>Excludes overlapping multi-node creates until the pending job is registered (same collection, case-insensitive).</summary>
    private readonly ConcurrentDictionary<string, byte> _snapshotCreateInFlight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SnapshotRecoveryStartResult> RequestRecoverAsync(
        string collectionName,
        string targetNodeUrl,
        Aer.QdrantClient.Http.Models.Shared.SnapshotPriority snapshotPriority,
        bool waitForResult,
        CancellationToken cancellationToken,
        SnapshotSource? source = null,
        string? snapshotName = null,
        string? sourceCollectionName = null,
        string? snapshotUrl = null,
        string? snapshotChecksum = null
    )
    {
        if (waitForResult)
        {
            var (success, error) = await ExecuteRecoverAsync(
                collectionName,
                targetNodeUrl,
                snapshotPriority,
                waitForResult: true,
                cancellationToken,
                source,
                snapshotName,
                sourceCollectionName,
                snapshotUrl,
                snapshotChecksum
            );

            return success
                ? new SnapshotRecoveryStartResult(false, false, $"Collection '{collectionName}' recovered successfully")
                : new SnapshotRecoveryStartResult(true, false, error ?? "Recovery failed");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IJob job = new RecoverFromSnapshotJob(
            serviceProvider,
            collectionName,
            targetNodeUrl,
            snapshotPriority,
            source,
            snapshotName: snapshotName,
            sourceCollectionName: sourceCollectionName,
            snapshotUrl: snapshotUrl,
            snapshotChecksum: snapshotChecksum
        );

        if (!jobRegistry.TryAddJob(job, cts))
        {
            cts.Dispose();

            return new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: true,
                Message: $"Recovery already in progress for collection '{collectionName}'"
            );
        }

        return new SnapshotRecoveryStartResult(
            ApiError: false,
            AlreadyInProgress: false,
            Message: $"Recovery started for collection '{collectionName}'"
        );
    }

    public async Task<SnapshotRecoveryStartResult> RequestMultiRecoverAsync(
        string targetCollectionName,
        IReadOnlyList<V1MultiRecoverItem> recoveryItems,
        Aer.QdrantClient.Http.Models.Shared.SnapshotPriority snapshotPriority,
        CancellationToken cancellationToken,
        string? sourceCollectionName = null
    )
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var peerClients = new IQdrantHttpClient[recoveryItems.Count];
        var snapshotUris = new Uri[recoveryItems.Count];
        var targetNodeUrls = new string[recoveryItems.Count];

        for (var i = 0; i < recoveryItems.Count; i++)
        {
            var item = recoveryItems[i];
            peerClients[i] = clientFactory.CreateClientFromUrl(item.TargetNodeUrl, _options.ApiKey);
            targetNodeUrls[i] = item.TargetNodeUrl;

            _ = Enum.TryParse<SnapshotSource>(item.SnapshotSource, out var snapshotSource);

            switch (snapshotSource)
            {
                case SnapshotSource.S3Storage:
                {
                    var sourceCollection = !string.IsNullOrWhiteSpace(sourceCollectionName)
                        ? sourceCollectionName
                        : targetCollectionName;

                    var presignedUrl = await s3SnapshotService.GetPresignedDownloadUrlAsync(
                        sourceCollection!,
                        item.SnapshotName,
                        TimeSpan.FromHours(2), // since recovery is done one node at a time - twice the expiration
                        null,
                        cancellationToken
                    );

                    if (string.IsNullOrWhiteSpace(presignedUrl))
                    {
                        cts.Dispose();
                        return new SnapshotRecoveryStartResult(
                            ApiError: true,
                            AlreadyInProgress: false,
                            Message: $"Failed to generate presigned URL for snapshot '{item.SnapshotName}' on item {i + 1}"
                        );
                    }

                    snapshotUris[i] = new Uri(presignedUrl);
                    break;
                }

                case SnapshotSource.KubernetesStorage or SnapshotSource.QdrantApi
                    when !string.IsNullOrWhiteSpace(sourceCollectionName)
                        && !string.Equals(sourceCollectionName, targetCollectionName, StringComparison.Ordinal):

                    snapshotUris[i] = new Uri($"file:///qdrant/snapshots/{sourceCollectionName!}/{item.SnapshotName}");
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Can't restore collection {targetCollectionName} from source {snapshotSource}"
                    );
            }
        }

        var (job, errorMessage) = await RecoverFromMultipleSnapshotsJob.CreateAsync(
            serviceProvider,
            peerClients,
            snapshotUris,
            targetNodeUrls,
            targetCollectionName,
            snapshotPriority,
            cancellationToken
        );

        if (errorMessage is not null)
        {
            cts.Dispose();
            return new SnapshotRecoveryStartResult(ApiError: true, AlreadyInProgress: false, Message: errorMessage);
        }

        if (job is null)
        {
            cts.Dispose();
            return new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: $"Collection '{targetCollectionName}' has no recovery steps to execute"
            );
        }

        if (!jobRegistry.TryAddJob(job, cts))
        {
            cts.Dispose();
            return new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: true,
                Message: $"Multi-snapshot recovery already in progress for collection '{targetCollectionName}'"
            );
        }

        return new SnapshotRecoveryStartResult(
            ApiError: false,
            AlreadyInProgress: false,
            Message: $"Multi-snapshot recovery started for collection '{targetCollectionName}' on {recoveryItems.Count} node(s)"
        );
    }

    public async Task<(bool Success, string? ErrorMessage)> ExecuteRecoverAsync(
        string collectionName,
        string targetNodeUrl,
        Aer.QdrantClient.Http.Models.Shared.SnapshotPriority snapshotPriority,
        bool waitForResult,
        CancellationToken cancellationToken,
        SnapshotSource? source = null,
        string? snapshotName = null,
        string? sourceCollectionName = null,
        string? snapshotUrl = null,
        string? snapshotChecksum = null
    )
    {
        try
        {
            var useUrlRecovery = !string.IsNullOrWhiteSpace(snapshotUrl);
            if (useUrlRecovery)
            {
                var qdrantClient = clientFactory.CreateClientFromUrl(targetNodeUrl, _options.ApiKey);
                var snapshotLocationUri = new Uri(snapshotUrl!);
                var urlResult = await qdrantClient.RecoverCollectionFromSnapshot(
                    collectionName,
                    snapshotLocationUri,
                    cancellationToken,
                    isWaitForResult: waitForResult,
                    snapshotPriority: snapshotPriority,
                    snapshotChecksum: snapshotChecksum
                );
                return urlResult.IsAcceptedOrSuccess()
                    ? (true, null)
                    : (false, $"Failed to recover collection '{collectionName}' from URL '{snapshotUrl}' on {targetNodeUrl}");
            }

            switch (source)
            {
                case SnapshotSource.S3Storage:
                {
                    var sourceCollection = !string.IsNullOrWhiteSpace(sourceCollectionName)
                        ? sourceCollectionName
                        : collectionName;

                    var presignedUrl = await s3SnapshotService.GetPresignedDownloadUrlAsync(
                        sourceCollection!,
                        snapshotName!,
                        TimeSpan.FromHours(1),
                        null,
                        cancellationToken
                    );
                    if (string.IsNullOrWhiteSpace(presignedUrl))
                    {
                        return (false, $"Failed to generate S3 download URL for snapshot '{snapshotName}'");
                    }

                    var qdrantClient = clientFactory.CreateClientFromUrl(targetNodeUrl, _options.ApiKey);

                    var presignedUri = new Uri(presignedUrl);

                    var s3RecoverResult = await qdrantClient.RecoverCollectionFromSnapshot(
                        collectionName,
                        presignedUri,
                        cancellationToken,
                        isWaitForResult: waitForResult,
                        snapshotPriority: snapshotPriority,
                        snapshotChecksum: null
                    );

                    return s3RecoverResult.IsAcceptedOrSuccess()
                        ? (true, null)
                        : (
                            false,
                            $"Failed to recover collection '{collectionName}' from snapshot '{snapshotName}' on {targetNodeUrl}"
                        );
                }

                case SnapshotSource.KubernetesStorage
                    when !string.IsNullOrWhiteSpace(sourceCollectionName)
                        && !string.Equals(sourceCollectionName, collectionName, StringComparison.Ordinal):
                {
                    var snapshotPath = $"file:///qdrant/snapshots/{sourceCollectionName}/{snapshotName}";

                    var qdrantClient = clientFactory.CreateClientFromUrl(targetNodeUrl, _options.ApiKey);

                    var snapshotPathUri = new Uri(snapshotPath);

                    var pathRecoverResult = await qdrantClient.RecoverCollectionFromSnapshot(
                        collectionName,
                        snapshotPathUri,
                        cancellationToken,
                        isWaitForResult: waitForResult,
                        snapshotPriority: snapshotPriority,
                        snapshotChecksum: null
                    );

                    return pathRecoverResult.IsAcceptedOrSuccess()
                        ? (true, null)
                        : (
                            false,
                            $"Failed to recover collection '{collectionName}' from snapshot '{snapshotName}' on {targetNodeUrl}"
                        );
                }
            }

            var regularClient = clientFactory.CreateClientFromUrl(targetNodeUrl, _options.ApiKey);
            var regularResult = await regularClient.RecoverCollectionFromSnapshot(
                collectionName,
                snapshotName!,
                cancellationToken,
                isWaitForResult: waitForResult,
                snapshotPriority: snapshotPriority
            );

            return regularResult.IsAcceptedOrSuccess()
                ? (true, null)
                : (false, $"Failed to recover collection '{collectionName}' from snapshot '{snapshotName}' on {targetNodeUrl}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during recovery for collection {CollectionName}", collectionName);
            return (false, ex.Message);
        }
    }

    public async Task<CreateCollectionSnapshotBatchResult> CreateCollectionSnapshotAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken,
        bool waitForResult = false,
        int? retainLastNAfterVisible = null,
        IReadOnlySet<ulong>? retentionClusterPeerIds = null
    )
    {
        var nodeUrlsList = nodeUrls.ToList();

        if (jobRegistry.HasPendingSnapshotCreationForCollection(collectionName))
        {
            logger.LogInformation(
                "Snapshot create skipped for {CollectionName}: still waiting for previous snapshot(s) (pending job)",
                collectionName
            );
            return CreateCollectionSnapshotBatchResult.SkippedForNodes(nodeUrlsList);
        }

        // Guards the gap before snapshot-create job is registered:
        // two concurrent calls can both see "no pending job" and race into duplicate create requests.
        var flightKey = collectionName.ToLowerInvariant();
        if (!_snapshotCreateInFlight.TryAdd(flightKey, 0))
        {
            logger.LogInformation(
                "Snapshot create skipped for {CollectionName}: another create operation is in progress",
                collectionName
            );
            return CreateCollectionSnapshotBatchResult.SkippedForNodes(nodeUrlsList);
        }

        try
        {
            logger.LogInformation(
                "Creating snapshot for collection {CollectionName} on {NodeCount} specified nodes",
                collectionName,
                nodeUrlsList.Count
            );

            var baselineSnapshotKeys = await BuildBaselineSnapshotKeysForCollectionAsync(
                    collectionName,
                    nodeUrlsList,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var requestedAt = DateTime.UtcNow;

            var results = new Dictionary<string, string?>();

            var createTasks = nodeUrlsList.Select(async nodeUrl =>
            {
                try
                {
                    logger.LogInformation(
                        "Creating snapshot for collection {CollectionName} on node {NodeUrl}",
                        collectionName,
                        nodeUrl
                    );

                    var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
                    var result = await qdrantClient.CreateCollectionSnapshot(
                        collectionName,
                        cancellationToken,
                        isWaitForResult: waitForResult
                    );

                    if (!result.IsAcceptedOrSuccess())
                    {
                        logger.LogError(
                            "Failed to create snapshot for collection {CollectionName} on node {NodeUrl}: {Error}",
                            collectionName,
                            nodeUrl,
                            result?.Status?.Error ?? MetricConstants.UnknownErrorMessage
                        );
                        return (NodeUrl: nodeUrl, SnapshotName: (string?)null);
                    }

                    var snapshotName = result.Result?.Name ?? $"{collectionName}-snapshot-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    var statusText = result.IsAccepted()
                        ? QdrantConstants.SnapshotAcceptedStatus
                        : QdrantConstants.SnapshotCreatedStatus;
                    logger.LogInformation(
                        "Snapshot {StatusText} for collection {CollectionName} on node {NodeUrl}",
                        statusText,
                        collectionName,
                        nodeUrl
                    );

                    return (NodeUrl: nodeUrl, SnapshotName: snapshotName);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to create snapshot for collection {CollectionName} on node {NodeUrl}",
                        collectionName,
                        nodeUrl
                    );
                    return (NodeUrl: nodeUrl, SnapshotName: (string?)null);
                }
            });

            var createResults = await Task.WhenAll(createTasks);

            foreach (var (NodeUrl, SnapshotName) in createResults)
            {
                results[NodeUrl] = SnapshotName;
            }

            var successCount = results.Values.Count(s => s != null);
            logger.LogInformation(
                "Snapshot created for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes",
                collectionName,
                successCount,
                results.Count
            );

            // Follow-up job: UI + optional retention after snapshots become visible (important when waitForResult is false).
            if (successCount > 0)
            {
                var dynamicConfig = await dynamicConfigService.GetConfigAsync(cancellationToken).ConfigureAwait(false);
                var timeoutSeconds = Math.Max(1, dynamicConfig.Snapshot.PendingCreateTimeoutSeconds);
                var pendingTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                var succeededNodeUrls = results.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();
                var requestedNodes = succeededNodeUrls.Select(url => new NodeInfo { Url = url }).ToList();
                var job = new PendingSnapshotCreationJob(
                    serviceProvider,
                    collectionName,
                    requestedNodes,
                    requestedAt,
                    baselineSnapshotKeys,
                    retainLastNAfterVisible,
                    retentionClusterPeerIds,
                    timeout: pendingTimeout
                );
                var cts = new CancellationTokenSource();
                if (!jobRegistry.TryAddJob(job, cts))
                {
                    cts.Dispose();
                }
            }

            return new CreateCollectionSnapshotBatchResult { Results = results, SkippedDuplicatePending = false };
        }
        finally
        {
            _snapshotCreateInFlight.TryRemove(flightKey, out _);
        }
    }

    /// <summary>
    /// Deletes a snapshot for a collection on a specific node
    /// </summary>
    public async Task<bool> DeleteCollectionSnapshotApiAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            logger.LogInformation(
                "Deleting snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}",
                snapshotName,
                collectionName,
                nodeUrl
            );
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.DeleteCollectionSnapshot(
                collectionName,
                snapshotName,
                cancellationToken,
                isWaitForResult: false
            );

            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted()
                    ? QdrantConstants.SnapshotDeletionAcceptedStatus
                    : QdrantConstants.SnapshotDeletedStatus;
                logger.LogInformation(
                    "Snapshot {SnapshotName} {StatusText} for collection {CollectionName} on node {NodeUrl}",
                    snapshotName,
                    statusText,
                    collectionName,
                    nodeUrl
                );
                return true;
            }

            logger.LogError(
                "Failed to delete snapshot {SnapshotName} for collection {CollectionName}: {Error}",
                snapshotName,
                collectionName,
                result?.Status?.Error ?? MetricConstants.UnknownErrorMessage
            );
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to delete snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}",
                snapshotName,
                collectionName,
                nodeUrl
            );
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionSnapshotApiAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default
    )
    {
        var nodeUrlsList = nodeUrls.ToList();
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} via API on {NodeCount} specified nodes",
            snapshotName,
            collectionName,
            nodeUrlsList.Count
        );

        var results = new Dictionary<string, bool>();

        var deleteTasks = nodeUrlsList.Select(async nodeUrl =>
        {
            var success = await DeleteCollectionSnapshotApiAsync(nodeUrl, collectionName, snapshotName, cancellationToken);

            return (NodeUrl: nodeUrl, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var (NodeUrl, Success) in deleteResults)
        {
            results[NodeUrl] = Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Snapshot {SnapshotName} deleted via API: {SuccessCount}/{TotalCount} nodes",
            snapshotName,
            successCount,
            results.Count
        );

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteSnapshotFromDiskAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        CancellationToken cancellationToken
    )
    {
        var podsList = pods.ToList();
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on {PodCount} specified pods",
            snapshotName,
            collectionName,
            podsList.Count
        );

        var results = new Dictionary<string, bool>();

        var deleteTasks = podsList.Select(async pod =>
        {
            var success = await DeleteSnapshotFromDiskAsync(
                pod.PodName,
                pod.PodNamespace,
                collectionName,
                snapshotName,
                cancellationToken
            );

            return (PodName: pod.PodName, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var (PodName, Success) in deleteResults)
        {
            results[PodName] = Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Snapshot {SnapshotName} deleted from disk: {SuccessCount}/{TotalCount} pods",
            snapshotName,
            successCount,
            results.Count
        );

        return results;
    }

    public async Task<bool> DeleteSnapshotAsync(
        string collectionName,
        string snapshotName,
        SnapshotSource source,
        CancellationToken cancellationToken,
        string? nodeUrl = null,
        string? podName = null,
        string? podNamespace = null
    )
    {
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} (source: {Source})",
            snapshotName,
            collectionName,
            source
        );

        switch (source)
        {
            case SnapshotSource.S3Storage:
                // Delete from S3 storage
                logger.LogInformation("Deleting snapshot {SnapshotName} from S3 storage", snapshotName);

                return await s3SnapshotService.DeleteSnapshotAsync(collectionName, snapshotName, podNamespace, cancellationToken);
            // Delete from Kubernetes storage (disk)
            case SnapshotSource.KubernetesStorage when string.IsNullOrEmpty(podName) || string.IsNullOrEmpty(podNamespace):
                logger.LogError("PodName and PodNamespace are required for deleting snapshots from Kubernetes storage");
                return false;
            case SnapshotSource.KubernetesStorage:
                logger.LogInformation(
                    "Deleting snapshot {SnapshotName} from Kubernetes storage on pod {PodName}",
                    snapshotName,
                    podName
                );

                return await DeleteSnapshotFromDiskAsync(podName, podNamespace, collectionName, snapshotName, cancellationToken);
            // SnapshotSource.QdrantApi
            default:
            {
                // Delete via Qdrant API (for S3 or API-managed snapshots)
                if (string.IsNullOrEmpty(nodeUrl))
                {
                    logger.LogError("NodeUrl is required for deleting snapshots via Qdrant API");
                    return false;
                }

                logger.LogInformation("Deleting snapshot {SnapshotName} via Qdrant API on node {NodeUrl}", snapshotName, nodeUrl);

                return await DeleteCollectionSnapshotApiAsync(nodeUrl, collectionName, snapshotName, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Downloads a snapshot for a collection from a specific node via Qdrant API
    /// </summary>
    public async Task<Stream?> DownloadCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            logger.LogInformation(
                "Downloading snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}",
                snapshotName,
                collectionName,
                nodeUrl
            );
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);

            var result = await qdrantClient.DownloadCollectionSnapshot(collectionName, snapshotName, cancellationToken);

            if (result?.Result?.SnapshotDataStream != null)
            {
                logger.LogInformation(
                    "Snapshot {SnapshotName} downloaded successfully for collection {CollectionName} from node {NodeUrl}",
                    snapshotName,
                    collectionName,
                    nodeUrl
                );
                return result.Result.SnapshotDataStream;
            }

            logger.LogError(
                "Failed to download snapshot {SnapshotName} for collection {CollectionName}: empty or null result",
                snapshotName,
                collectionName
            );
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to download snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}",
                snapshotName,
                collectionName,
                nodeUrl
            );
            return null;
        }
    }

    /// <summary>
    /// Downloads a snapshot directly from disk on a specific pod (bypasses Qdrant API)
    /// </summary>
    public async Task<Stream?> DownloadSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var snapshotPath = $"{QdrantConstants.SnapshotsPath}/{collectionName}/{snapshotName}";

            if (commandExecutor == null)
            {
                logger.LogError("Command executor not available - not running in Kubernetes cluster");
                return null;
            }

            // Get expected file size for verification
            var expectedSize = await commandExecutor.GetFileSizeInBytesAsync(
                podName,
                podNamespace,
                snapshotPath,
                cancellationToken
            );

            if (!expectedSize.HasValue)
            {
                logger.LogWarning("Could not get file size from pod - will download without size limit!");
            }

            // Get checksum for verification
            var checksumPath = $"{snapshotPath}.checksum";
            var expectedChecksum = await commandExecutor.GetFileContentAsync(
                podName,
                podNamespace,
                checksumPath,
                cancellationToken
            );

            // Download file using cat command
            var snapshotStream = await commandExecutor.DownloadFileAsync(
                podName,
                podNamespace,
                snapshotPath,
                expectedSize,
                cancellationToken
            );

            if (snapshotStream == null)
            {
                logger.LogError("Failed to download snapshot {SnapshotName} from disk on pod {PodName}", snapshotName, podName);
                return null;
            }

            return snapshotStream;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to download snapshot {SnapshotName} from disk on pod {PodName} in namespace {Namespace}",
                snapshotName,
                podName,
                podNamespace
            );
            return null;
        }
    }

    public async Task<Stream?> DownloadSnapshotWithFallbackAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken,
        string? podName = null,
        string? podNamespace = null
    )
    {
        logger.LogInformation(
            "Downloading snapshot {SnapshotName} for collection {CollectionName} with fallback (S3 → API → Disk)",
            snapshotName,
            collectionName
        );

        // Priority 1: Try S3 first if available
        var isS3Available = await s3SnapshotService.CheckIsAvailableAsync(podNamespace, cancellationToken);
        if (isS3Available)
        {
            try
            {
                var s3Stream = await s3SnapshotService.DownloadSnapshotAsync(
                    collectionName,
                    snapshotName,
                    podNamespace,
                    cancellationToken
                );

                if (s3Stream != null)
                {
                    return s3Stream;
                }

                logger.LogWarning("S3 download returned null, trying API fallback");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "S3 download failed, trying API fallback");
            }
        }

        // Priority 2: Try API
        try
        {
            var apiStream = await DownloadCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, cancellationToken);

            if (apiStream != null)
            {
                return apiStream;
            }

            logger.LogWarning("API download returned null, trying disk fallback");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "API download failed, trying disk fallback");
        }

        // Priority 3: Fallback to disk if API fails
        if (!string.IsNullOrEmpty(podName) && !string.IsNullOrEmpty(podNamespace))
        {
            try
            {
                var diskStream = await DownloadSnapshotFromDiskAsync(
                    podName,
                    podNamespace,
                    collectionName,
                    snapshotName,
                    cancellationToken
                );

                if (diskStream != null)
                {
                    return diskStream;
                }

                logger.LogWarning("Disk download returned null");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Disk download failed");
            }
        }
        else
        {
            logger.LogWarning("Cannot attempt disk fallback: PodName or PodNamespace is missing");
        }

        logger.LogError("Failed to download snapshot via both API and disk");
        return null;
    }

    public void InvalidateCache()
    {
        _snapshotsCache = null;
    }

    public async Task<IReadOnlyCollection<SnapshotInfo>> GetSnapshotsInfoAsync(
        CancellationToken cancellationToken,
        bool clearCache = false,
        IReadOnlyList<NodeInfo>? nodesToUse = null
    )
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // Clear cache if requested
            if (clearCache)
            {
                _snapshotsCache = null;
            }

            if (_snapshotsCache != null && nodesToUse == null)
            {
                return _snapshotsCache;
            }

            List<NodeInfo> nodes;
            if (nodesToUse != null)
            {
                nodes = [.. nodesToUse];
            }
            else
            {
                nodes = await nodesProvider.BuildNodeInfoListAsync(cancellationToken);
            }

            if (nodes.Count == 0)
            {
                return [];
            }

            var foundSnapshots = new ConcurrentBag<SnapshotInfo>();

            // short circuiting the next priority storage search if higher priority storage search ended up with error.
            bool hasErrors = false;

            // Priority 1: Try to get snapshots from S3 (if configured)
            var isS3Available = await s3SnapshotService.CheckIsAvailableAsync(nodes[0].Namespace, cancellationToken);

            if (isS3Available)
            {
                try
                {
                    await GetSnapshotsFromS3Async(nodes, foundSnapshots, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching snapshots from S3");
                    hasErrors = true;
                }
            }

            // Priority 2: If S3 not available or no snapshots found, try Kubernetes storage (if we have pod names)
            if (foundSnapshots.IsEmpty && !hasErrors)
            {
                bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
                if (hasPodsWithNames)
                {
                    await Task.WhenAll(
                        nodes.Select(node => GetSnapshotsFromKubernetesAsync(node, foundSnapshots, cancellationToken))
                    );
                }
            }

            // Priority 3: If still no snapshots, try Qdrant API
            if (foundSnapshots.IsEmpty && !hasErrors)
            {
                try
                {
                    await GetSnapshotsFromQdrantApiAsync(nodes, foundSnapshots, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching snapshots from Qdrant API");
                    hasErrors = true;
                }
            }

            // Cache the result only when using provider nodes and no errors (so we don't mix filtered vs full node lists)
            if (!hasErrors && nodesToUse == null)
            {
                _snapshotsCache = foundSnapshots;
            }
            else if (hasErrors)
            {
                logger.LogWarning("Not caching snapshots due to errors during fetch");
            }

            return foundSnapshots;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Gets snapshot information with sizes for a collection on a specific node
    /// </summary>
    public async Task<List<(string Name, long Size)>> GetCollectionSnapshotsWithSizeAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.ListCollectionSnapshots(collectionName, cancellationToken);

            if (result?.Status?.IsSuccess == true && result.Result != null)
            {
                return [.. result.Result.Select(s => (s.Name, s.Size))];
            }

            logger.LogWarning(
                "Failed to get snapshots for collection {CollectionName}: {Error}",
                collectionName,
                result?.Status?.Error ?? MetricConstants.UnknownErrorMessage
            );
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to get snapshots for collection {CollectionName} on node {NodeUrl}",
                collectionName,
                nodeUrl
            );
            return [];
        }
    }

    public async Task<IEnumerable<SnapshotInfo>> GetSnapshotsFromDiskForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        ulong peerId,
        CancellationToken cancellationToken
    )
    {
        if (commandExecutor == null)
        {
            logger.LogWarning("Kubernetes client not available, cannot get snapshots from disk for pod {PodName}", podName);
            return [];
        }

        var snapshots = new List<SnapshotInfo>();

        try
        {
            var collectionFolders = await commandExecutor.ListFilesAsync(
                podName,
                podNamespace,
                QdrantConstants.SnapshotsPath,
                QdrantConstants.DirectoryPattern,
                cancellationToken
            );

            // Process each collection folder
            foreach (var collectionName in collectionFolders)
            {
                try
                {
                    var snapshotFiles = await commandExecutor.ListFilesAsync(
                        podName,
                        podNamespace,
                        $"{QdrantConstants.SnapshotsPath}/{collectionName}",
                        QdrantConstants.SnapshotFilePattern,
                        cancellationToken
                    );

                    foreach (
                        var snapshotFile in snapshotFiles.Where(f =>
                            f.EndsWith(QdrantConstants.SnapshotFilePattern.TrimStart('*'))
                        )
                    )
                    {
                        var sizeBytes = await commandExecutor.GetSizeAsync(
                            podName,
                            podNamespace,
                            $"{QdrantConstants.SnapshotsPath}/{collectionName}",
                            snapshotFile,
                            cancellationToken
                        );

                        if (sizeBytes.HasValue)
                        {
                            var snapshotInfo = new SnapshotInfo
                            {
                                PodName = podName,
                                NodeUrl = nodeUrl,
                                PeerId = peerId,
                                CollectionName = collectionName,
                                SnapshotName = snapshotFile,
                                SizeBytes = sizeBytes.Value,
                                PodNamespace = podNamespace,
                                CreatedAt = ParseSnapshotName(snapshotFile, collectionName).CreatedAt,
                            };

                            snapshots.Add(snapshotInfo);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Could not get size for snapshot {SnapshotFile} in collection {CollectionName}",
                                snapshotFile,
                                collectionName
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to get snapshots for collection {Collection} on pod {PodName}",
                        collectionName,
                        podName
                    );
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get snapshots from disk for pod {PodName}", podName);
        }

        logger.LogInformation("Found {Count} snapshots on pod {PodName}", snapshots.Count, podName);
        return snapshots;
    }

    public async Task<bool> DeleteSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            snapshotName,
            collectionName,
            podName,
            podNamespace
        );

        if (commandExecutor == null)
        {
            logger.LogError("Kubernetes client not available, cannot delete snapshot from disk");
            return false;
        }

        var fullPath = $"{QdrantConstants.SnapshotsPath}/{collectionName}/{snapshotName}";
        return await commandExecutor.DeleteAndVerifyAsync(
            podName,
            podNamespace,
            fullPath,
            isDirectory: false,
            $"Snapshot {snapshotName}",
            cancellationToken
        );
    }

    private async Task GetSnapshotsFromKubernetesAsync(
        NodeInfo node,
        ConcurrentBag<SnapshotInfo> foundSnapshots,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation(
            "Getting snapshots from node: URL={NodeUrl}, PeerId={PeerId}, Namespace={Namespace}, PodName={PodName}",
            node.Url,
            node.PeerId,
            node.Namespace,
            node.PodName
        );

        try
        {
            if (string.IsNullOrEmpty(node.PodName))
            {
                logger.LogWarning("Pod name is not available for node {NodeUrl}", node.Url);
                return;
            }

            logger.LogInformation("Found pod {PodName} for node {NodeUrl}, retrieving snapshots...", node.PodName, node.Url);

            var snapshots = await GetSnapshotsFromDiskForPodAsync(
                node.PodName,
                node.Namespace ?? "",
                node.Url,
                node.PeerId,
                cancellationToken
            );

            //var snapshotsList = snapshots.ToList();
            foreach (var snapshot in snapshots)
            {
                snapshot.Source = SnapshotSource.KubernetesStorage;
                foundSnapshots.Add(snapshot);
            }

            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get snapshots for node {NodeUrl}", node.Url);
            return;
        }
    }

    private async Task GetSnapshotsFromS3Async(
        List<NodeInfo> nodes,
        ConcurrentBag<SnapshotInfo> foundSnapshots,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("Fetching snapshots from S3 storage");

        var firstNode = nodes.FirstOrDefault();
        if (firstNode == null)
        {
            logger.LogWarning("No nodes available to get namespace");
            return;
        }

        // Get ALL snapshots from S3, not just for current collections
        // This way we show snapshots even for deleted/old collections
        var allS3Snapshots = await s3SnapshotService.ListAllSnapshotsAsync(firstNode.Namespace, cancellationToken);

        logger.LogInformation("Found {Count} snapshots in S3 storage", allS3Snapshots.Count);

        foreach (var (collectionName, snapshotName, sizeBytes, lastModifiedUtc) in allS3Snapshots)
        {
            var parsedSnapshotName = ParseSnapshotName(snapshotName, collectionName);

            var snapshotInfo = new SnapshotInfo
            {
                PodName = S3Constants.StorageIdentifier,
                NodeUrl = S3Constants.StorageIdentifier,
                PeerId = parsedSnapshotName.PeerId,
                CollectionName = collectionName,
                SnapshotName = snapshotName,
                SizeBytes = sizeBytes,
                PodNamespace = firstNode.Namespace ?? KubernetesConstants.DefaultNamespace,
                Source = SnapshotSource.S3Storage,
                CreatedAt = parsedSnapshotName.CreatedAt,
                S3StorageModifiedUtc = lastModifiedUtc,
            };

            foundSnapshots.Add(snapshotInfo);
        }
    }

    public async Task EnforceRetentionAsync(
        string collectionName,
        int retainLastN,
        CancellationToken cancellationToken,
        IReadOnlySet<ulong>? currentClusterPeerIds = null
    )
    {
        logger.LogInformation(
            "Enforcing retention of last {RetainLastN} snapshots per node for collection {CollectionName}",
            retainLastN,
            collectionName
        );

        var allSnapshots = await GetSnapshotsInfoAsync(cancellationToken, clearCache: true);
        var collectionSnapshots = allSnapshots.Where(s => s.CollectionName == collectionName).ToList();

        if (collectionSnapshots.Count == 0)
        {
            return;
        }

        // Group per-node: for Qdrant API / K8s — by NodeUrl; for S3 — by peerId extracted from the snapshot name
        // (S3 snapshots have PeerId = "S3", but the actual peer ID is always embedded in the snapshot name)
        var groups = collectionSnapshots.GroupBy(s =>
            s.Source == SnapshotSource.S3Storage ? ParseSnapshotName(s.SnapshotName, collectionName).PeerId.ToString() : s.NodeUrl
        );

        foreach (var snapshotGroup in groups)
        {
            // Delete all snapshots for orphaned peers (peer ID not in current cluster)
            if (
                currentClusterPeerIds != null
                && snapshotGroup.Key.Length > 0
                // Group key is either a ulong or a string. We can check currentClusterPeerIds only when the group.Key is ulong peer id
                && ulong.TryParse(snapshotGroup.Key, out var snapshotPeerId)
                && !currentClusterPeerIds.Contains(snapshotPeerId)
            )
            {
                logger.LogInformation(
                    "Deleting {Count} orphaned peer snapshots for collection {CollectionName} (peer {PeerId} not in current cluster)",
                    snapshotGroup.Count(),
                    collectionName,
                    snapshotPeerId
                );

                foreach (var snapshot in snapshotGroup)
                {
                    await DeleteSnapshotAsync(
                        collectionName,
                        snapshot.SnapshotName,
                        snapshot.Source,
                        nodeUrl: snapshot.NodeUrl == S3Constants.StorageIdentifier ? null : snapshot.NodeUrl,
                        podName: snapshot.PodName == S3Constants.StorageIdentifier ? null : snapshot.PodName,
                        podNamespace: snapshot.PodNamespace,
                        cancellationToken: cancellationToken
                    );
                }

                continue;
            }

            // Sort by parsed creation date; fall back to lexicographic order if date is unavailable
            var sorted = snapshotGroup
                .Select(s => (Snapshot: s, Info: ParseSnapshotName(s.SnapshotName, collectionName)))
                .OrderBy(x => x.Info.CreatedAt ?? DateTime.MinValue)
                .ThenBy(x => x.Snapshot.SnapshotName)
                .ToList();

            if (sorted.Count <= retainLastN)
            {
                continue;
            }

            var toDelete = sorted.Take(sorted.Count - retainLastN).Select(x => x.Snapshot).ToList();
            logger.LogInformation(
                "Deleting {DeleteCount} old snapshots for collection {CollectionName} on {NodeKey} (keeping last {RetainLastN})",
                toDelete.Count,
                collectionName,
                snapshotGroup.Key,
                retainLastN
            );

            foreach (var snapshot in toDelete)
            {
                await DeleteSnapshotAsync(
                    collectionName,
                    snapshot.SnapshotName,
                    snapshot.Source,
                    nodeUrl: snapshot.NodeUrl == S3Constants.StorageIdentifier ? null : snapshot.NodeUrl,
                    podName: snapshot.PodName == S3Constants.StorageIdentifier ? null : snapshot.PodName,
                    podNamespace: snapshot.PodNamespace,
                    cancellationToken: cancellationToken
                );
            }
        }
    }

    private async Task GetSnapshotsFromQdrantApiAsync(
        List<NodeInfo> nodes,
        ConcurrentBag<SnapshotInfo> foundSnapshots,
        CancellationToken cancellationToken
    )
    {
        logger.LogInformation("No snapshots found in Kubernetes storage, trying to get them from Qdrant API");

        var nodeResults = await Task.WhenAll(
            nodes.Select(node => GetSnapshotsFromSingleNodeQdrantAsync(node, cancellationToken))
        );

        var uniqueSnapshots = new HashSet<string>();
        var errorCount = 0;
        foreach (var (snapshots, error) in nodeResults)
        {
            if (error != null)
            {
                errorCount++;
            }

            foreach (var snapshotInfo in snapshots)
            {
                var uniqueKey = $"{snapshotInfo.NodeUrl}|{snapshotInfo.CollectionName}|{snapshotInfo.SnapshotName}";
                if (uniqueSnapshots.Add(uniqueKey))
                {
                    foundSnapshots.Add(snapshotInfo);
                }
            }
        }

        logger.LogInformation("Finished processing Qdrant API. Total snapshots collected: {Count}", foundSnapshots.Count);

        if (errorCount == nodes.Count && nodes.Count > 0)
        {
            var errors = nodeResults.Where(r => r.Error != null).Select(r => r.Error!).ToList();
            throw new AggregateException("Failed to get snapshots from all nodes via Qdrant API", errors);
        }
    }

    private async Task<(List<SnapshotInfo> Snapshots, Exception? Error)> GetSnapshotsFromSingleNodeQdrantAsync(
        NodeInfo node,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var qdrantClient = clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);
            var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);

            if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
            {
                logger.LogWarning(
                    "Failed to get collections from node {NodeUrl}: {Error}",
                    node.Url,
                    collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage
                );
                return ([], null);
            }

            var collections = collectionsResponse.Result.Collections;
            var collectionTasks = collections.Select(async collection =>
            {
                var collectionName = collection.Name;

                try
                {
                    var snapshotsWithSize = await GetCollectionSnapshotsWithSizeAsync(
                        node.Url,
                        collectionName,
                        cancellationToken
                    );

                    var infos = new List<SnapshotInfo>();
                    foreach (var (name, size) in snapshotsWithSize)
                    {
                        // If node PeerId is 0 we dem snapshot not belonging to this node
                        bool belongsToThisNode =
                            node.PeerId == 0 || name.Contains(node.PeerId.ToString(), StringComparison.OrdinalIgnoreCase);

                        if (!belongsToThisNode)
                        {
                            continue;
                        }

                        infos.Add(
                            new SnapshotInfo
                            {
                                PodName = node.PodName ?? MetricConstants.UnknownPodName,
                                NodeUrl = node.Url,
                                PeerId = node.PeerId,
                                CollectionName = collectionName,
                                SnapshotName = name,
                                SizeBytes = size,
                                PodNamespace = node.Namespace ?? "",
                                Source = SnapshotSource.QdrantApi,
                                CreatedAt = ParseSnapshotName(name, collectionName).CreatedAt,
                            }
                        );
                    }

                    return infos;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to get snapshots for collection {CollectionName} on node {NodeUrl}",
                        collectionName,
                        node.Url
                    );
                    return [];
                }
            });

            var collectionResults = await Task.WhenAll(collectionTasks);
            var snapshots = collectionResults.SelectMany(x => x).ToList();
            return (snapshots, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process node {NodeUrl} from Qdrant API", node.Url);
            return ([], ex);
        }
    }

    private async Task<HashSet<string>> BuildBaselineSnapshotKeysForCollectionAsync(
        string collectionName,
        List<string> nodeUrls,
        CancellationToken cancellationToken
    )
    {
        var nodes = nodeUrls.Select(u => new NodeInfo { Url = u }).ToList();
        try
        {
            var list = await GetSnapshotsInfoAsync(cancellationToken, true, nodesToUse: nodes).ConfigureAwait(false);
            return list.Where(s => string.Equals(s.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase))
                .Select(PendingSnapshotCreationJob.BaselineKey)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not list snapshots for baseline before create for {CollectionName}; pending job uses empty baseline",
                collectionName
            );
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Parses snapshot name into its constituent parts.
    /// Expected format: {collectionName}-{peerId}-{YYYY}-{MM}-{DD}-{HH}-{mm}-{ss}[.snapshot]
    /// </summary>
    private static ParsedSnapshotInfo ParseSnapshotName(ReadOnlySpan<char> snapshotName, string collectionName)
    {
        var collectionPrefix = collectionName + "-";

        if (!snapshotName.StartsWith(collectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Means that the snapshot name was not in expected format
            return new ParsedSnapshotInfo(CollectionName: collectionName, PeerId: 0, CreatedAt: null);
        }

        var peerIdAndTimestampRemainder = snapshotName[collectionPrefix.Length..];

        var nextDashLocation = peerIdAndTimestampRemainder.IndexOf('-');

        var peerIdPart = peerIdAndTimestampRemainder[..nextDashLocation];

        if (!ulong.TryParse(peerIdPart, out ulong peerId))
        {
            // Means that the snapshot name was not in expected format
            return new ParsedSnapshotInfo(CollectionName: collectionName, PeerId: 0, CreatedAt: null);
        }

        // might contain '.snapshot' extension
        var timestampRemainder = peerIdAndTimestampRemainder[(nextDashLocation + 1)..];

        if (timestampRemainder.EndsWith(".snapshot"))
        {
            // means extension is present
            timestampRemainder = timestampRemainder[..timestampRemainder.IndexOf(".snapshot")];
        }

        var dateTimePattern = "yyyy-MM-dd-HH-mm-ss";

        if (
            !DateTime.TryParseExact(
                timestampRemainder,
                dateTimePattern,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var createdAt
            )
        )
        {
            // Means timestamp in in unexpected format
            return new ParsedSnapshotInfo(collectionName, peerId, null);
        }

        return new ParsedSnapshotInfo(collectionName, peerId, createdAt);
    }
}
