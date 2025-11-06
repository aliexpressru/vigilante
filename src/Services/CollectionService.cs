using k8s;
using Aer.QdrantClient.Http.Abstractions;
using Vigilante.Models;
using Vigilante.Configuration;
using Microsoft.Extensions.Options;
using Vigilante.Services.Interfaces;
using Vigilante.Extensions;

namespace Vigilante.Services;

public class CollectionService : ICollectionService
{
    private readonly ILogger<CollectionService> _logger;
    private readonly IMeterService _meterService;
    private readonly PodCommandExecutor? _commandExecutor;
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
            var uri = new Uri(healthyNodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            var result = await qdrantClient.ReplicateShards(
                sourcePeerId: sourcePeerId,
                targetPeerId: targetPeerId,
                collectionNamesToReplicate: new[] { collectionName },
                shardIdsToReplicate: shardIds,
                isMoveShards: isMove,
                cancellationToken: cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("✅ Shard replication initiated: {Collection} [{SourcePeer}→{TargetPeer}]", 
                    collectionName, sourcePeerId, targetPeerId);
                return true;
            }

            _logger.LogError("Failed to replicate shards for {Collection}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
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
                "/qdrant/storage/collections",
                cancellationToken);

            _logger.LogDebug("Found {Count} collections on pod {PodName}", collections.Count, podName);

            foreach (var collection in collections)
            {
                var sizeBytes = await _commandExecutor.GetSizeAsync(
                    podName,
                    podNamespace,
                    "/qdrant/storage/collections",
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

    public async Task<(bool IsHealthy, string? ErrorMessage)> CheckCollectionsHealthAsync(IQdrantHttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Checking collections health");
            var collectionsResponse = await client.ListCollections(cancellationToken);
            
            if (!collectionsResponse.Status.IsSuccess)
            {
                var errorDetails = collectionsResponse.Status?.Error ?? "Unknown error";
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
                        var errorDetails = collectionInfo.Status?.Error ?? "Unknown error";
                        _logger.LogWarning("Collections health check failed for {CollectionName}: {Error}", collectionName, errorDetails);
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
                    return (false, $"Failed to get info for collection '{failedCollection.CollectionName}': {failedCollection.Error}");
                }
                
                _logger.LogDebug("Collections health check passed for all {CollectionCount} collections", collections.Length);
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

            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            var result = await qdrantClient.DeleteCollection(collectionName, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("✅ Collection {CollectionName} deleted successfully via API on node {NodeUrl}", 
                    collectionName, nodeUrl);
                return true;
            }

            _logger.LogError("Failed to delete collection {CollectionName} via API: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
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

        var fullPath = $"/qdrant/storage/collections/{collectionName}";
        return await _commandExecutor.DeleteAndVerifyAsync(
            podName, 
            podNamespace, 
            fullPath, 
            isDirectory: true, 
            $"Collection {collectionName}", 
            cancellationToken);
    }
    public async Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Creating snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.CreateCollectionSnapshot(
                collectionName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var snapshotName = result.Result?.Name ?? $"{collectionName}-snapshot-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var statusText = result.IsAccepted() ? "accepted" : "created successfully";
                _logger.LogInformation("✅ Snapshot {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    statusText, collectionName, nodeUrl);
                return snapshotName;
            }
            
            _logger.LogError("Failed to create snapshot for collection {CollectionName}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return null;
        }
    }
    public async Task<List<string>> ListCollectionSnapshotsAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Listing snapshots for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.ListCollectionSnapshots(collectionName, cancellationToken);
            if (result?.Status?.IsSuccess == true && result.Result != null)
            {
                var snapshots = result.Result.Select(s => s.Name).ToList();
                _logger.LogDebug("Found {Count} snapshots for collection {CollectionName} on node {NodeUrl}", 
                    snapshots.Count, collectionName, nodeUrl);
                return snapshots;
            }
            _logger.LogWarning("Failed to list snapshots for collection {CollectionName}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list snapshots for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return new List<string>();
        }
    }
    public async Task<bool> DeleteCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.DeleteCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "deletion accepted" : "deleted successfully";
                _logger.LogInformation("✅ Snapshot {SnapshotName} {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    snapshotName, statusText, collectionName, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to delete snapshot {SnapshotName} for collection {CollectionName}: {Error}",
                snapshotName, collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            return false;
        }
    }
    public async Task<Stream?> DownloadCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            
            var result = await qdrantClient.DownloadCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken);
            
            if (result?.Result?.SnapshotDataStream != null)
            {
                _logger.LogInformation("✅ Snapshot {SnapshotName} downloaded successfully for collection {CollectionName} from node {NodeUrl}", 
                    snapshotName, collectionName, nodeUrl);
                return result.Result.SnapshotDataStream;
            }
            
            _logger.LogError("Failed to download snapshot {SnapshotName} for collection {CollectionName}: empty or null result",
                snapshotName, collectionName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            return null;
        }
    }
    public async Task<bool> RecoverCollectionFromSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}", 
                collectionName, snapshotName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.RecoverCollectionFromSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "recovery accepted" : "recovered successfully";
                _logger.LogInformation("✅ Collection {CollectionName} {StatusText} from snapshot {SnapshotName} on node {NodeUrl}", 
                    collectionName, statusText, snapshotName, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to recover collection {CollectionName} from snapshot {SnapshotName}: {Error}",
                collectionName, snapshotName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}", 
                collectionName, snapshotName, nodeUrl);
            return false;
        }
    }
    public async Task<bool> RecoverCollectionFromUploadedSnapshotAsync(
        string nodeUrl,
        string collectionName,
        Stream snapshotData,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from uploaded snapshot on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            
            var result = await qdrantClient.RecoverCollectionFromUploadedSnapshot(
                collectionName, 
                snapshotData, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "recovery accepted" : "recovered successfully";
                _logger.LogInformation("✅ Collection {CollectionName} {StatusText} from uploaded snapshot on node {NodeUrl}", 
                    collectionName, statusText, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to recover collection {CollectionName} from uploaded snapshot: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover collection {CollectionName} from uploaded snapshot on node {NodeUrl}",
                collectionName, nodeUrl);
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
            var uri = new Uri(healthyNodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            // Find the peer ID of the node we're querying
            var healthyNodePeerId = collectionInfos
                .FirstOrDefault(c => c.NodeUrl == healthyNodeUrl)?.PeerId;
            
            if (string.IsNullOrEmpty(healthyNodePeerId))
            {
                _logger.LogWarning("Could not find peer ID for node {NodeUrl}", healthyNodeUrl);
                return;
            }

            // Get unique collection names for this specific node
            var collectionNames = collectionInfos
                .Where(c => c.NodeUrl == healthyNodeUrl)
                .Select(c => c.CollectionName)
                .Distinct();

            foreach (var collectionName in collectionNames)
            {
                try
                {
                    var clusteringInfo = await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);
                    if (clusteringInfo?.Status?.IsSuccess == true && clusteringInfo.Result != null)
                    {
                        // Update metrics only for this node's collection
                        var info = collectionInfos.FirstOrDefault(c => 
                            c.CollectionName == collectionName && c.NodeUrl == healthyNodeUrl);
                        
                        if (info == null)
                            continue;

                        var shards = new List<ulong>();
                        var shardStates = new Dictionary<string, string>();

                        // Use local shards for this node
                        if (clusteringInfo.Result.LocalShards != null)
                        {
                            foreach (var shard in clusteringInfo.Result.LocalShards)
                            {
                                shards.Add(shard.ShardId);
                                shardStates[shard.ShardId.ToString()] = shard.State.ToString();
                            }
                        }

                        if (shards.Count != 0)
                        {
                            info.Metrics["shards"] = shards;
                            info.Metrics["shardStates"] = shardStates;
                        }

                        // Add outgoing transfers information with pod names instead of PeerIds
                        if (clusteringInfo.Result.ShardTransfers != null)
                        {
                            var outgoingTransfers = clusteringInfo.Result.ShardTransfers
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
                    }
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

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsFromQdrantAsync(
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
                
                var uri = new Uri(node.Url);
                var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
                
                // Get list of collections
                var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
                if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
                {
                    _logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}", 
                        node.Url, collectionsResponse.Status?.Error ?? "Unknown error");
                    continue;
                }
                
                // For each collection, get its info
                foreach (var collection in collectionsResponse.Result.Collections)
                {
                    try
                    {
                        var collectionName = collection.Name;
                        
                        // Get snapshots for this collection
                        var snapshots = await ListCollectionSnapshotsAsync(node.Url, collectionName, cancellationToken);
                        
                        var metrics = new Dictionary<string, object>
                        {
                            { "prettySize", "N/A" },
                            { "sizeBytes", 0L },
                            { "snapshots", snapshots }
                        };

                        result.Add(new CollectionInfo
                        {
                            CollectionName = collectionName,
                            NodeUrl = node.Url,
                            PodName = node.PodName ?? "unknown",
                            PeerId = node.PeerId,
                            PodNamespace = node.Namespace ?? "",
                            Metrics = metrics
                        });
                        
                        _logger.LogDebug("Added collection {CollectionName} from node {NodeUrl}", collectionName, node.Url);
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

    public async Task<IEnumerable<SnapshotInfo>> GetSnapshotsFromDiskForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting to get snapshots from disk for pod {PodName} (Node URL {NodeUrl}) in namespace {Namespace}",
            podName, nodeUrl, podNamespace);

        if (_commandExecutor == null)
        {
            return [];
        }

        var snapshots = new List<SnapshotInfo>();

        try
        {
            var collectionFolders = await _commandExecutor.ListFilesAsync(
                podName,
                podNamespace,
                "/qdrant/snapshots",
                "*/",
                cancellationToken);

            _logger.LogDebug("Found {Count} collection folders in snapshots directory on pod {PodName}", 
                collectionFolders.Count, podName);

            foreach (var collectionName in collectionFolders)
            {
                try
                {
                    var snapshotFiles = await _commandExecutor.ListFilesAsync(
                        podName,
                        podNamespace,
                        $"/qdrant/snapshots/{collectionName}",
                        "*.snapshot",
                        cancellationToken);

                    _logger.LogDebug("Found {Count} snapshot files for collection {CollectionName} on pod {PodName}", 
                        snapshotFiles.Count, collectionName, podName);

                    foreach (var snapshotFile in snapshotFiles.Where(f => f.EndsWith(".snapshot")))
                    {
                        var sizeBytes = await _commandExecutor.GetSizeAsync(
                            podName,
                            podNamespace,
                            $"/qdrant/snapshots/{collectionName}",
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
                                SizeBytes = sizeBytes.Value
                            };

                            snapshots.Add(snapshotInfo);
                            _logger.LogDebug("Added snapshot {SnapshotName} for collection {CollectionName}: {Size}", 
                                snapshotFile, collectionName, snapshotInfo.PrettySize);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get snapshots for collection {Collection} on pod {PodName}",
                        collectionName, podName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get snapshots from disk for pod {PodName}", podName);
        }

        _logger.LogInformation("Found {Count} snapshots on pod {PodName}", snapshots.Count, podName);
        return snapshots;
    }

    public async Task<bool> DeleteSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            snapshotName, collectionName, podName, podNamespace);

        if (_commandExecutor == null)
        {
            _logger.LogError("Kubernetes client not available, cannot delete snapshot from disk");
            return false;
        }

        var fullPath = $"/qdrant/snapshots/{collectionName}/{snapshotName}";
        return await _commandExecutor.DeleteAndVerifyAsync(
            podName, 
            podNamespace, 
            fullPath, 
            isDirectory: false, 
            $"Snapshot {snapshotName}", 
            cancellationToken);
    }
}
