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
    ILogger<SnapshotService> logger) : ISnapshotService
{
    private readonly QdrantOptions _options = options.Value;

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

        if (source == SnapshotSource.KubernetesStorage)
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
            "Downloading snapshot {SnapshotName} for collection {CollectionName} from {NodeUrl} with fallback",
            snapshotName, collectionName, nodeUrl);

        // Try API first
        try
        {
            logger.LogDebug("Attempting to download snapshot via API");
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

        // Fallback to disk if API fails
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
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetSnapshotsInfoAsync");

        var nodes = await BuildNodeInfoListAsync(cancellationToken);
        logger.LogInformation("Found {NodesCount} nodes to process", nodes.Count);
        
        var result = new List<SnapshotInfo>();
        bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
        
        // Priority 1: Try to get snapshots from Kubernetes storage (if we have pod names)
        if (hasPodsWithNames)
        {
            await GetSnapshotsFromKubernetesStorageAsync(nodes, result, cancellationToken);
        }
        
        // Priority 2: If we didn't get any snapshots from k8s storage, try to get them from Qdrant API
        if (result.Count == 0)
        {
            await GetSnapshotsFromQdrantApiAsync(nodes, result, cancellationToken);
        }

        logger.LogInformation("GetSnapshotsInfoAsync completed. Total snapshots: {Count}", result.Count);
        return result;
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

    private async Task GetSnapshotsFromQdrantApiAsync(
        List<NodeInfo> nodes, 
        List<SnapshotInfo> result, 
        CancellationToken cancellationToken)
    {
        logger.LogInformation("No snapshots found in Kubernetes storage, trying to get them from Qdrant API");
        
        var uniqueSnapshots = new HashSet<string>();
        
        foreach (var node in nodes)
        {
            await ProcessNodeSnapshotsFromQdrantApiAsync(node, result, uniqueSnapshots, cancellationToken);
        }
        
        logger.LogInformation("Finished processing Qdrant API. Total snapshots collected: {Count}", result.Count);
    }

    private async Task ProcessNodeSnapshotsFromQdrantApiAsync(
        NodeInfo node, 
        List<SnapshotInfo> result, 
        HashSet<string> uniqueSnapshots, 
        CancellationToken cancellationToken)
    {
        try
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get snapshots from Qdrant API for node {NodeUrl}", node.Url);
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

