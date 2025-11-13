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

    private async Task<string?> GetSnapshotChecksumAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        if (_commandExecutor == null)
        {
            _logger.LogWarning("Kubernetes client not available, cannot get checksum");
            return null;
        }

        try
        {
            var snapshotPath = $"/qdrant/snapshots/{collectionName}/{snapshotName}";
            var checksumPath = $"{snapshotPath}.checksum";
            
            _logger.LogInformation("Reading checksum from {ChecksumPath} on pod {PodName}", checksumPath, podName);
            
            var checksumContent = await _commandExecutor.GetFileContentAsync(
                podName,
                podNamespace,
                checksumPath,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(checksumContent))
            {
                _logger.LogWarning("No checksum file found for snapshot {SnapshotName}", snapshotName);
                return null;
            }

            // Checksum file format: "<checksum> <filename>" or just "<checksum>"
            var parts = checksumContent.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var checksum = parts[0].Trim().ToLowerInvariant();
                _logger.LogInformation("Found checksum for {SnapshotName}: {Checksum}", snapshotName, checksum);
                return checksum;
            }

            _logger.LogWarning("Could not parse checksum from content: {Content}", checksumContent);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get checksum for snapshot {SnapshotName}", snapshotName);
            return null;
        }
    }

    public async Task<Stream?> DownloadSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Downloading snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}. Received namespace: '{NamespaceRaw}'",
            snapshotName, collectionName, podName, podNamespace, podNamespace ?? "NULL");

        if (_commandExecutor == null)
        {
            _logger.LogError("Kubernetes client not available, cannot download snapshot from disk");
            return null;
        }

        // Ensure namespace is not empty or null
        var effectiveNamespace = string.IsNullOrWhiteSpace(podNamespace) ? "qdrant" : podNamespace;
        
        if (effectiveNamespace != podNamespace)
        {
            _logger.LogWarning("Namespace was empty or null, using default 'qdrant'. Original value: '{Original}'", 
                podNamespace ?? "NULL");
        }

        var snapshotPath = $"/qdrant/snapshots/{collectionName}/{snapshotName}";
        _logger.LogInformation("Starting download: {Path} from pod {PodName}", snapshotPath, podName);
        
        // Get expected file size using stat command
        var expectedSize = await _commandExecutor.GetFileSizeInBytesAsync(
            podName,
            effectiveNamespace,
            snapshotPath,
            cancellationToken);

        if (expectedSize.HasValue)
        {
            _logger.LogInformation("📏 Got expected file size: {Size} bytes ({PrettySize})", 
                expectedSize.Value, expectedSize.Value.ToPrettySize());
        }
        else
        {
            _logger.LogWarning("⚠️ Could not get file size from pod - will download without size limit!");
        }
        
        // Get expected checksum
        var expectedChecksum = await GetSnapshotChecksumAsync(
            podName,
            effectiveNamespace,
            collectionName,
            snapshotName,
            cancellationToken);
        
        // Use kubectl cp instead of WebSocket exec for reliable large file downloads
        // kubectl cp uses tar internally and is designed for file transfers
        _logger.LogInformation("Using kubectl cp for reliable download of snapshot {SnapshotName} from pod {PodName}", 
            snapshotName, podName);
        
        var fileStream = await _commandExecutor.DownloadFileViaKubectlCpAsync(
            podName, 
            effectiveNamespace, 
            snapshotPath,
            cancellationToken);

        if (fileStream == null)
        {
            _logger.LogError("Failed to download snapshot {SnapshotName} from disk on pod {PodName} in namespace {Namespace} using kubectl cp", 
                snapshotName, podName, effectiveNamespace);
            return null;
        }

        _logger.LogInformation("✅ Snapshot {SnapshotName} downloaded successfully from disk on pod {PodName} in namespace {Namespace} using kubectl cp", 
            snapshotName, podName, effectiveNamespace);

        if (expectedSize.HasValue)
        {
            _logger.LogInformation("📏 Expected file size: {Size} bytes ({PrettySize})", 
                expectedSize.Value, expectedSize.Value.ToPrettySize());
        }

        if (!string.IsNullOrEmpty(expectedChecksum))
        {
            _logger.LogInformation("📋 Expected checksum: {Checksum} - verify after download", expectedChecksum);
        }

        // Return the file stream - kubectl cp downloaded to temp file with DeleteOnClose option
        return fileStream;
    }

    public async Task<bool> CheckCollectionExistsAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Checking if collection {CollectionName} exists on node {NodeUrl}", 
                collectionName, nodeUrl);
            
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            
            var result = await qdrantClient.GetCollectionInfo(collectionName, cancellationToken);
            
            var exists = result?.Status?.IsSuccess == true;
            _logger.LogInformation("Collection {CollectionName} exists: {Exists}", collectionName, exists);
            
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if collection {CollectionName} exists on node {NodeUrl}, assuming it doesn't exist", 
                collectionName, nodeUrl);
            return false;
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
                isWaitForResult: true);
            
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
            _logger.LogWarning("Kubernetes client not available, cannot get snapshots from disk for pod {PodName}", podName);
            return [];
        }

        var snapshots = new List<SnapshotInfo>();

        try
        {
            _logger.LogInformation("Listing collection folders in /qdrant/snapshots on pod {PodName}", podName);
            
            var collectionFolders = await _commandExecutor.ListFilesAsync(
                podName,
                podNamespace,
                "/qdrant/snapshots",
                "*/",
                cancellationToken);

            _logger.LogInformation("Found {Count} collection folders in snapshots directory on pod {PodName}: {Folders}", 
                collectionFolders.Count, podName, string.Join(", ", collectionFolders));

            foreach (var collectionName in collectionFolders)
            {
                try
                {
                    _logger.LogInformation("Listing snapshot files in /qdrant/snapshots/{CollectionName} on pod {PodName}", 
                        collectionName, podName);
                    
                    var snapshotFiles = await _commandExecutor.ListFilesAsync(
                        podName,
                        podNamespace,
                        $"/qdrant/snapshots/{collectionName}",
                        "*.snapshot",
                        cancellationToken);

                    _logger.LogInformation("Found {Count} snapshot files for collection {CollectionName} on pod {PodName}: {Files}", 
                        snapshotFiles.Count, collectionName, podName, string.Join(", ", snapshotFiles));

                    foreach (var snapshotFile in snapshotFiles.Where(f => f.EndsWith(".snapshot")))
                    {
                        _logger.LogDebug("Getting size for snapshot {SnapshotFile} in {CollectionName}", 
                            snapshotFile, collectionName);
                        
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
                            _logger.LogInformation("Added snapshot {SnapshotName} for collection {CollectionName}: {Size} bytes ({PrettySize})", 
                                snapshotFile, collectionName, sizeBytes.Value, snapshotInfo.PrettySize);
                        }
                        else
                        {
                            _logger.LogWarning("Could not get size for snapshot {SnapshotFile} in collection {CollectionName}", 
                                snapshotFile, collectionName);
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

