using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Requests;
using Aer.QdrantClient.Http.Models.Shared;
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
    private List<CollectionInfo>? _cachedCollections;
    private readonly Lock _cacheLock = new();

    public CollectionService(
        ILogger<CollectionService> logger,
        IMeterService meterService,
        IQdrantClientFactory clientFactory,
        IOptions<QdrantOptions> options,
        ILogger<PodCommandExecutor> commandExecutorLogger,
        IKubernetes? kubernetes = null)
    {
        _logger = logger;
        _meterService = meterService;
        _clientFactory = clientFactory;
        _options = options.Value;

        // Initialize command executor if Kubernetes client is available
        if (kubernetes != null)
        {
            _commandExecutor = new PodCommandExecutor(kubernetes, commandExecutorLogger);
        }
        else
        {
            _logger.LogWarning("Kubernetes client not available, collection size monitoring will be disabled");
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
        ShardTransferMethod? shardTransferMethod,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            // Use provided transfer method or default to Snapshot
            var transferMethod =
                shardTransferMethod ?? ShardTransferMethod.Snapshot;

            _logger.LogInformation(
                "Initiating shard replication via Qdrant client: Collection={Collection}, Source={SourcePeerId}, Target={TargetPeerId}, " +
                "Shards=[{ShardIds}], Move={IsMove}, TransferMethod={TransferMethod}",
                collectionName, sourcePeerId, targetPeerId, string.Join(", ", shardIds), isMove, transferMethod);

            var result = await qdrantClient.ReplicateShards(
                sourcePeerId: sourcePeerId,
                targetPeerId: targetPeerId,
                collectionNamesToReplicate: new[] { collectionName },
                shardIdsToReplicate: shardIds,
                isMoveShards: isMove,
                shardTransferMethod: transferMethod,
                cancellationToken: cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation(
                    "Shard replication initiated: {Collection} [{SourcePeer}→{TargetPeer}] using {TransferMethod}",
                    collectionName, sourcePeerId, targetPeerId, transferMethod);

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

    public async Task<bool> AbortShardTransferAsync(
        string healthyNodeUrl,
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint shardId,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            _logger.LogInformation(
                "Aborting shard transfer via Qdrant client: Collection={Collection}, Source={SourcePeerId}, Target={TargetPeerId}, " +
                "ShardId={ShardId}",
                collectionName, sourcePeerId, targetPeerId, shardId);

            var request = UpdateCollectionClusteringSetupRequest.CreateAbortShardTransferRequest(
                shardId,
                sourcePeerId,
                targetPeerId);

            var result = await qdrantClient.UpdateCollectionClusteringSetup(
                collectionName,
                request,
                cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation(
                    "Shard transfer aborted: {Collection} [Shard {ShardId}: {SourcePeer}→{TargetPeer}]",
                    collectionName, shardId, sourcePeerId, targetPeerId);

                return true;
            }

            var errorMessage = result?.Status?.Error ?? MetricConstants.UnknownErrorMessage;
            _logger.LogError("Failed to abort shard transfer for {Collection}: {Error}",
                collectionName, errorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to abort shard transfer for collection {Collection}", collectionName);

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
        // This method is kept for backward compatibility with old code that passes IQdrantHttpClient
        // It performs a simple health check without caching
        
        try
        {
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

                // Create tasks for all collection health checks
                var checkTasks = collections.Select(async collection =>
                {
                    var collectionName = collection.Name;

                    var collectionInfo = await client.GetCollectionInfo(collectionName, cancellationToken);

                    if (!collectionInfo.Status.IsSuccess)
                    {
                        var errorDetails = collectionInfo.Status?.Error ?? MetricConstants.UnknownErrorMessage;

                        _logger.LogWarning("Collections health check failed for {CollectionName}: {Error}",
                            collectionName, errorDetails);

                        return (IsHealthy: false, CollectionName: collectionName, Error: errorDetails);
                    }

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
                isWaitForResult: true,
                snapshotPriority: SnapshotPriority.Snapshot);

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
        SnapshotPriority snapshotPriority,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from URL {SnapshotUrl} on node {NodeUrl} with priority {SnapshotPriority}",
                collectionName, snapshotUrl, nodeUrl, snapshotPriority);

            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);

            var snapshotLocationUri = new Uri(snapshotUrl);

            var result = await qdrantClient.RecoverCollectionFromSnapshot(
                collectionName,
                snapshotLocationUri,
                cancellationToken,
                isWaitForResult: waitForResult,
                snapshotPriority: snapshotPriority,
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

    public async Task<(List<CollectionInfo> Collections, bool IsHealthy, string? ErrorMessage)> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken,
        bool clearCache = false)
    {
        var nodesList = nodes.ToList();

        if (nodesList.Count == 0)
        {
            _logger.LogWarning("No nodes provided to GetCollectionsFromQdrantAsync");
            return (new List<CollectionInfo>(), true, null);
        }

        // Check cache first if not clearing
        if (!clearCache)
        {
            lock (_cacheLock)
            {
                if (_cachedCollections != null && _cachedCollections.Count > 0)
                {
                    // Check if cache contains data from all requested nodes
                    var cachedNodeUrls = _cachedCollections.Select(c => c.NodeUrl).Distinct().ToHashSet();
                    var requestedNodeUrls = nodesList.Select(n => n.Url).ToHashSet();
                    
                    // If cache contains all requested nodes, return cached data
                    if (requestedNodeUrls.All(url => cachedNodeUrls.Contains(url)))
                    {
                        _logger.LogInformation("Returning cached {UniqueCollectionCount} collections from {NodeCount} nodes", 
                            GetUniqueCollectionCount(_cachedCollections), requestedNodeUrls.Count);
                        return (_cachedCollections, true, null);
                    }
                    else
                    {
                        _logger.LogInformation("Cache doesn't contain all requested nodes. Requested: {Requested}, Cached: {Cached}. Fetching fresh data.",
                            string.Join(", ", requestedNodeUrls), string.Join(", ", cachedNodeUrls));
                    }
                }
            }
        }
        else
        {
            _logger.LogInformation("Clearing collections cache");
            lock (_cacheLock)
            {
                _cachedCollections = null;
            }
        }


        var result = new List<CollectionInfo>();
        var overallHealthy = true;
        string? overallErrorMessage = null;

        foreach (var node in nodesList)
        {
            try
            {
                var qdrantClient = _clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);

                // Get list of collections
                var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
                if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
                {
                    var errorDetails = collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage;
                    _logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}",
                        node.Url, errorDetails);

                    overallHealthy = false;
                    overallErrorMessage = $"Failed to list collections from node {node.Url}: {errorDetails}";
                    continue;
                }

                if (collectionsResponse.Result.Collections.Length == 0)
                {
                    continue;
                }

                // Get all aliases for this node
                Dictionary<string, List<string>> collectionAliases = new();
                try
                {
                    var aliasesResponse = await qdrantClient.ListAllAliases(cancellationToken);
                    if (aliasesResponse?.Status?.IsSuccess == true && aliasesResponse.Result?.Aliases != null)
                    {
                        // Group aliases by collection name
                        collectionAliases = aliasesResponse.Result.Aliases
                            .GroupBy(a => a.CollectionName)
                            .ToDictionary(
                                g => g.Key,
                                g => g.Select(a => a.AliasName).ToList()
                            );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get aliases from node {NodeUrl}", node.Url);
                }

                // For each collection, get its detailed info including status
                foreach (var collection in collectionsResponse.Result.Collections)
                {
                    try
                    {
                        var collectionName = collection.Name;

                        // Get detailed collection info to retrieve status
                        var collectionInfoResponse = await qdrantClient.GetCollectionInfo(collectionName, cancellationToken);
                        
                        if (!collectionInfoResponse.Status.IsSuccess)
                        {
                            var errorDetails = collectionInfoResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage;
                            _logger.LogWarning("Failed to get info for collection {CollectionName} from node {NodeUrl}: {Error}",
                                collectionName, node.Url, errorDetails);
                            
                            overallHealthy = false;
                            overallErrorMessage = $"Failed to get info for collection '{collectionName}': {errorDetails}";
                            continue;
                        }

                        var metrics = new Dictionary<string, object>
                        {
                            { MetricConstants.PrettySizeKey, MetricConstants.NotAvailableValue },
                            { MetricConstants.SizeBytesKey, 0L }
                        };

                        // Get aliases for this collection
                        var aliases = collectionAliases.TryGetValue(collectionName, out var aliasList)
                            ? aliasList
                            : new List<string>();

                        result.Add(new CollectionInfo
                        {
                            CollectionName = collectionName,
                            NodeUrl = node.Url,
                            PodName = node.PodName ?? MetricConstants.UnknownPodName,
                            PeerId = node.PeerId,
                            PodNamespace = node.Namespace ?? string.Empty,
                            Metrics = metrics,
                            Aliases = aliases,
                            Status = collectionInfoResponse.Result?.Status,
                            HnswM = collectionInfoResponse.Result?.Config?.HnswConfig?.M
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get info for collection {CollectionName} from node {NodeUrl}",
                            collection.Name, node.Url);
                        
                        overallHealthy = false;
                        overallErrorMessage = $"Exception while getting collection '{collection.Name}' info: {ex.Message}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collections from node {NodeUrl}", node.Url);
                overallHealthy = false;
                overallErrorMessage = $"Exception during collections check from node {node.Url}: {ex.Message}";
            }
        }

        // Cache the result if successful
        if (overallHealthy && result.Count > 0)
        {
            lock (_cacheLock)
            {
                _cachedCollections = result;
                _logger.LogInformation("Cached {UniqueCollectionCount} collections from {NodeCount} nodes", 
                    GetUniqueCollectionCount(result), nodesList.Count);
            }
        }

        return (result, overallHealthy, overallErrorMessage);
    }


    public async Task<IReadOnlyList<CollectionInfo>> GetEnrichedCollectionsInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken,
        bool clearCache = false)
    {
        // Get collections from Qdrant API (only from healthy nodes)
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();

        if (healthyNodes.Count == 0)
        {
            _logger.LogWarning("No healthy nodes available to get collections from");

            return new List<CollectionInfo>();
        }

        var (collections, _, _) = await GetCollectionsFromQdrantAsync(
            healthyNodes.Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName)),
            cancellationToken,
            clearCache);

        if (collections.Count == 0)
        {
            _logger.LogWarning("No collections found from Qdrant API");

            return collections;
        }

        // Enrich with clustering info first (creates ShardDetails with ID and State)
        await EnrichCollectionsWithClusteringInfoAsync(healthyNodes, collections, peerToPodMap, cancellationToken);

        // Then enrich with storage info (adds SizeBytes to existing ShardDetails)
        if (healthyNodes.Any(n => !string.IsNullOrEmpty(n.PodName)))
        {
            await EnrichCollectionsWithStorageInfoAsync(healthyNodes, collections, cancellationToken);
        }

        // Sort collection shards by node within each collection:
        // Group by collection name, sort nodes within each group, then flatten back
        collections = collections
            .GroupBy(c => c.CollectionName)
            .SelectMany(group => group.OrderBy(c => NodeSortingExtensions.GetNodeSortKey(c.PodName, c.PeerId)))
            .ToList();

        // Log summary with unique collection names
        var uniqueCollectionCount = GetUniqueCollectionCount(collections);
        var uniqueCollectionNames = collections.Select(c => c.CollectionName).Distinct().OrderBy(n => n).ToList();
        _logger.LogInformation(
            "Retrieved {UniqueCount} collections from {NodeCount} nodes: {CollectionNames}",
            uniqueCollectionCount,
            healthyNodes.Count,
            string.Join(", ", uniqueCollectionNames));

        return collections;
    }

    public async Task<bool> DropShardsFromPeerAsync(
        string healthyNodeUrl,
        string collectionName,
        ulong peerId,
        uint[] shardIds,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            var result = await qdrantClient.DropCollectionShardsFromPeer(
                collectionName: collectionName,
                peerId: peerId,
                shardIds: shardIds,
                cancellationToken: cancellationToken,
                logger: _logger,
                isDryRun: isDryRun);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Shards drop {Mode} for {Collection} from peer {PeerId}: {ShardIds}",
                    isDryRun ? "simulated" : "completed",
                    collectionName,
                    peerId,
                    string.Join(", ", shardIds));

                return true;
            }

            _logger.LogError("Failed to drop shards for {Collection} from peer {PeerId}: {Error}",
                collectionName, peerId, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to drop shards for collection {Collection} from peer {PeerId}",
                collectionName, peerId);

            return false;
        }
    }

    public async Task<bool> StartReshardingAsync(
        string healthyNodeUrl,
        string collectionName,
        ReshardingOperationDirection direction,
        ulong? peerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodeUrl, _options.ApiKey);

            var request = UpdateCollectionClusteringSetupRequest.CreateStartReshardingRequest(
                direction,
                peerId);

            var result = await qdrantClient.UpdateCollectionClusteringSetup(
                collectionName,
                request,
                cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                var peerInfo = peerId.HasValue ? $" on peer {peerId.Value}" : " on all peers";
                _logger.LogInformation("Resharding operation started for {Collection} (direction: {Direction}){PeerInfo}",
                    collectionName,
                    direction.ToString(),
                    peerInfo);

                return true;
            }

            _logger.LogError("Failed to start resharding for {Collection} (direction: {Direction}): {Error}",
                collectionName, direction.ToString(), result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start resharding for collection {Collection} (direction: {Direction})",
                collectionName, direction.ToString());

            return false;
        }
    }
    
    /// <summary>
    /// Gets the number of unique collections from a list of CollectionInfo instances
    /// </summary>
    private static int GetUniqueCollectionCount(IEnumerable<CollectionInfo> collections)
    {
        return collections.Select(c => c.CollectionName).Distinct().Count();
    }

    private async Task EnrichWithClusteringInfoAsync(
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
                    var clusteringInfo =
                        await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);

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

    private void UpdateShardMetrics(CollectionInfo info,
        Aer.QdrantClient.Http.Models.Responses.GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            clusteringResult)
    {
        if (clusteringResult.LocalShards == null)
            return;

        var shardDetails = new List<ShardDetails>();
        var shardStates = new Dictionary<string, string>();

        foreach (var shard in clusteringResult.LocalShards)
        {
            shardDetails.Add(new ShardDetails
            {
                ShardId = (uint)shard.ShardId,
                State = shard.State.ToString(),
                SizeBytes = null // Will be populated later from storage info
            });
            shardStates[shard.ShardId.ToString()] = shard.State.ToString();
        }

        if (shardDetails.Count != 0)
        {
            info.Metrics[MetricConstants.ShardsKey] = shardDetails;
            info.Metrics[MetricConstants.ShardStatesKey] = shardStates;
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
                IsSync = t.Sync,
                Method = t.Method.ToString()
            })
            .ToList();

        if (outgoingTransfers.Count != 0)
        {
            info.Metrics[MetricConstants.OutgoingTransfersKey] = outgoingTransfers;
        }
    }


    private async Task EnrichCollectionsWithStorageInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        CancellationToken cancellationToken)
    {
        var storageCollections = new Dictionary<(string NodeUrl, string CollectionName), CollectionSize>();
        var shardSizes = new Dictionary<(string NodeUrl, string CollectionName), List<ShardSize>>();

        foreach (var node in nodes)
        {
            try
            {
                if (string.IsNullOrEmpty(node.PodName))
                {
                    continue;
                }

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

                // Get shard sizes for all collections on this node
                var allShardSizes = (await GetAllShardsSizesForPodAsync(
                    node.PodName,
                    node.Namespace ?? string.Empty,
                    node.Url,
                    node.PeerId,
                    cancellationToken)).ToList();

                // Group shards by collection
                foreach (var shardGroup in allShardSizes.GroupBy(s => (s.NodeUrl, s.CollectionName)))
                {
                    shardSizes[shardGroup.Key] = shardGroup.OrderBy(s => s.ShardId).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collection sizes for node {NodeUrl}", node.Url);
            }
        }

        // Enrich collections with storage data
        foreach (var collection in collections)
        {
            var key = (collection.NodeUrl, collection.CollectionName);

            if (storageCollections.TryGetValue(key, out var storageInfo))
            {
                collection.Metrics[MetricConstants.PrettySizeKey] = storageInfo.PrettySize;
                collection.Metrics[MetricConstants.SizeBytesKey] = storageInfo.SizeBytes;
            }
            else
            {
                collection.Issues.Add("Collection exists in API but not found in storage");

                _logger.LogWarning("Collection {CollectionName} on node {NodeUrl} exists in API but not in storage!",
                    collection.CollectionName, collection.NodeUrl);
            }

            // Enrich existing shard details with size information from storage
            if (shardSizes.TryGetValue(key, out var shardSizesList))
            {
                // Get existing shard details from Metrics if they exist (from clustering info)
                if (collection.Metrics.TryGetValue(MetricConstants.ShardsKey, out var shardsObj) 
                    && shardsObj is List<ShardDetails> existingShardDetails)
                {
                    // Create a dictionary for quick lookup of shard sizes
                    var sizeByShardId = shardSizesList.ToDictionary(s => s.ShardId, s => s.SizeBytes);

                    // Update existing shard details with size information
                    foreach (var shardDetail in existingShardDetails)
                    {
                        if (sizeByShardId.TryGetValue(shardDetail.ShardId, out var sizeBytes))
                        {
                            shardDetail.SizeBytes = sizeBytes;
                        }
                    }

                    _logger.LogDebug(
                        "Updated {ShardCount} shard(s) with size info for collection {CollectionName} on node {NodeUrl}",
                        existingShardDetails.Count, collection.CollectionName, collection.NodeUrl);
                }
                else
                {
                    // If shards key doesn't exist yet (no clustering info), create it from storage data
                    var shardDetails = shardSizesList.Select(s => new ShardDetails
                    {
                        ShardId = s.ShardId,
                        State = null,
                        SizeBytes = s.SizeBytes
                    }).ToList();

                    collection.Metrics[MetricConstants.ShardsKey] = shardDetails;
                    
                    _logger.LogDebug(
                        "Created {ShardCount} shard(s) info from storage for collection {CollectionName} on node {NodeUrl}",
                        shardDetails.Count, collection.CollectionName, collection.NodeUrl);
                }
            }
        }
    }
    
    internal async Task<IEnumerable<ShardSize>> GetCollectionShardsSizesForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        string collectionName,
        CancellationToken cancellationToken)
    {
        if (_commandExecutor == null)
        {
            return [];
        }

        var shardSizes = new List<ShardSize>();

        try
        {
            var collectionPath = $"{QdrantConstants.StoragePath}/{collectionName}";
            
            var shardDirectories = await _commandExecutor.ListDirectoriesAsync(
                podName,
                podNamespace,
                collectionPath,
                cancellationToken);

            foreach (var shardDir in shardDirectories)
            {
                if (uint.TryParse(shardDir, out var shardId))
                {
                    var sizeBytes = await _commandExecutor.GetSizeAsync(
                        podName,
                        podNamespace,
                        collectionPath,
                        shardDir,
                        cancellationToken);

                    if (sizeBytes.HasValue)
                    {
                        var shardSize = new ShardSize
                        {
                            PodName = podName,
                            NodeUrl = nodeUrl,
                            PeerId = peerId,
                            CollectionName = collectionName,
                            ShardId = shardId,
                            SizeBytes = sizeBytes.Value
                        };

                        shardSizes.Add(shardSize);
                        
                        _logger.LogDebug(
                            "Shard size calculated: Collection={Collection}, Shard={ShardId}, Pod={PodName}, Size={Size}",
                            collectionName, shardId, podName, shardSize.PrettySize);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Found non-numeric directory '{ShardDir}' in collection '{Collection}' on pod '{PodName}'",
                        shardDir, collectionName, podName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to get shard sizes for collection {Collection} on pod {PodName}", 
                collectionName, podName);
        }

        return shardSizes;
    }

    internal async Task<IEnumerable<ShardSize>> GetAllShardsSizesForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken)
    {
        if (_commandExecutor == null)
        {
            return [];
        }

        var allShardSizes = new List<ShardSize>();

        try
        {
            var collections = await _commandExecutor.ListDirectoriesAsync(
                podName,
                podNamespace,
                QdrantConstants.StoragePath,
                cancellationToken);

            _logger.LogInformation(
                "Found {CollectionCount} collections on pod {PodName}, getting shard sizes...",
                collections.Count, podName);

            foreach (var collection in collections)
            {
                var shardSizes = await GetCollectionShardsSizesForPodAsync(
                    podName,
                    podNamespace,
                    nodeUrl,
                    peerId,
                    collection,
                    cancellationToken);

                allShardSizes.AddRange(shardSizes);
            }

            _logger.LogInformation(
                "Retrieved {ShardCount} shard sizes across {CollectionCount} collections on pod {PodName}",
                allShardSizes.Count, collections.Count, podName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all shard sizes for pod {PodName}", podName);
        }

        return allShardSizes;
    }

    private async Task EnrichCollectionsWithClusteringInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken)
    {
        // Get clustering info from each healthy node to get their local shards
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();

        if (healthyNodes.Count == 0)
        {
            _logger.LogWarning("No healthy nodes available for clustering info");

            return;
        }


        // Query each healthy node to get its local shards
        foreach (var node in healthyNodes)
        {
            await EnrichWithClusteringInfoAsync(node.Url, collections, peerToPodMap, cancellationToken);
        }
    }

}
