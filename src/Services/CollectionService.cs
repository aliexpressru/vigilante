using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Requests;
using Aer.QdrantClient.Http.Models.Requests.Public;
using Aer.QdrantClient.Http.Models.Responses;
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
                        return (_cachedCollections, true, null);
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


        var nodeResults = await Task.WhenAll(
            nodesList.Select(node => GetCollectionsFromSingleNodeAsync(node, cancellationToken)));

        var result = new List<CollectionInfo>();
        var overallHealthy = true;
        string? overallErrorMessage = null;

        foreach (var (collections, isHealthy) in nodeResults)
        {
            result.AddRange(collections);
            if (!isHealthy)
                overallHealthy = false;
        }

        // Cache the result if successful
        if (overallHealthy && result.Count > 0)
        {
            lock (_cacheLock)
            {
                _cachedCollections = result;
            }
        }

        return (result, overallHealthy, overallErrorMessage);
    }

    private async Task<(List<CollectionInfo> Collections, bool IsHealthy)> GetCollectionsFromSingleNodeAsync(
        (string Url, string PeerId, string? Namespace, string? PodName) node,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(node.Url, _options.ApiKey);

            var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
            if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
            {
                var errorDetails = collectionsResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage;
                _logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}", node.Url, errorDetails);
                return ([], false);
            }

            var collections = collectionsResponse.Result.Collections;
            if (collections.Length == 0)
                return ([], true);

            Dictionary<string, List<string>> collectionAliases = new();
            try
            {
                var aliasesResponse = await qdrantClient.ListAllAliases(cancellationToken);
                if (aliasesResponse?.Status?.IsSuccess == true && aliasesResponse.Result?.Aliases != null)
                {
                    collectionAliases = aliasesResponse.Result.Aliases
                        .GroupBy(a => a.CollectionName)
                        .ToDictionary(g => g.Key, g => g.Select(a => a.AliasName).ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get aliases from node {NodeUrl}", node.Url);
            }

            var infoTasks = collections.Select(async collection =>
            {
                try
                {
                    var collectionInfoResponse = await qdrantClient.GetCollectionInfo(collection.Name, cancellationToken);
                    if (!collectionInfoResponse.Status.IsSuccess)
                    {
                        _logger.LogWarning("Failed to get info for collection {CollectionName} from node {NodeUrl}: {Error}",
                            collection.Name, node.Url, collectionInfoResponse.Status?.Error ?? MetricConstants.UnknownErrorMessage);
                        return null;
                    }

                    var aliases = collectionAliases.TryGetValue(collection.Name, out var aliasList)
                        ? aliasList
                        : new List<string>();

                    List<CollectionOptimizationInfo> runningOptimizations = [];
                    try
                    {
                        var optimizationProgressResponse = await qdrantClient.GetCollectionOptimizationProgress(
                            collection.Name,
                            cancellationToken);

                        if (optimizationProgressResponse?.Status?.IsSuccess == true)
                        {
                            runningOptimizations = BuildRunningOptimizations(optimizationProgressResponse);
                        }
                    }
                    catch (Exception)
                    {
                    }

                    var collectionInfo = new CollectionInfo
                    {
                        CollectionName = collection.Name,
                        NodeUrl = node.Url,
                        PodName = node.PodName ?? MetricConstants.UnknownPodName,
                        PeerId = node.PeerId,
                        PodNamespace = node.Namespace ?? string.Empty,
                        Metrics = new CollectionMetrics
                        {
                            [MetricConstants.PrettySizeKey] = MetricConstants.NotAvailableValue
                        },
                        Aliases = aliases,
                        Status = collectionInfoResponse.Result?.Status,
                        HnswM = collectionInfoResponse.Result?.Config?.HnswConfig?.M,
                        RunningOptimizations = runningOptimizations
                    };

                    return collectionInfo;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get info for collection {CollectionName} from node {NodeUrl}",
                        collection.Name, node.Url);
                    return null;
                }
            });

            var infos = (await Task.WhenAll(infoTasks)).OfType<CollectionInfo>().ToList();
            var isHealthy = infos.Count == collections.Length;
            return (infos, isHealthy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collections from node {NodeUrl}", node.Url);
            return ([], false);
        }
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

        await EnrichCollectionsWithMemoryReportAsync(healthyNodes, collections, cancellationToken);

        // Enrich shards with vectors/payload sizes from cluster telemetry
        await EnrichCollectionsWithTelemetryAsync(healthyNodes, collections, cancellationToken);

        PopulateCollectionWarnings(collections);

        // Sort collection shards by node within each collection:
        // Group by collection name, sort nodes within each group, then flatten back
        collections = collections
            .GroupBy(c => c.CollectionName)
            .SelectMany(group => group.OrderBy(c => NodeSortingExtensions.GetNodeSortKey(c.PodName, c.PeerId)))
            .ToList();

        // Log summary with unique collection names
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

    public async Task<bool> SetCollectionAliasAsync(
        string nodeUrl,
        string collectionName,
        string aliasName,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);

            var allAliasesResponse = await qdrantClient.ListAllAliases(cancellationToken);
            if (allAliasesResponse?.Status?.IsSuccess != true || allAliasesResponse.Result?.Aliases == null)
            {
                _logger.LogWarning("Failed to get aliases from node {NodeUrl}: {Error}",
                    nodeUrl, allAliasesResponse?.Status?.Error ?? "Unknown");
                return false;
            }

            var currentForCollection = allAliasesResponse.Result.Aliases
                .Where(a => string.Equals(a.CollectionName, collectionName, StringComparison.Ordinal))
                .Select(a => a.AliasName)
                .ToList();
            if (currentForCollection.Contains(aliasName, StringComparer.Ordinal))
            {
                _logger.LogInformation("Alias {AliasName} already exists for collection {CollectionName} on node {NodeUrl}",
                    aliasName, collectionName, nodeUrl);
                return true;
            }

            var aliasUsedByOther = allAliasesResponse.Result.Aliases
                .FirstOrDefault(a => string.Equals(a.AliasName, aliasName, StringComparison.Ordinal) && !string.Equals(a.CollectionName, collectionName, StringComparison.Ordinal));
            if (aliasUsedByOther != null)
            {
                _logger.LogWarning("Alias {AliasName} is already used by collection {OtherCollection}, cannot add to {CollectionName}",
                    aliasName, aliasUsedByOther.CollectionName, collectionName);
                return false;
            }

            var request = UpdateCollectionAliasesRequest.Create().CreateAlias(collectionName, aliasName);
            var result = await qdrantClient.UpdateCollectionsAliases(request, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Alias {AliasName} set for collection {CollectionName} on node {NodeUrl}",
                    aliasName, collectionName, nodeUrl);
                return true;
            }

            _logger.LogError("Failed to set alias {AliasName} for collection {CollectionName}: {Error}",
                aliasName, collectionName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set alias {AliasName} for collection {CollectionName} on node {NodeUrl}",
                aliasName, collectionName, nodeUrl);
            return false;
        }
    }

    public async Task<bool> RenameCollectionAliasAsync(
        string nodeUrl,
        string oldAliasName,
        string newAliasName,
        CancellationToken cancellationToken)
    {
        if (string.Equals(oldAliasName, newAliasName, StringComparison.Ordinal))
        {
            _logger.LogInformation("Old and new alias names are the same, no-op");
            return true;
        }

        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var request = UpdateCollectionAliasesRequest.Create().RenameAlias(oldAliasName, newAliasName);
            var result = await qdrantClient.UpdateCollectionsAliases(request, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Alias renamed from {OldAliasName} to {NewAliasName} on node {NodeUrl}",
                    oldAliasName, newAliasName, nodeUrl);
                return true;
            }

            _logger.LogError("Failed to rename alias {OldAliasName} to {NewAliasName}: {Error}",
                oldAliasName, newAliasName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename alias {OldAliasName} to {NewAliasName} on node {NodeUrl}",
                oldAliasName, newAliasName, nodeUrl);
            return false;
        }
    }

    public async Task<bool> DeleteCollectionAliasAsync(
        string nodeUrl,
        string aliasName,
        CancellationToken cancellationToken)
    {
        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(nodeUrl, _options.ApiKey);
            var request = UpdateCollectionAliasesRequest.Create().DeleteAlias(aliasName);
            var result = await qdrantClient.UpdateCollectionsAliases(request, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("Alias {AliasName} deleted on node {NodeUrl}", aliasName, nodeUrl);
                return true;
            }

            _logger.LogError("Failed to delete alias {AliasName}: {Error}",
                aliasName, result?.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete alias {AliasName} on node {NodeUrl}", aliasName, nodeUrl);
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

    private static List<CollectionOptimizationInfo> BuildRunningOptimizations(GetCollectionOptimizationProgressResponse response)
    {
        var running = response.Result?.Running;
        if (running == null || running.Length == 0)
        {
            return [];
        }

        return running
            .Select(r =>
            {
                return new CollectionOptimizationInfo
                {
                    Optimizer = r.Optimizer,
                    SegmentsCount = r.Segments?.Length ?? 0,
                    Done = r.Progress?.Done,
                    Total = r.Progress?.Total
                };
            })
            .ToList();
    }

    private static void PopulateCollectionWarnings(List<CollectionInfo> collections)
    {
        foreach (var collectionGroup in collections.GroupBy(c => c.CollectionName))
        {
            var warnings = new List<string>();

            var hasNonActiveShards = collectionGroup.Any(nodeCollection =>
                nodeCollection.Metrics.Shards?.Any(shard =>
                    !shard.IsActive) == true);

            if (hasNonActiveShards)
            {
                warnings.Add(CollectionWarningConstants.NonActiveShardsWarning);
            }

            var hasActiveTransfers = collectionGroup.Any(nodeCollection =>
                nodeCollection.Metrics.OutgoingTransfers?.Count > 0);

            if (hasActiveTransfers)
            {
                warnings.Add(CollectionWarningConstants.ActiveTransfersWarning);
            }

            var runningOptimizations = collectionGroup
                .SelectMany(nodeCollection => nodeCollection.RunningOptimizations)
                .DistinctBy(optimization =>
                    $"{optimization.Optimizer}|{optimization.SegmentsCount}|{optimization.Done}|{optimization.Total}")
                .ToList();

            if (runningOptimizations.Count != 0)
            {
                var formattedOptimizations = runningOptimizations
                    .Select(optimization =>
                    {
                        if (optimization.Done.HasValue && optimization.Total.HasValue)
                        {
                            return string.Format(
                                CollectionWarningConstants.OptimizerWithSegmentsAndProgressFormat,
                                optimization.Optimizer,
                                optimization.SegmentsCount,
                                optimization.Done.Value,
                                optimization.Total.Value);
                        }

                        return string.Format(
                            CollectionWarningConstants.OptimizerWithSegmentsFormat,
                            optimization.Optimizer,
                            optimization.SegmentsCount);
                    })
                    .ToList();

                warnings.Add(
                    $"{CollectionWarningConstants.RunningOptimizationsPrefix} ({runningOptimizations.Count}): " +
                    string.Join("; ", formattedOptimizations));
            }

            var finalWarnings = warnings.Count == 0
                ? new List<string>()
                : new List<string> { string.Join("; ", warnings) };

            foreach (var nodeCollection in collectionGroup)
            {
                nodeCollection.Warnings = finalWarnings;
            }
        }
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

            var clusteringTasks = collectionNames.Select(async collectionName =>
            {
                try
                {
                    var clusteringInfo =
                        await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);
                    return (collectionName, clusteringInfo);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get clustering info for collection {Collection} on node {NodeUrl}",
                        collectionName, healthyNodeUrl);
                    return (collectionName, (Aer.QdrantClient.Http.Models.Responses.GetCollectionClusteringInfoResponse?)null);
                }
            });

            var clusteringResults = await Task.WhenAll(clusteringTasks);
            foreach (var (collectionName, clusteringInfo) in clusteringResults)
            {
                if (clusteringInfo?.Status?.IsSuccess != true || clusteringInfo.Result == null)
                    continue;

                var info = collectionInfos.FirstOrDefault(c =>
                    c.CollectionName == collectionName && c.NodeUrl == healthyNodeUrl);

                if (info == null)
                    continue;

                UpdateShardMetrics(info, clusteringInfo.Result);
                UpdateTransferMetrics(info, clusteringInfo.Result, peerToPodMap);
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
                State = shard.State.ToString()
            });
            shardStates[shard.ShardId.ToString()] = shard.State.ToString();
        }

        if (shardDetails.Count != 0)
        {
            info.Metrics.Shards = shardDetails;
            info.Metrics.ShardStates = shardStates;
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
            .Select(t => new OutgoingTransferInfo
            {
                ShardId = t.ShardId,
                To = peerToPodMap.TryGetValue(t.To.ToString(), out var podName) ? podName : t.To.ToString(),
                ToPeerId = t.To.ToString(),
                IsSync = t.Sync,
                Method = t.Method.ToString()
            })
            .ToList();

        if (outgoingTransfers.Count != 0)
        {
            info.Metrics.OutgoingTransfers = outgoingTransfers;
        }
    }


    /// <summary>
    /// Enriches collection metrics with cluster-wide disk/RAM usage from Qdrant memory report API.
    /// Any healthy node returns the same report for the whole collection.
    /// </summary>
    private async Task EnrichCollectionsWithMemoryReportAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        CancellationToken cancellationToken)
    {
        if (collections.Count == 0)
            return;

        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(nodes[0].Url, _options.ApiKey);
            var collectionNames = collections.Select(c => c.CollectionName).Distinct(StringComparer.Ordinal);

            var reportTasks = collectionNames.Select(async collectionName =>
            {
                try
                {
                    var response = await qdrantClient.GetCollectionMemoryReport(collectionName, cancellationToken);
                    if (response?.Status?.IsSuccess != true || response.Result == null)
                    {
                        _logger.LogWarning(
                            "Failed to get memory report for collection {CollectionName}: {Error}",
                            collectionName,
                            response?.Status?.GetErrorMessage() ?? MetricConstants.UnknownErrorMessage);
                        return (collectionName, (CollectionMemoryReportInfo?)null);
                    }

                    return (collectionName, (CollectionMemoryReportInfo?)response.Result.ToInfo());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get memory report for collection {CollectionName}", collectionName);
                    return (collectionName, (CollectionMemoryReportInfo?)null);
                }
            });

            var reports = await Task.WhenAll(reportTasks);
            foreach (var (collectionName, report) in reports)
            {
                if (report == null)
                    continue;

                var diskBytes = (long)report.Total.DiskBytes;
                var ramBytes = (long)report.Total.RamBytes;

                foreach (var collection in collections.Where(c =>
                             string.Equals(c.CollectionName, collectionName, StringComparison.Ordinal)))
                {
                    collection.Metrics.SizeBytes = diskBytes;
                    collection.Metrics.PrettySize = diskBytes.ToPrettySize();
                    collection.Metrics.RamBytes = ramBytes;
                    collection.Metrics.PrettyRamSize = ramBytes.ToPrettySize();
                    collection.Metrics.MemoryReport = report;

                    _meterService.UpdateCollectionSize(new CollectionSize
                    {
                        CollectionName = collectionName,
                        NodeUrl = collection.NodeUrl,
                        PeerId = collection.PeerId,
                        PodName = collection.PodName,
                        SizeBytes = diskBytes
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich collections with memory report from node {NodeUrl}", nodes[0].Url);
        }
    }

    /// <summary>
    /// Enriches shard metrics with logical vectors/payload sizes from Qdrant cluster telemetry.
    /// Uses a single GET /cluster/telemetry call from one healthy node, which already returns
    /// a cluster-wide aggregated view (no need to query every peer separately).
    /// </summary>
    private async Task EnrichCollectionsWithTelemetryAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        CancellationToken cancellationToken)
    {
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();
        if (healthyNodes.Count == 0)
            return;

        try
        {
            var qdrantClient = _clientFactory.CreateClientFromUrl(healthyNodes[0].Url, _options.ApiKey);
            var response = await qdrantClient.GetClusterTelemetry(cancellationToken);

            if (response?.Status?.IsSuccess != true || response.Result?.Collections == null)
                return;
            
            foreach (var (collectionName, collTel) in response.Result.Collections)
            {
                if (collTel?.Shards == null)
                    continue;

                foreach (var shard in collTel.Shards)
                {
                    if (shard.Replicas == null)
                        continue;

                    foreach (var replica in shard.Replicas)
                    {
                        var peerIdStr = replica.PeerId.ToString();
                        var info = collections.FirstOrDefault(c =>
                            c.CollectionName == collectionName && c.PeerId == peerIdStr);
                        if (info == null)
                            continue;

                        if (info.Metrics.Shards is not { } shardDetails)
                            continue;

                        var detail = shardDetails.FirstOrDefault(s => s.ShardId == shard.Id);
                        if (detail == null)
                            continue;

                        detail.VectorsSizeBytes = replica.VectorsSizeBytes;
                        detail.PayloadsSizeBytes = replica.PayloadsSizeBytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cluster telemetry for vectors/payload sizes");
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


        await Task.WhenAll(
            healthyNodes.Select(node => EnrichWithClusteringInfoAsync(node.Url, collections, peerToPodMap, cancellationToken)));
    }
}
