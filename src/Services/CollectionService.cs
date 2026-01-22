using Aer.QdrantClient.Http.Abstractions;
using k8s;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class CollectionService : ICollectionService
{
    private readonly ILogger<CollectionService> _logger;
    private readonly IMeterService _meterService;
    private readonly IPodCommandExecutor? _commandExecutor;
    private readonly QdrantOptions _options;
    private readonly IQdrantClientFactory _clientFactory;

    public CollectionService(
        ILogger<CollectionService> logger,
        IMeterService meterService,
        IQdrantClientFactory clientFactory,
        IOptions<QdrantOptions> options,
        ILogger<PodCommandExecutor> commandExecutorLogger)
    {
        _logger = logger;
        _meterService = meterService;
        _clientFactory = clientFactory;
        _options = options.Value;

        // Try to initialize Kubernetes client and command executor only if we're running in a cluster
        try
        {
            var kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
            _commandExecutor = new PodCommandExecutor(kubernetes, commandExecutorLogger);
        }
        catch (k8s.Exceptions.KubeConfigException)
        {
            _logger.LogWarning("Not running in Kubernetes cluster, collection size monitoring will be disabled");
            _commandExecutor = null;
        }
    }

    public async Task<bool> ReplicateShardsAsync(
        string healthyNodeUrl,
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            var result = await qdrantClient.ReplicateShards(
                sourcePeerId: sourcePeerId,
                targetPeerId: targetPeerId,
                collectionNamesToReplicate: new[] { collectionName },
                shardIdsToReplicate: shardIds,
                isMoveShards: isMove,
                cancellationToken: cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Shard replication initiated: {Collection} [{SourcePeer}→{TargetPeer}]",
                    collectionName, sourcePeerId, targetPeerId);

                return true;
            }

            _logger.LogError("Failed to replicate shards for {Collection}: {Error}",
                collectionName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to replicate shards for collection {Collection}", collectionName);

            return false;
        }
    }

    public async Task<IEnumerable<CollectionSize>> GetCollectionsSizesForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting to get collection sizes for pod {PodName} (Node URL {NodeUrl}) in namespace {Namespace}",
            podName, nodeUrl, podNamespace);

        if (_commandExecutor == null)
        {
            return [];
        }

        var sizes = new List<CollectionSize>();

        try
        {
            var collections = await _commandExecutor.ListDirectoriesAsync(
                podName,
                podNamespace,
                QdrantConstants.StoragePath,
                cancellationToken);

            _logger.LogDebug("Found {Count} collections on pod {PodName}", collections.Count, podName);

            foreach (var collection in collections)
            {
                var sizeBytes = await _commandExecutor.GetSizeAsync(
                    podName,
                    podNamespace,
                    QdrantConstants.StoragePath,
                    collection,
                    cancellationToken);

                if (sizeBytes.HasValue)
                {
                    var collectionSize = new CollectionSize
                    {
                        PodName = podName,
                        NodeUrl = nodeUrl,
                        PeerId = peerId,
                        CollectionName = collection,
                        SizeBytes = sizeBytes.Value
                    };

                    sizes.Add(collectionSize);
                    _meterService.UpdateCollectionSize(collectionSize);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection sizes for pod {PodName}", podName);
        }

        return sizes;
    }

    public async Task<(bool IsHealthy, string? ErrorMessage)> CheckCollectionsHealthAsync(IQdrantHttpClient client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Checking collections health");
            var collectionsResponse = await client.ListCollections(cancellationToken);

            if (!collectionsResponse.Status.IsSuccess)
            {
                var errorDetails = collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage;

                _logger.LogWarning("Collections health check failed: {Error}", errorDetails);

                return (false, $"Failed to list collections: {errorDetails}");
            }

            // If there are collections, check each one in parallel
            if (collectionsResponse.Result?.Collections != null && collectionsResponse.Result.Collections.Any())
            {
                var collections = collectionsResponse.Result.Collections;

                _logger.LogDebug("Checking health for {CollectionCount} collections in parallel", collections.Length);
                // Create tasks for all collection health checks
                var checkTasks = collections.Select(async collection =>
                {
                    var collectionName = collection.Name;

                    _logger.LogDebug("Checking collection info for {CollectionName}", collectionName);
                    var collectionInfo = await client.GetCollectionInfo(collectionName, cancellationToken);

                    if (!collectionInfo.Status.IsSuccess)
                    {
                        var errorDetails = collectionInfo.Status?.Error ?? MetricConstants.UnknownErrorMessage;

                        _logger.LogWarning("Collections health check failed for {CollectionName}: {Error}",
                            collectionName, errorDetails);

                        return (IsHealthy: false, CollectionName: collectionName, Error: errorDetails);
                    }

                    _logger.LogDebug("Collection {CollectionName} is healthy", collectionName);

                    return (IsHealthy: true, CollectionName: collectionName, Error: (string?)null);
                }).ToArray();

                // Wait for all checks to complete
                var results = await Task.WhenAll(checkTasks);

                // Check if any collection failed
                var failedCollection = results.FirstOrDefault(r => !r.IsHealthy);
                if (failedCollection.CollectionName != null)
                {
                    return (false,
                        $"Failed to get info for collection '{failedCollection.CollectionName}': {failedCollection.Error}");
                }

                _logger.LogDebug("Collections health check passed for all {CollectionCount} collections",
                    collections.Length);
            }
            else
            {
                _logger.LogDebug("Collections health check passed (no collections to verify)");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Collections health check failed with exception");

            return (false, $"Exception during collections check: {ex.Message}");
        }
    }

    public async Task<bool> DeleteCollectionViaApiAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Deleting collection {CollectionName} via API on node {NodeUrl}",
                collectionName, nodeUrl);

            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);

            var result = await qdrantClient.DeleteCollection(collectionName, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Collection {CollectionName} deleted successfully via API on node {NodeUrl}",
                    collectionName, nodeUrl);

                return true;
            }

            _logger.LogError("Failed to delete collection {CollectionName} via API: {Error}",
                collectionName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete collection {CollectionName} via API on node {NodeUrl}",
                collectionName, nodeUrl);

            return false;
        }
    }

    public async Task<bool> DeleteCollectionFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Deleting collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            collectionName, podName, podNamespace);

        if (_commandExecutor == null)
        {
            _logger.LogError("Kubernetes client not available, cannot delete collection from disk");

            return false;
        }

        return await _commandExecutor.DeleteAndVerifyAsync(
            podName,
            podNamespace,
            $"{QdrantConstants.StoragePath}/{collectionName}",
            isDirectory: true,
            $"Collection {collectionName}",
            cancellationToken);
    }


    public async Task<bool> RecoverCollectionFromSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Recovering collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}",
                collectionName, snapshotName, nodeUrl);

            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var result = await qdrantClient.RecoverCollectionFromSnapshot(
                collectionName,
                snapshotName,
                cancellationToken,
                isWaitForResult: true);

            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted()
                    ? MetricConstants.RecoveryAcceptedMessage
                    : MetricConstants.RecoverySuccessMessage;

                _logger.LogInformation(
                    "Collection {CollectionName} {StatusText} from snapshot {SnapshotName} on node {NodeUrl}",
                    collectionName, statusText, snapshotName, nodeUrl);

                return true;
            }

            _logger.LogError("Failed to recover collection {CollectionName} from snapshot {SnapshotName}: {Error}",
                collectionName, snapshotName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to recover collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}",
                collectionName, snapshotName, nodeUrl);

            return false;
        }
    }

    public async Task<bool> RecoverCollectionFromUrlAsync(
        string nodeUrl,
        string collectionName,
        string snapshotUrl,
        string? snapshotChecksum,
        bool waitForResult,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from URL {SnapshotUrl} on node {NodeUrl}",
                collectionName, snapshotUrl, nodeUrl);

            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);

            var snapshotLocationUri = new Uri(snapshotUrl);

            var result = await qdrantClient.RecoverCollectionFromSnapshot(
                collectionName,
                snapshotLocationUri,
                cancellationToken,
                isWaitForResult: waitForResult,
                snapshotChecksum: snapshotChecksum);

            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted()
                    ? MetricConstants.RecoveryAcceptedMessage
                    : MetricConstants.RecoverySuccessMessage;

                _logger.LogInformation(
                    "Collection {CollectionName} {StatusText} from URL {SnapshotUrl} on node {NodeUrl}",
                    collectionName, statusText, snapshotUrl, nodeUrl);

                return true;
            }

            _logger.LogError("Failed to recover collection {CollectionName} from URL {SnapshotUrl}: {Error}",
                collectionName, snapshotUrl, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to recover collection {CollectionName} from URL {SnapshotUrl} on node {NodeUrl}",
                collectionName, snapshotUrl, nodeUrl);

            return false;
        }
    }

    public async Task EnrichWithClusteringInfoAsync(
        string healthyNodeUrl,
        IList<CollectionInfo> collectionInfos,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            var healthyNodePeerId = collectionInfos
                .FirstOrDefault(c => c.NodeUrl == healthyNodeUrl)?.PeerId;

            if (string.IsNullOrEmpty(healthyNodePeerId))
            {
                _logger.LogWarning("Could not find peer ID for node {NodeUrl}", healthyNodeUrl);

                return;
            }

            var collectionNames = collectionInfos
                .Where(c => c.NodeUrl == healthyNodeUrl)
                .Select(c => c.CollectionName)
                .Distinct();

            foreach (var collectionName in collectionNames)
            {
                try
                {
                    var clusteringInfo = await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);

                    if (clusteringInfo?.Status?.IsSuccess != true || clusteringInfo.Result == null)
                        continue;

                    var info = collectionInfos.FirstOrDefault(c =>
                        c.CollectionName == collectionName && c.NodeUrl == healthyNodeUrl);

                    if (info == null)
                        continue;

                    UpdateShardMetrics(info, clusteringInfo.Result);
                    UpdateTransferMetrics(info, clusteringInfo.Result, peerToPodMap);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get clustering info for collection {Collection} on node {NodeUrl}",
                        collectionName, healthyNodeUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup QdrantClient for node {NodeUrl}", healthyNodeUrl);
        }
    }
    
    public async Task<List<CollectionInfo>> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting collections from Qdrant API (Kubernetes storage not available)");

        var result = new List<CollectionInfo>();

        foreach (var node in nodes)
        {
            try
            {
                _logger.LogDebug("Getting collections from node {NodeUrl}", node.Url);

                var qdrantClient = _clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);

                // Get list of collections
                var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
                if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
                {
                    _logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}",
                        node.Url, collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage);

                    continue;
                }

                // For each collection, get its info
                foreach (var collection in collectionsResponse.Result.Collections)
                {
                    try
                    {
                        var collectionName = collection.Name;

                        var metrics = new Dictionary<string, object>
                        {
                            { MetricConstants.PrettySizeKey, MetricConstants.NotAvailableValue },
                            { MetricConstants.SizeBytesKey, 0L }
                        };

                        result.Add(new CollectionInfo
                        {
                            CollectionName = collectionName,
                            NodeUrl = node.Url,
                            PodName = node.PodName ?? MetricConstants.UnknownPodName,
                            PeerId = node.PeerId,
                            PodNamespace = node.Namespace ?? string.Empty,
                            Metrics = metrics
                        });

                        _logger.LogDebug("Added collection {CollectionName} from node {NodeUrl}", collectionName,
                            node.Url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get info for collection {CollectionName} from node {NodeUrl}",
                            collection.Name, node.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collections from node {NodeUrl}", node.Url);
            }
        }

        _logger.LogInformation("Retrieved {Count} collections from Qdrant API", result.Count);

        return result;
    }

    private void UpdateShardMetrics(CollectionInfo info,
        Aer.QdrantClient.Http.Models.Responses.GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            clusteringResult)
    {
        if (clusteringResult.LocalShards == null)
            return;

        var shards = new List<ulong>();
        var shardStates = new Dictionary<string, string>();

        foreach (var shard in clusteringResult.LocalShards)
        {
            shards.Add(shard.ShardId);
            shardStates[shard.ShardId.ToString()] = shard.State.ToString();
        }

        if (shards.Count != 0)
        {
            info.Metrics["shards"] = shards;
            info.Metrics["shardStates"] = shardStates;
        }
    }

    private void UpdateTransferMetrics(
        CollectionInfo info,
        Aer.QdrantClient.Http.Models.Responses.GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            clusteringResult,
        Dictionary<string, string> peerToPodMap)
    {
        if (clusteringResult.ShardTransfers == null)
            return;

        var outgoingTransfers = clusteringResult.ShardTransfers
            .Where(t => t.From.ToString() == info.PeerId)
            .Select(t => new
            {
                t.ShardId,
                To = peerToPodMap.TryGetValue(t.To.ToString(), out var podName) ? podName : t.To.ToString(),
                ToPeerId = t.To.ToString(),
                IsSync = t.Sync
            })
            .ToList();

        if (outgoingTransfers.Count != 0)
        {
            info.Metrics["outgoingTransfers"] = outgoingTransfers;
        }
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetEnrichedCollectionsInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting enriched collections info from {NodesCount} nodes", nodes.Count);

        // Get collections from Qdrant API (only from healthy nodes)
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();
        
        _logger.LogInformation("Using {HealthyCount} healthy nodes out of {TotalCount}", 
            healthyNodes.Count, nodes.Count);

        var collections = await GetCollectionsFromQdrantAsync(
            healthyNodes.Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName)),
            cancellationToken);

        if (collections.Count == 0)
        {
            _logger.LogDebug("No collections found from API");
            return collections;
        }

        // Enrich with storage info if nodes have pod names
        if (healthyNodes.Any(n => !string.IsNullOrEmpty(n.PodName)))
        {
            await EnrichCollectionsWithStorageInfoAsync(healthyNodes, collections, cancellationToken);
        }

        // Enrich with clustering info
        await EnrichCollectionsWithClusteringInfoAsync(healthyNodes, collections, peerToPodMap, cancellationToken);

        _logger.LogInformation("Retrieved and enriched {Count} collections", collections.Count);

        return collections;
    }

    private async Task EnrichCollectionsWithStorageInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Enriching collections with storage information from Kubernetes");

        var storageCollections = new Dictionary<(string NodeUrl, string CollectionName), CollectionSize>();

        foreach (var node in nodes)
        {
            try
            {
                if (string.IsNullOrEmpty(node.PodName))
                {
                    _logger.LogDebug("Skipping node {NodeUrl} - no pod name available", node.Url);
                    continue;
                }

                _logger.LogInformation("Fetching storage info from pod {PodName} for node {NodeUrl}", 
                    node.PodName, node.Url);

                var collectionSizes = (await GetCollectionsSizesForPodAsync(
                    node.PodName,
                    node.Namespace ?? string.Empty,
                    node.Url,
                    node.PeerId,
                    cancellationToken)).ToList();

                foreach (var size in collectionSizes)
                {
                    storageCollections[(size.NodeUrl, size.CollectionName)] = size;
                }

                _logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}",
                    collectionSizes.Count, node.PodName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collection sizes for node {NodeUrl}", node.Url);
            }
        }

        _logger.LogInformation("Found {Count} collections in storage across all nodes", storageCollections.Count);

        // Enrich collections with storage data
        foreach (var collection in collections)
        {
            var key = (collection.NodeUrl, collection.CollectionName);

            if (storageCollections.TryGetValue(key, out var storageInfo))
            {
                collection.Metrics[MetricConstants.PrettySizeKey] = storageInfo.PrettySize;
                collection.Metrics[MetricConstants.SizeBytesKey] = storageInfo.SizeBytes;

                _logger.LogDebug("Enriched collection {CollectionName} on {NodeUrl} with storage data: {Size}",
                    collection.CollectionName, collection.NodeUrl, storageInfo.PrettySize);
            }
            else
            {
                collection.Issues.Add("Collection exists in API but not found in storage");

                _logger.LogWarning("Collection {CollectionName} on node {NodeUrl} exists in API but not in storage!",
                    collection.CollectionName, collection.NodeUrl);
            }
        }
    }

    private async Task EnrichCollectionsWithClusteringInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Enriching collections with clustering information");

        // Find a healthy node to query clustering info
        var healthyNode = nodes.FirstOrDefault(n => n.IsHealthy);
        
        if (healthyNode == null)
        {
            _logger.LogWarning("No healthy nodes available for clustering info");
            return;
        }

        await EnrichWithClusteringInfoAsync(healthyNode.Url, collections, peerToPodMap, cancellationToken);
    }
}

