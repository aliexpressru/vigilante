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
    ICollectionService collectionService,
    IOptions<QdrantOptions> options,
    IS3SnapshotService s3SnapshotService,
    ILogger<SnapshotService> logger) : ISnapshotService
{
    private readonly QdrantOptions _options = options.Value;
    private IReadOnlyList<SnapshotInfo>? _snapshotsCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Clears the snapshots cache
    /// </summary>
    public void ClearCache()
    {
        _cacheLock.Wait();
        try
        {
            _snapshotsCache = null;
            logger.LogInformation("Snapshots cache cleared");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Gets snapshots from cache or fetches them if cache is empty
    /// </summary>
    private async Task<IReadOnlyList<SnapshotInfo>> GetOrFetchSnapshotsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (forceRefresh || _snapshotsCache == null)
            {
                logger.LogInformation("Fetching snapshots (ForceRefresh: {ForceRefresh}, CacheEmpty: {CacheEmpty})",
                    forceRefresh, _snapshotsCache == null);
                var snapshots = await GetSnapshotsInfoAsync(forceRefresh, cancellationToken);
                _snapshotsCache = snapshots;
            }

            return _snapshotsCache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Gets paginated and filtered snapshots information
    /// </summary>
    public async Task<(IReadOnlyList<SnapshotInfo> Snapshots, int TotalCount)> GetSnapshotsInfoPaginatedAsync(
        int page,
        int pageSize,
        string? filter,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var allSnapshots = await GetOrFetchSnapshotsAsync(forceRefresh, cancellationToken);

        // Apply filter
        var filteredSnapshots = string.IsNullOrWhiteSpace(filter)
            ? allSnapshots
            : allSnapshots.Where(s => s.CollectionName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .AsReadOnly();

        var totalCount = filteredSnapshots.Count;

        // Apply pagination
        var paginatedSnapshots = filteredSnapshots
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .AsReadOnly();

        logger.LogInformation(
            "Returning page {Page} of snapshots (PageSize: {PageSize}, Filter: '{Filter}', TotalCount: {TotalCount}, PageCount: {PageCount})",
            page, pageSize, filter ?? "(none)", totalCount, paginatedSnapshots.Count);

        return (paginatedSnapshots, totalCount);
    }

    public async Task<Dictionary<string, string?>> CreateCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating snapshot for collection {CollectionName} on all nodes", collectionName);

        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        var results = new Dictionary<string, string?>();

        var createTasks = nodes.Select(async node =>
        {
            var nodeUrl = $"{QdrantConstants.HttpProtocol}{node.Host}:{node.Port}";
            var snapshotName = await collectionService.CreateCollectionSnapshotAsync(
                nodeUrl,
                collectionName,
                cancellationToken);

            return (NodeUrl: nodeUrl, SnapshotName: snapshotName);
        });

        var createResults = await Task.WhenAll(createTasks);

        foreach (var result in createResults)
        {
            results[result.NodeUrl] = result.SnapshotName;
        }

        var successCount = results.Values.Count(s => s != null);
        logger.LogInformation("Snapshot created for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes", 
            collectionName, successCount, results.Count);

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on all nodes", 
            snapshotName, collectionName);

        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        var results = new Dictionary<string, bool>();

        var deleteTasks = nodes.Select(async node =>
        {
            var nodeUrl = $"{QdrantConstants.HttpProtocol}{node.Host}:{node.Port}";
            var success = await collectionService.DeleteCollectionSnapshotAsync(
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
        logger.LogInformation("Snapshot {SnapshotName} deleted for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes", 
            snapshotName, collectionName, successCount, results.Count);

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

        if (source == SnapshotSource.S3Storage)
        {
            // Delete from S3 storage
            logger.LogInformation("Deleting snapshot {SnapshotName} from S3 storage", snapshotName);

            return await s3SnapshotService.DeleteSnapshotAsync(
                collectionName,
                snapshotName,
                podNamespace,
                cancellationToken);
        }
        else if (source == SnapshotSource.KubernetesStorage)
        {
            // Delete from Kubernetes storage (disk)
            if (string.IsNullOrEmpty(podName) || string.IsNullOrEmpty(podNamespace))
            {
                logger.LogError("PodName and PodNamespace are required for deleting snapshots from Kubernetes storage");
                return false;
            }

            logger.LogInformation("Deleting snapshot {SnapshotName} from Kubernetes storage on pod {PodName}", 
                snapshotName, podName);

            return await collectionService.DeleteSnapshotFromDiskAsync(
                podName,
                podNamespace,
                collectionName,
                snapshotName,
                cancellationToken);
        }
        else // SnapshotSource.QdrantApi
        {
            // Delete via Qdrant API (for S3 or API-managed snapshots)
            if (string.IsNullOrEmpty(nodeUrl))
            {
                logger.LogError("NodeUrl is required for deleting snapshots via Qdrant API");
                return false;
            }

            logger.LogInformation("Deleting snapshot {SnapshotName} via Qdrant API on node {NodeUrl}", 
                snapshotName, nodeUrl);

            return await collectionService.DeleteCollectionSnapshotAsync(
                nodeUrl,
                collectionName,
                snapshotName,
                cancellationToken);
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
            var apiStream = await collectionService.DownloadCollectionSnapshotAsync(
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

                var diskStream = await collectionService.DownloadSnapshotFromDiskAsync(
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
            var nodes = await BuildNodeInfoListAsync(cancellationToken);
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
                    logger.LogInformation("Fetching snapshots from Kubernetes storage");
                    try
                    {
                        await GetSnapshotsFromKubernetesStorageAsync(nodes, result, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error fetching snapshots from Kubernetes storage");
                        hasErrors = true;
                    }
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

    private async Task<List<NodeInfo>> BuildNodeInfoListAsync(CancellationToken cancellationToken)
    {
        var nodeConfigs = await nodesProvider.GetNodesAsync(cancellationToken);
        var nodes = new List<NodeInfo>();

        foreach (var nodeConfig in nodeConfigs)
        {
            var nodeUrl = $"{QdrantConstants.HttpProtocol}{nodeConfig.Host}:{nodeConfig.Port}";
            
            string? peerId = await GetPeerIdForNodeAsync(nodeUrl, nodeConfig, cancellationToken);

            nodes.Add(new NodeInfo
            {
                Url = nodeUrl,
                Namespace = nodeConfig.Namespace,
                PodName = nodeConfig.PodName,
                PeerId = peerId ?? "",
                IsHealthy = true,
                LastSeen = DateTime.UtcNow
            });
        }

        return nodes;
    }

    private async Task<string?> GetPeerIdForNodeAsync(string nodeUrl, QdrantNodeConfig nodeConfig, CancellationToken cancellationToken)
    {
        try
        {
            var client = clientFactory.CreateClient(nodeConfig.Host, nodeConfig.Port, _options.ApiKey);
            var clusterInfo = await client.GetClusterInfo(cancellationToken);
            
            if (clusterInfo.Status.IsSuccess && clusterInfo.Result?.PeerId != null)
            {
                return clusterInfo.Result.PeerId.ToString();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get cluster info for node {NodeUrl}", nodeUrl);
        }

        return null;
    }

    private async Task GetSnapshotsFromKubernetesStorageAsync(
        List<NodeInfo> nodes, 
        List<SnapshotInfo> result, 
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Attempting to get snapshots from Kubernetes storage for {NodeCount} nodes", nodes.Count);
        
        foreach (var node in nodes)
        {
            await ProcessNodeSnapshotsFromKubernetesAsync(node, result, cancellationToken);
        }
        
        logger.LogInformation("Finished processing all nodes. Total snapshots collected from k8s storage: {Count}", result.Count);
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
            
            var snapshots = await collectionService.GetSnapshotsFromDiskForPodAsync(
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
                PodName = "S3",
                NodeUrl = "S3",
                PeerId = "S3",
                CollectionName = collectionName,
                SnapshotName = snapshotName,
                SizeBytes = sizeBytes,
                PodNamespace = firstNode.Namespace ?? "qdrant",
                Source = SnapshotSource.S3Storage
            };

            result.Add(snapshotInfo);
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
                await ProcessNodeSnapshotsFromQdrantApiAsync(node, result, uniqueSnapshots, cancellationToken);
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

    private async Task ProcessNodeSnapshotsFromQdrantApiAsync(
        NodeInfo node, 
        List<SnapshotInfo> result, 
        HashSet<string> uniqueSnapshots, 
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching snapshots from Qdrant API for node {NodeUrl}", node.Url);
        
        var qdrantClient = clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);
        var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
        
        if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
        {
            logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}", 
                node.Url, collectionsResponse.Status?.Error ?? "Unknown error");
            return;
        }
        
        logger.LogInformation("Found {CollectionCount} collections on node {NodeUrl}", 
            collectionsResponse.Result.Collections.Length, node.Url);
        
        foreach (var collection in collectionsResponse.Result.Collections)
        {
            await ProcessCollectionSnapshotsFromApiAsync(
                node, 
                collection.Name, 
                result, 
                uniqueSnapshots, 
                cancellationToken);
        }
    }

    private async Task ProcessCollectionSnapshotsFromApiAsync(
        NodeInfo node,
        string collectionName,
        List<SnapshotInfo> result,
        HashSet<string> uniqueSnapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Getting snapshots with size info for collection {CollectionName} on node {NodeUrl}", 
                collectionName, node.Url);
            
            var snapshotsWithSize = await collectionService.GetCollectionSnapshotsWithSizeAsync(
                node.Url, 
                collectionName, 
                cancellationToken);
            
            logger.LogInformation("Found {SnapshotCount} snapshots for collection {CollectionName} on node {NodeUrl}", 
                snapshotsWithSize.Count, collectionName, node.Url);
            
            int matchedCount = AddSnapshotsToResult(node, collectionName, snapshotsWithSize, result, uniqueSnapshots);
            
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

    private int AddSnapshotsToResult(
        NodeInfo node,
        string collectionName,
        List<(string Name, long Size)> snapshotsWithSize,
        List<SnapshotInfo> result,
        HashSet<string> uniqueSnapshots)
    {
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
                PodName = node.PodName ?? "unknown",
                NodeUrl = node.Url,
                PeerId = node.PeerId,
                CollectionName = collectionName,
                SnapshotName = name,
                SizeBytes = size,
                PodNamespace = node.Namespace ?? "",
                Source = SnapshotSource.QdrantApi
            };
            
            result.Add(snapshotInfo);
            matchedCount++;
            logger.LogDebug("Added snapshot {SnapshotName} for collection {CollectionName} from Qdrant API (node: {PeerId}, size: {Size} bytes)", 
                name, collectionName, node.PeerId, size);
        }
        
        return matchedCount;
    }
}

