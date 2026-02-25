using System.Text.RegularExpressions;
using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Service for managing collection snapshots across the Qdrant cluster
/// </summary>
public class SnapshotService(
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    IS3SnapshotService s3SnapshotService,
    IPodCommandExecutor? commandExecutor,
    IOptions<QdrantOptions> options,
    ILogger<SnapshotService> logger) : ISnapshotService
{
    private readonly QdrantOptions _options = options.Value;
    private IReadOnlyList<SnapshotInfo>? _snapshotsCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Creates a snapshot of a collection on a specific node
    /// </summary>
    public async Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken,
        bool waitForResult = false)
    {
        try
        {
            logger.LogInformation("Creating snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.CreateCollectionSnapshot(
                collectionName, 
                cancellationToken,
                isWaitForResult: waitForResult);
            
            if (result.IsAcceptedOrSuccess())
            {
                var snapshotName = result.Result?.Name ?? $"{collectionName}-snapshot-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var statusText = result.IsAccepted() ? QdrantConstants.SnapshotAcceptedStatus : QdrantConstants.SnapshotCreatedStatus;
                
                logger.LogInformation("Snapshot {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    statusText, collectionName, nodeUrl);
                return snapshotName;
            }

            logger.LogError("Failed to create snapshot for collection {CollectionName} on node {NodeUrl}: {Error}",
                collectionName, nodeUrl, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return null;
        }
    }

    public async Task<Dictionary<string, string?>> CreateCollectionSnapshotAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default,
        bool waitForResult = false)
    {
        var nodeUrlsList = nodeUrls.ToList();
        logger.LogInformation(
            "Creating snapshot for collection {CollectionName} on {NodeCount} specified nodes", 
            collectionName, 
            nodeUrlsList.Count);

        var results = new Dictionary<string, string?>();

        var createTasks = nodeUrlsList.Select(async nodeUrl =>
        {
            var snapshotName = await CreateCollectionSnapshotAsync(
                nodeUrl,
                collectionName,
                cancellationToken,
                waitForResult);

            return (NodeUrl: nodeUrl, SnapshotName: snapshotName);
        });

        var createResults = await Task.WhenAll(createTasks);

        foreach (var result in createResults)
        {
            results[result.NodeUrl] = result.SnapshotName;
        }

        var successCount = results.Values.Count(s => s != null);
        logger.LogInformation(
            "Snapshot created for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes", 
            collectionName, 
            successCount, 
            results.Count);

        return results;
    }
    
    /// <summary>
    /// Deletes a snapshot for a collection on a specific node
    /// </summary>
    public async Task<bool> DeleteCollectionSnapshotApiAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.DeleteCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? QdrantConstants.SnapshotDeletionAcceptedStatus : QdrantConstants.SnapshotDeletedStatus;
                logger.LogInformation("Snapshot {SnapshotName} {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    snapshotName, statusText, collectionName, nodeUrl);
                return true;
            }
            
            logger.LogError("Failed to delete snapshot {SnapshotName} for collection {CollectionName}: {Error}",
                snapshotName, collectionName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionSnapshotApiAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default)
    {
        var nodeUrlsList = nodeUrls.ToList();
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} via API on {NodeCount} specified nodes", 
            snapshotName, 
            collectionName, 
            nodeUrlsList.Count);

        var results = new Dictionary<string, bool>();

        var deleteTasks = nodeUrlsList.Select(async nodeUrl =>
        {
            var success = await DeleteCollectionSnapshotApiAsync(
                nodeUrl,
                collectionName,
                snapshotName,
                cancellationToken);

            return (NodeUrl: nodeUrl, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var result in deleteResults)
        {
            results[result.NodeUrl] = result.Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Snapshot {SnapshotName} deleted via API: {SuccessCount}/{TotalCount} nodes", 
            snapshotName, 
            successCount, 
            results.Count);

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteSnapshotFromDiskAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        CancellationToken cancellationToken = default)
    {
        var podsList = pods.ToList();
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on {PodCount} specified pods", 
            snapshotName, 
            collectionName, 
            podsList.Count);

        var results = new Dictionary<string, bool>();

        var deleteTasks = podsList.Select(async pod =>
        {
            var success = await DeleteSnapshotFromDiskAsync(
                pod.PodName,
                pod.PodNamespace,
                collectionName,
                snapshotName,
                cancellationToken);

            return (PodName: pod.PodName, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var result in deleteResults)
        {
            results[result.PodName] = result.Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Snapshot {SnapshotName} deleted from disk: {SuccessCount}/{TotalCount} pods", 
            snapshotName, 
            successCount, 
            results.Count);

        return results;
    }

    public async Task<bool> DeleteSnapshotAsync(
        string collectionName,
        string snapshotName,
        SnapshotSource source,
        string? nodeUrl = null,
        string? podName = null,
        string? podNamespace = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} (source: {Source})", 
            snapshotName, collectionName, source);

        switch (source)
        {
            case SnapshotSource.S3Storage:
                // Delete from S3 storage
                logger.LogInformation("Deleting snapshot {SnapshotName} from S3 storage", snapshotName);

                return await s3SnapshotService.DeleteSnapshotAsync(
                    collectionName,
                    snapshotName,
                    podNamespace,
                    cancellationToken);
            // Delete from Kubernetes storage (disk)
            case SnapshotSource.KubernetesStorage when string.IsNullOrEmpty(podName) || string.IsNullOrEmpty(podNamespace):
                logger.LogError("PodName and PodNamespace are required for deleting snapshots from Kubernetes storage");
                return false;
            case SnapshotSource.KubernetesStorage:
                logger.LogInformation("Deleting snapshot {SnapshotName} from Kubernetes storage on pod {PodName}", 
                    snapshotName, podName);

                return await DeleteSnapshotFromDiskAsync(
                    podName,
                    podNamespace,
                    collectionName,
                    snapshotName,
                    cancellationToken);
            // SnapshotSource.QdrantApi
            default:
            {
                // Delete via Qdrant API (for S3 or API-managed snapshots)
                if (string.IsNullOrEmpty(nodeUrl))
                {
                    logger.LogError("NodeUrl is required for deleting snapshots via Qdrant API");
                    return false;
                }

                logger.LogInformation("Deleting snapshot {SnapshotName} via Qdrant API on node {NodeUrl}", 
                    snapshotName, nodeUrl);

                return await DeleteCollectionSnapshotApiAsync(
                    nodeUrl,
                    collectionName,
                    snapshotName,
                    cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            
            var result = await qdrantClient.DownloadCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken);
            
            if (result?.Result?.SnapshotDataStream != null)
            {
                logger.LogInformation("Snapshot {SnapshotName} downloaded successfully for collection {CollectionName} from node {NodeUrl}", 
                    snapshotName, collectionName, nodeUrl);
                return result.Result.SnapshotDataStream;
            }
            
            logger.LogError("Failed to download snapshot {SnapshotName} for collection {CollectionName}: empty or null result",
                snapshotName, collectionName);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
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
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}", 
                snapshotName, collectionName, podName, podNamespace);

            var snapshotPath = $"{QdrantConstants.SnapshotsPath}/{collectionName}/{snapshotName}";
            
            logger.LogInformation("Starting download: {SnapshotPath} from pod {PodName}", snapshotPath, podName);

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
                cancellationToken);

            if (expectedSize.HasValue)
            {
                logger.LogInformation("Got expected file size: {Size} bytes ({FormattedSize})", 
                    expectedSize.Value, expectedSize.Value.ToPrettySize());
            }
            else
            {
                logger.LogWarning("Could not get file size from pod - will download without size limit!");
            }

            // Get checksum for verification
            var checksumPath = $"{snapshotPath}.checksum";
            var expectedChecksum = await commandExecutor.GetFileContentAsync(
                podName,
                podNamespace,
                checksumPath,
                cancellationToken);

            if (!string.IsNullOrEmpty(expectedChecksum))
            {
                logger.LogInformation("Expected checksum: {Checksum}", expectedChecksum);
            }

            // Download file using cat command
            var snapshotStream = await commandExecutor.DownloadFileAsync(
                podName,
                podNamespace,
                snapshotPath,
                expectedSize,
                cancellationToken);

            if (snapshotStream == null)
            {
                logger.LogError("Failed to download snapshot {SnapshotName} from disk on pod {PodName}", 
                    snapshotName, podName);
                return null;
            }

            logger.LogInformation("Snapshot {SnapshotName} download stream started successfully from disk on pod {PodName} in namespace {Namespace}", 
                snapshotName, podName, podNamespace);

            return snapshotStream;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download snapshot {SnapshotName} from disk on pod {PodName} in namespace {Namespace}", 
                snapshotName, podName, podNamespace);
            return null;
        }
    }

    public async Task<Stream?> DownloadSnapshotWithFallbackAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        string? podName = null,
        string? podNamespace = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Downloading snapshot {SnapshotName} for collection {CollectionName} with fallback (S3 → API → Disk)",
            snapshotName, collectionName);

        // Priority 1: Try S3 first if available
        var isS3Available = await s3SnapshotService.IsAvailableAsync(podNamespace, cancellationToken);
        if (isS3Available)
        {
            try
            {
                logger.LogDebug("Attempting to download snapshot from S3");
                var s3Stream = await s3SnapshotService.DownloadSnapshotAsync(
                    collectionName,
                    snapshotName,
                    podNamespace,
                    cancellationToken);

                if (s3Stream != null)
                {
                    logger.LogInformation("Successfully downloaded snapshot from S3");
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
            logger.LogDebug("Attempting to download snapshot via API from {NodeUrl}", nodeUrl);
            var apiStream = await DownloadCollectionSnapshotAsync(
                nodeUrl,
                collectionName,
                snapshotName,
                cancellationToken);

            if (apiStream != null)
            {
                logger.LogInformation("Successfully downloaded snapshot via API");
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
                logger.LogDebug("Attempting to download snapshot from disk: Pod={PodName}, Namespace={Namespace}",
                    podName, podNamespace);

                var diskStream = await DownloadSnapshotFromDiskAsync(
                    podName,
                    podNamespace,
                    collectionName,
                    snapshotName,
                    cancellationToken);

                if (diskStream != null)
                {
                    logger.LogInformation("Successfully downloaded snapshot from disk");
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

    public async Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(
        bool clearCache = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetSnapshotsInfoAsync (ClearCache: {ClearCache})", clearCache);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            // Clear cache if requested
            if (clearCache)
            {
                logger.LogInformation("Clearing snapshots cache");
                _snapshotsCache = null;
            }

            // Return cached data if available
            if (_snapshotsCache != null)
            {
                logger.LogInformation("Returning {Count} snapshots from cache", _snapshotsCache.Count);
                return _snapshotsCache;
            }

            // Fetch fresh data
            var nodes = await nodesProvider.BuildNodeInfoListAsync(cancellationToken);
            logger.LogInformation("Found {NodesCount} nodes to process", nodes.Count);
            
            var result = new List<SnapshotInfo>();
            bool hasErrors = false;
            
            // Priority 1: Try to get snapshots from S3 (if configured)
            var isS3Available = await s3SnapshotService.IsAvailableAsync(
                nodes.FirstOrDefault()?.Namespace, 
                cancellationToken);
                
            if (isS3Available)
            {
                logger.LogInformation("S3 storage is available, fetching snapshots from S3");
                try
                {
                    await GetSnapshotsFromS3Async(nodes, result, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching snapshots from S3");
                    hasErrors = true;
                }
            }
            
            // Priority 2: If S3 not available or no snapshots found, try Kubernetes storage (if we have pod names)
            if (result.Count == 0 && !hasErrors)
            {
                bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
                if (hasPodsWithNames)
                {
                    logger.LogInformation("Fetching snapshots from Kubernetes storage for {NodeCount} nodes", nodes.Count);
                    
                    foreach (var node in nodes)
                    {
                        await ProcessNodeSnapshotsFromKubernetesAsync(node, result, cancellationToken);
                    }
                    
                    logger.LogInformation("Finished processing all nodes. Total snapshots collected from k8s storage: {Count}", result.Count);
                }
            }
            
            // Priority 3: If still no snapshots, try Qdrant API
            if (result.Count == 0 && !hasErrors)
            {
                logger.LogInformation("Fetching snapshots from Qdrant API");
                try
                {
                    await GetSnapshotsFromQdrantApiAsync(nodes, result, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching snapshots from Qdrant API");
                    hasErrors = true;
                }
            }

            // Cache the result only if we successfully fetched data (even if empty but without errors)
            // If there were errors, don't cache so next request will try again
            if (!hasErrors)
            {
                _snapshotsCache = result;
                logger.LogInformation("Cached {Count} snapshots", result.Count);
            }
            else
            {
                logger.LogWarning("Not caching snapshots due to errors during fetch");
            }

            logger.LogInformation("GetSnapshotsInfoAsync completed. Total snapshots: {Count}", result.Count);
            return result;
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
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Getting snapshots with size info for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            var qdrantClient = clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.ListCollectionSnapshots(collectionName, cancellationToken);
            
            if (result?.Status?.IsSuccess == true && result.Result != null)
            {
                var snapshots = result.Result
                    .Select(s => (s.Name, s.Size))
                    .ToList();
                
                logger.LogDebug("Found {Count} snapshots with size info for collection {CollectionName} on node {NodeUrl}", 
                    snapshots.Count, collectionName, nodeUrl);
                return snapshots;
            }
            
            logger.LogWarning("Failed to get snapshots for collection {CollectionName}: {Error}",
                collectionName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return new List<(string Name, long Size)>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get snapshots for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return new List<(string Name, long Size)>();
        }
    }

    public async Task<IEnumerable<SnapshotInfo>> GetSnapshotsFromDiskForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting to get snapshots from disk for pod {PodName} (Node URL {NodeUrl}) in namespace {Namespace}",
            podName, nodeUrl, podNamespace);

        if (commandExecutor == null)
        {
            logger.LogWarning("Kubernetes client not available, cannot get snapshots from disk for pod {PodName}", podName);
            return [];
        }

        var snapshots = new List<SnapshotInfo>();

        try
        {
            logger.LogInformation("Listing collection folders in {SnapshotsPath} on pod {PodName}", 
                QdrantConstants.SnapshotsPath, podName);
            
            var collectionFolders = await commandExecutor.ListFilesAsync(
                podName,
                podNamespace,
                QdrantConstants.SnapshotsPath,
                QdrantConstants.DirectoryPattern,
                cancellationToken);

            logger.LogInformation("Found {Count} collection folders in snapshots directory on pod {PodName}: {Folders}", 
                collectionFolders.Count, podName, string.Join(", ", collectionFolders));

            // Process each collection folder
            foreach (var collectionName in collectionFolders)
            {
                try
                {
                    logger.LogInformation("Listing snapshot files in {SnapshotsPath}/{CollectionName} on pod {PodName}", 
                        QdrantConstants.SnapshotsPath, collectionName, podName);
                    
                    var snapshotFiles = await commandExecutor.ListFilesAsync(
                        podName,
                        podNamespace,
                        $"{QdrantConstants.SnapshotsPath}/{collectionName}",
                        QdrantConstants.SnapshotFilePattern,
                        cancellationToken);

                    logger.LogInformation("Found {Count} snapshot files for collection {CollectionName} on pod {PodName}: {Files}", 
                        snapshotFiles.Count, collectionName, podName, string.Join(", ", snapshotFiles));

                    // Process each snapshot file in the collection
                    foreach (var snapshotFile in snapshotFiles.Where(f => f.EndsWith(QdrantConstants.SnapshotFilePattern.TrimStart('*'))))
                    {
                        logger.LogDebug("Getting size for snapshot {SnapshotFile} in {CollectionName}", 
                            snapshotFile, collectionName);
                        
                        var sizeBytes = await commandExecutor.GetSizeAsync(
                            podName,
                            podNamespace,
                            $"{QdrantConstants.SnapshotsPath}/{collectionName}",
                            snapshotFile,
                            cancellationToken);

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
                                CreatedAt = ParseSnapshotName(snapshotFile, collectionName).CreatedAt
                            };

                            snapshots.Add(snapshotInfo);
                            logger.LogInformation("Added snapshot {SnapshotName} for collection {CollectionName}: {Size} bytes ({PrettySize})", 
                                snapshotFile, collectionName, sizeBytes.Value, snapshotInfo.PrettySize);
                        }
                        else
                        {
                            logger.LogWarning("Could not get size for snapshot {SnapshotFile} in collection {CollectionName}", 
                                snapshotFile, collectionName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get snapshots for collection {Collection} on pod {PodName}",
                        collectionName, podName);
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
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            snapshotName, collectionName, podName, podNamespace);

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
            cancellationToken);
    }

    private async Task ProcessNodeSnapshotsFromKubernetesAsync(
        NodeInfo node, 
        List<SnapshotInfo> result, 
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Processing node for snapshots: URL={NodeUrl}, PeerId={PeerId}, Namespace={Namespace}, PodName={PodName}", 
            node.Url, node.PeerId, node.Namespace, node.PodName);

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
                cancellationToken);
            
            var snapshotsList = snapshots.ToList();
            
            // Mark all snapshots from disk with KubernetesStorage source
            foreach (var snapshot in snapshotsList)
            {
                snapshot.Source = SnapshotSource.KubernetesStorage;
            }
            
            logger.LogInformation("Retrieved {SnapshotsCount} snapshots from pod {PodName} (Node: {NodeUrl})", 
                snapshotsList.Count, node.PodName, node.Url);

            if (snapshotsList.Count > 0)
            {
                logger.LogDebug("Snapshots from pod {PodName}: {SnapshotNames}", 
                    node.PodName, string.Join(", ", snapshotsList.Select(s => s.SnapshotName)));
            }

            result.AddRange(snapshotsList);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get snapshots for node {NodeUrl}", node.Url);
        }
    }

    private async Task GetSnapshotsFromS3Async(
        List<NodeInfo> nodes,
        List<SnapshotInfo> result,
        CancellationToken cancellationToken)
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
        var allSnapshots = await s3SnapshotService.ListAllSnapshotsAsync(
            firstNode.Namespace,
            cancellationToken);

        logger.LogInformation("Found {Count} snapshots in S3 storage", allSnapshots.Count);

        foreach (var (collectionName, snapshotName, sizeBytes) in allSnapshots)
        {
            var snapshotInfo = new SnapshotInfo
            {
                PodName = S3Constants.StorageIdentifier,
                NodeUrl = S3Constants.StorageIdentifier,
                PeerId = S3Constants.StorageIdentifier,
                CollectionName = collectionName,
                SnapshotName = snapshotName,
                SizeBytes = sizeBytes,
                PodNamespace = firstNode.Namespace ?? KubernetesConstants.DefaultNamespace,
                Source = SnapshotSource.S3Storage,
                CreatedAt = ParseSnapshotName(snapshotName, collectionName).CreatedAt
            };

            result.Add(snapshotInfo);
        }
    }

    public async Task EnforceRetentionAsync(
        string collectionName,
        int retainLastN,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Enforcing retention of last {RetainLastN} snapshots per node for collection {CollectionName}",
            retainLastN, collectionName);

        var allSnapshots = await GetSnapshotsInfoAsync(clearCache: true, cancellationToken);
        var collectionSnapshots = allSnapshots.Where(s => s.CollectionName == collectionName).ToList();

        if (collectionSnapshots.Count == 0)
            return;

        // Group per-node: for Qdrant API / K8s — by NodeUrl; for S3 — by peerId extracted from the snapshot name
        // (S3 snapshots have PeerId = "S3", but the actual peer ID is always embedded in the snapshot name)
        var groups = collectionSnapshots.GroupBy(s =>
            s.Source == SnapshotSource.S3Storage
                ? ParseSnapshotName(s.SnapshotName, collectionName).PeerId
                : s.NodeUrl);

        foreach (var group in groups)
        {
            // Sort by parsed creation date; fall back to lexicographic order if date is unavailable
            var sorted = group
                .Select(s => (Snapshot: s, Info: ParseSnapshotName(s.SnapshotName, collectionName)))
                .OrderBy(x => x.Info.CreatedAt ?? DateTime.MinValue)
                .ThenBy(x => x.Snapshot.SnapshotName)
                .ToList();

            if (sorted.Count <= retainLastN)
                continue;

            var toDelete = sorted.Take(sorted.Count - retainLastN).Select(x => x.Snapshot).ToList();
            logger.LogInformation(
                "Deleting {DeleteCount} old snapshots for collection {CollectionName} on {NodeKey} (keeping last {RetainLastN})",
                toDelete.Count, collectionName, group.Key, retainLastN);

            foreach (var snapshot in toDelete)
            {
                await DeleteSnapshotAsync(
                    collectionName,
                    snapshot.SnapshotName,
                    snapshot.Source,
                    nodeUrl: snapshot.NodeUrl == S3Constants.StorageIdentifier ? null : snapshot.NodeUrl,
                    podName: snapshot.PodName == S3Constants.StorageIdentifier ? null : snapshot.PodName,
                    podNamespace: snapshot.PodNamespace,
                    cancellationToken: cancellationToken);
            }
        }
    }

    private async Task GetSnapshotsFromQdrantApiAsync(
        List<NodeInfo> nodes, 
        List<SnapshotInfo> result, 
        CancellationToken cancellationToken)
    {
        logger.LogInformation("No snapshots found in Kubernetes storage, trying to get them from Qdrant API");
        
        var uniqueSnapshots = new HashSet<string>();
        var errors = new List<Exception>();
        
        foreach (var node in nodes)
        {
            try
            {
                logger.LogInformation("Fetching snapshots from Qdrant API for node {NodeUrl}", node.Url);
                
                var qdrantClient = clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);
                var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
                
                if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
                {
                    logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}", 
                        node.Url, collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage);
                    continue;
                }
                
                logger.LogInformation("Found {CollectionCount} collections on node {NodeUrl}", 
                    collectionsResponse.Result.Collections.Length, node.Url);
                
                foreach (var collection in collectionsResponse.Result.Collections)
                {
                    var collectionName = collection.Name;
                    
                    try
                    {
                        logger.LogDebug("Getting snapshots with size info for collection {CollectionName} on node {NodeUrl}", 
                            collectionName, node.Url);
                        
                        var snapshotsWithSize = await GetCollectionSnapshotsWithSizeAsync(
                            node.Url, 
                            collectionName, 
                            cancellationToken);
                        
                        logger.LogInformation("Found {SnapshotCount} snapshots for collection {CollectionName} on node {NodeUrl}", 
                            snapshotsWithSize.Count, collectionName, node.Url);
                        
                        // Process each snapshot and add to result if it belongs to this node
                        int matchedCount = 0;
                        
                        foreach (var (name, size) in snapshotsWithSize)
                        {
                            // Check if snapshot belongs to this node (by PeerId in snapshot name)
                            bool belongsToThisNode = string.IsNullOrEmpty(node.PeerId) || 
                                                    name.Contains(node.PeerId, StringComparison.OrdinalIgnoreCase);
                            
                            if (!belongsToThisNode)
                            {
                                logger.LogTrace("Skipping snapshot {SnapshotName} - does not belong to node {PeerId}", 
                                    name, node.PeerId);
                                continue;
                            }
                            
                            // Create unique key to prevent duplicates
                            var uniqueKey = $"{node.Url}|{collectionName}|{name}";
                            
                            if (!uniqueSnapshots.Add(uniqueKey))
                            {
                                logger.LogTrace("Skipping duplicate snapshot {SnapshotName} for node {NodeUrl}", 
                                    name, node.Url);
                                continue;
                            }
                            
                            var snapshotInfo = new SnapshotInfo
                            {
                                PodName = node.PodName ?? MetricConstants.UnknownPodName,
                                NodeUrl = node.Url,
                                PeerId = node.PeerId,
                                CollectionName = collectionName,
                                SnapshotName = name,
                                SizeBytes = size,
                                PodNamespace = node.Namespace ?? "",
                                Source = SnapshotSource.QdrantApi,
                                CreatedAt = ParseSnapshotName(name, collectionName).CreatedAt
                            };
                            
                            result.Add(snapshotInfo);
                            matchedCount++;
                            logger.LogDebug("Added snapshot {SnapshotName} for collection {CollectionName} from Qdrant API (node: {PeerId}, size: {Size} bytes)", 
                                name, collectionName, node.PeerId, size);
                        }
                        
                        if (matchedCount < snapshotsWithSize.Count)
                        {
                            logger.LogInformation("Filtered {FilteredCount} out of {TotalCount} snapshots for collection {CollectionName} on node {NodeUrl} (matched by PeerId: {MatchedCount})", 
                                snapshotsWithSize.Count - matchedCount, snapshotsWithSize.Count, collectionName, node.Url, matchedCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to get snapshots for collection {CollectionName} on node {NodeUrl}", 
                            collectionName, node.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process node {NodeUrl} from Qdrant API", node.Url);
                errors.Add(ex);
            }
        }
        
        logger.LogInformation("Finished processing Qdrant API. Total snapshots collected: {Count}", result.Count);
        
        // If all nodes failed, throw to prevent caching empty result
        if (errors.Count == nodes.Count && nodes.Count > 0)
        {
            throw new AggregateException("Failed to get snapshots from all nodes via Qdrant API", errors);
        }
    }

    private sealed record SnapshotParsedInfo(string CollectionName, string PeerId, DateTime? CreatedAt);

    /// <summary>
    /// Parses snapshot name into its constituent parts.
    /// Expected format: {collectionName}-{peerId}-{YYYY}-{MM}-{DD}-{HH}-{mm}-{ss}[.snapshot]
    /// </summary>
    private static SnapshotParsedInfo ParseSnapshotName(string snapshotName, string collectionName)
    {
        var prefix = collectionName + "-";
        if (!snapshotName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new SnapshotParsedInfo(collectionName, snapshotName, null);
        }

        var remainder = snapshotName[prefix.Length..];

        // Timestamp format: -YYYY-MM-DD-HH-mm-ss (followed by optional .snapshot extension)
        var match = Regex.Match(remainder, @"-(20\d{2})-(\d{2})-(\d{2})-(\d{2})-(\d{2})-(\d{2})");
        if (!match.Success)
        {
            return new SnapshotParsedInfo(collectionName, remainder, null);
        }

        var peerId = remainder[..match.Index];

        DateTime? createdAt = null;
        if (int.TryParse(match.Groups[1].Value, out var year)
            && int.TryParse(match.Groups[2].Value, out var month)
            && int.TryParse(match.Groups[3].Value, out var day)
            && int.TryParse(match.Groups[4].Value, out var hour)
            && int.TryParse(match.Groups[5].Value, out var minute)
            && int.TryParse(match.Groups[6].Value, out var second))
        {
            try { createdAt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc); }
            catch { /* ignore invalid dates */ }
        }

        return new SnapshotParsedInfo(collectionName, peerId, createdAt);
    }
}
