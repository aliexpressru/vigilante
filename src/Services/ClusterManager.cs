using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using System.Text.Json;
using ClusterInfoResult = Aer.QdrantClient.Http.Models.Responses.GetClusterInfoResponse.ClusterInfo;
using MessageSendFailureUnit = Aer.QdrantClient.Http.Models.Responses.GetClusterInfoResponse.MessageSendFailureUnit;

namespace Vigilante.Services;

public class ClusterManager(
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    ICollectionService collectionService,
    TestDataProvider testDataProvider,
    IOptions<QdrantOptions> options,
    ILogger<ClusterManager> logger,
    IMeterService meterService,
    IKubernetesManager? kubernetesManager) : IClusterManager
{
    private readonly QdrantOptions _options = options.Value;
    private readonly ClusterPeerState _clusterState = new();

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        var tasks = nodes.Select(node => GetNodeInfoAsync(node, cancellationToken));
        var nodeStatuses = await Task.WhenAll(tasks);

        DetectClusterSplits(nodeStatuses);
        FinalizeNodeHealthStatus(nodeStatuses);

        var state = new ClusterState
        {
            Nodes = nodeStatuses.ToList(),
            LastUpdated = DateTime.UtcNow
        };

        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        await AddKubernetesWarningsIfNeededAsync(state, cancellationToken);

        return state;
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetCollectionsInfoAsync");

        var state = await GetClusterStateAsync(cancellationToken);
        var peerToPodMap = CreatePeerToPodMap(state.Nodes);

        logger.LogInformation("Found {NodesCount} nodes to process. Healthy nodes: {HealthyCount}",
            state.Nodes.Count, state.Nodes.Count(n => n.IsHealthy));

        var result = await FetchCollectionsFromApiAsync(state.Nodes, cancellationToken);

        if (result.Count == 0)
        {
            logger.LogDebug("No collections found from API, returning test data");
            return testDataProvider.GenerateTestCollectionData();
        }

        if (HasPodsWithNames(state.Nodes))
        {
            await EnrichCollectionsWithStorageInfoAsync(state.Nodes, result, cancellationToken);
        }

        await EnrichCollectionsWithClusteringInfoAsync(state.Nodes, result, peerToPodMap, cancellationToken);

        LogCompletionSummary(result);

        return result;
    }

    public async Task<bool> ReplicateShardsAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting shard replication. Source: {SourcePeerId}, Target: {TargetPeerId}, Collection: {Collection}, " +
            "Shards: {ShardIds}, Move: {IsMove}",
            sourcePeerId, targetPeerId, collectionName, string.Join(", ", shardIds), isMove);

        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);

        if (healthyNode == null)
        {
            logger.LogError("No healthy nodes found to perform replication");
            return false;
        }

        return await collectionService.ReplicateShardsAsync(
            healthyNode.Url,
            sourcePeerId,
            targetPeerId,
            collectionName,
            shardIds,
            isMove,
            cancellationToken);
    }

    public async Task<bool> DeleteCollectionViaApiAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting collection {CollectionName} via API on node {NodeUrl}",
            collectionName, nodeUrl);

        return await collectionService.DeleteCollectionViaApiAsync(nodeUrl, collectionName, cancellationToken);
    }

    public async Task<bool> DeleteCollectionFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Deleting collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            collectionName, podName, podNamespace);

        return await collectionService.DeleteCollectionFromDiskAsync(podName, podNamespace, collectionName,
            cancellationToken);
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionViaApiOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting collection {CollectionName} via API on all nodes", collectionName);

        var state = await GetClusterStateAsync(cancellationToken);
        var results = new Dictionary<string, bool>();

        var deleteTasks = state.Nodes.Select(async node =>
        {
            var success = await collectionService.DeleteCollectionViaApiAsync(
                node.Url,
                collectionName,
                cancellationToken);

            var nodeIdentifier = !string.IsNullOrEmpty(node.PodName) ? node.PodName : node.Url;

            return (NodeIdentifier: nodeIdentifier, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var result in deleteResults)
        {
            results[result.NodeIdentifier] = result.Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation("Collection {CollectionName} deleted via API: {SuccessCount}/{TotalCount} nodes",
            collectionName, successCount, results.Count);

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionFromDiskOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting collection {CollectionName} from disk on all nodes", collectionName);

        var state = await GetClusterStateAsync(cancellationToken);
        var results = new Dictionary<string, bool>();

        var deleteTasks = state.Nodes
            .Where(n => !string.IsNullOrEmpty(n.PodName) && !string.IsNullOrEmpty(n.Namespace))
            .Select(async node =>
            {
                var success = await collectionService.DeleteCollectionFromDiskAsync(
                    node.PodName!,
                    node.Namespace!,
                    collectionName,
                    cancellationToken);

                return (PodName: node.PodName!, Success: success);
            });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var result in deleteResults)
        {
            results[result.PodName] = result.Success;
        }

        var successCount = results.Values.Count(s => s);
        logger.LogInformation("Collection {CollectionName} deleted from disk: {SuccessCount}/{TotalCount} pods",
            collectionName, successCount, results.Count);

        return results;
    }

    private async Task<NodeInfo> GetNodeInfoAsync(QdrantNodeConfig node, CancellationToken cancellationToken)
    {
        var nodeInfo = new NodeInfo
        {
            Url = $"{QdrantConstants.HttpProtocol}{node.Host}:{node.Port}",
            Namespace = node.Namespace,
            PodName = node.PodName,
            StatefulSetName = node.StatefulSetName,
            LastSeen = DateTime.UtcNow
        };

        try
        {
            var client = clientFactory.CreateClient(node.Host, node.Port, _options.ApiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var clusterInfo = await client.GetClusterInfo(linkedCts.Token).WaitAsync(timeoutCts.Token);
            
            if (clusterInfo.Status.IsSuccess && clusterInfo.Result?.PeerId != null)
            {
                await ProcessClusterInfoResultAsync(nodeInfo, clusterInfo.Result, client, linkedCts.Token, timeoutCts.Token, cancellationToken);
            }
            else
            {
                HandleInvalidClusterInfoResponse(nodeInfo, node, clusterInfo.Status?.Error);
            }
        }
        catch (OperationCanceledException ex)
        {
            HandleNodeQueryException(nodeInfo, node, ex, NodeErrorType.Timeout, "Request timed out", cancellationToken);
        }
        catch (Exception ex)
        {
            HandleNodeQueryException(nodeInfo, node, ex, NodeErrorType.ConnectionError, ex.Message, cancellationToken);
        }

        return nodeInfo;
    }

    private async Task ProcessClusterInfoResultAsync(
        NodeInfo nodeInfo,
        ClusterInfoResult clusterInfoResult,
        IQdrantHttpClient client,
        CancellationToken linkedToken,
        CancellationToken timeoutToken,
        CancellationToken originalToken)
    {
        nodeInfo.PeerId = clusterInfoResult.PeerId.ToString();
        nodeInfo.IsHealthy = true;
        nodeInfo.IsLeader = clusterInfoResult.RaftInfo?.Leader != null &&
                            clusterInfoResult.RaftInfo.Leader.ToString() == clusterInfoResult.PeerId.ToString();

        var errors = new List<string>();
        CheckConsensusErrors(nodeInfo, clusterInfoResult, errors);
        CheckMessageSendFailures(nodeInfo, clusterInfoResult, errors);
        CollectPeerInformation(nodeInfo, clusterInfoResult);
        await CheckCollectionsHealthAsync(nodeInfo, client, linkedToken, timeoutToken, originalToken, errors);

        if (errors.Count > 0)
        {
            nodeInfo.Error = string.Join("; ", errors);
            nodeInfo.ShortError = GetShortErrorMessage(nodeInfo.ErrorType);
        }
    }

    private void CheckConsensusErrors(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult, List<string> errors)
    {
        if (clusterInfoResult.ConsensusThreadStatus?.Err != null)
        {
            var consensusError = clusterInfoResult.ConsensusThreadStatus.Err;
            errors.Add("Consensus thread error: " + consensusError);
            nodeInfo.ErrorType = NodeErrorType.ConsensusThreadError;
            logger.LogWarning("Node {NodeUrl} has consensus thread error: {Error}", nodeInfo.Url, consensusError);
        }
    }

    private void CheckMessageSendFailures(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult, List<string> errors)
    {
        if (clusterInfoResult.MessageSendFailures == null || clusterInfoResult.MessageSendFailures.Count == 0)
            return;

        var consensusLastUpdate = clusterInfoResult.ConsensusThreadStatus?.LastUpdate;
        var (activeFailures, staleFailures) = CategorizeMessageSendFailures(
            clusterInfoResult.MessageSendFailures, 
            consensusLastUpdate);

        ProcessActiveFailures(nodeInfo, activeFailures, errors);
        ProcessStaleFailures(nodeInfo, staleFailures);
    }

    private (List<(string PeerId, MessageSendFailureUnit Failure)> Active, List<(string PeerId, MessageSendFailureUnit Failure)> Stale) 
        CategorizeMessageSendFailures(
            Dictionary<string, MessageSendFailureUnit> failures, 
            DateTime? consensusLastUpdate)
    {
        var activeFailures = new List<(string PeerId, MessageSendFailureUnit Failure)>();
        var staleFailures = new List<(string PeerId, MessageSendFailureUnit Failure)>();

        foreach (var failure in failures)
        {
            if (consensusLastUpdate.HasValue && failure.Value.LatestErrorTimestamp < consensusLastUpdate.Value)
            {
                staleFailures.Add((failure.Key, failure.Value));
            }
            else
            {
                activeFailures.Add((failure.Key, failure.Value));
            }
        }

        return (activeFailures, staleFailures);
    }

    private void ProcessActiveFailures(
        NodeInfo nodeInfo, 
        List<(string PeerId, MessageSendFailureUnit Failure)> activeFailures, 
        List<string> errors)
    {
        if (activeFailures.Count == 0)
            return;

        var failuresStr = string.Join(", ", activeFailures.Select(f => 
            $"{f.PeerId}: {FormatMessageSendFailure(f.Failure)}"));
        errors.Add($"Message send failures: {failuresStr}");
        
        if (nodeInfo.ErrorType == NodeErrorType.None)
        {
            nodeInfo.ErrorType = NodeErrorType.MessageSendFailures;
        }

        logger.LogWarning("Node {NodeUrl} has message send failures: {Failures}", nodeInfo.Url, failuresStr);
    }

    private void ProcessStaleFailures(
        NodeInfo nodeInfo, 
        List<(string PeerId, MessageSendFailureUnit Failure)> staleFailures)
    {
        if (staleFailures.Count == 0)
            return;

        var staleFailuresStr = string.Join(", ", staleFailures.Select(f => 
            $"{f.PeerId}: {FormatMessageSendFailure(f.Failure)}"));
        nodeInfo.Warnings.Add($"Stale message send failures (older than consensus update): {staleFailuresStr}");
        logger.LogInformation("Node {NodeUrl} has stale message send failures: {Failures}", nodeInfo.Url, staleFailuresStr);
    }

    private void CollectPeerInformation(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult)
    {
        if (clusterInfoResult.Peers != null)
        {
            nodeInfo.CurrentPeerIds =
            [
                ..clusterInfoResult.Peers.Keys,
                clusterInfoResult.PeerId.ToString()
            ];
        }
    }

    private async Task CheckCollectionsHealthAsync(
        NodeInfo nodeInfo,
        IQdrantHttpClient client,
        CancellationToken linkedToken,
        CancellationToken timeoutToken,
        CancellationToken originalToken,
        List<string> errors)
    {
        try
        {
            var (isHealthy, errorMessage) = await collectionService
                .CheckCollectionsHealthAsync(client, linkedToken)
                .WaitAsync(timeoutToken);

            if (!isHealthy)
            {
                errors.Add(errorMessage ?? "Failed to fetch collections");
                
                if (nodeInfo.ErrorType == NodeErrorType.None)
                {
                    nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                }

                logger.LogWarning("Node {NodeUrl} collections check failed: {Error}", nodeInfo.Url, errorMessage);
                nodeInfo.IsHealthy = false;
            }
        }
        catch (OperationCanceledException ex)
        {
            if (originalToken.IsCancellationRequested)
                throw;

            logger.LogWarning(ex, "Collections request timed out for node {NodeUrl}", nodeInfo.Url);
            errors.Add("Collections request timed out");
            
            if (nodeInfo.ErrorType == NodeErrorType.None)
            {
                nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
            }

            nodeInfo.IsHealthy = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch collections for node {NodeUrl}", nodeInfo.Url);
            errors.Add($"Failed to fetch collections: {ex.Message}");
            
            if (nodeInfo.ErrorType == NodeErrorType.None)
            {
                nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
            }

            nodeInfo.IsHealthy = false;
        }
    }

    private void HandleInvalidClusterInfoResponse(NodeInfo nodeInfo, QdrantNodeConfig node, string? errorDetails)
    {
        nodeInfo.PeerId = $"{node.Host}:{node.Port}";
        nodeInfo.IsHealthy = false;
        nodeInfo.Error = $"Failed to get cluster info: {errorDetails ?? "Invalid response"}";
        nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.InvalidResponse);
        nodeInfo.ErrorType = NodeErrorType.InvalidResponse;
        logger.LogWarning("Node {NodeUrl} returned invalid cluster info response. Error: {Error}", 
            nodeInfo.Url, errorDetails ?? "Invalid response");
    }

    private void HandleNodeQueryException(
        NodeInfo nodeInfo,
        QdrantNodeConfig node,
        Exception ex,
        NodeErrorType errorType,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            throw ex;

        logger.LogWarning(ex, "Failed to get status for node {NodeUrl}", nodeInfo.Url);
        nodeInfo.PeerId = $"{node.Host}:{node.Port}";
        nodeInfo.IsHealthy = false;
        nodeInfo.Error = errorMessage;
        nodeInfo.ShortError = GetShortErrorMessage(errorType);
        nodeInfo.ErrorType = errorType;
    }

    private void FinalizeNodeHealthStatus(NodeInfo[] nodeStatuses)
    {
        foreach (var node in nodeStatuses)
        {
            if (node.IsHealthy && !string.IsNullOrEmpty(node.Error) &&
                (node.ErrorType == NodeErrorType.ConsensusThreadError ||
                 node.ErrorType == NodeErrorType.MessageSendFailures))
            {
                node.IsHealthy = false;
                logger.LogInformation("Marking node {NodeUrl} as unhealthy due to {ErrorType}", node.Url, node.ErrorType);
            }
        }
    }

    private async Task AddKubernetesWarningsIfNeededAsync(ClusterState state, CancellationToken cancellationToken)
    {
        if (kubernetesManager == null)
        {
            logger.LogDebug("KubernetesManager is not available, skipping K8s events");
            return;
        }

        if (state.Status != ClusterStatus.Degraded)
        {
            logger.LogDebug("Cluster status is {Status}, skipping K8s events (only fetch for Degraded)", state.Status);
            return;
        }

        logger.LogInformation("Cluster is degraded, fetching Kubernetes warning events");

        var namespaceToUse = state.Nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.Namespace))?.Namespace;
        logger.LogInformation("Using namespace: {Namespace}", namespaceToUse ?? "default");

        try
        {
            var warningEvents = await kubernetesManager.GetWarningEventsAsync(namespaceToUse, cancellationToken);
            logger.LogInformation("Fetched {Count} K8s warning events", warningEvents.Count);

            if (warningEvents.Count > 0)
            {
                var targetNode = state.Nodes.FirstOrDefault(n => !n.IsHealthy) ?? state.Nodes.FirstOrDefault();

                if (targetNode != null)
                {
                    foreach (var warning in warningEvents)
                    {
                        targetNode.Warnings.Add($"K8s Event: {warning}");
                        logger.LogDebug("Added K8s event to node {NodeUrl}: {Warning}", targetNode.Url, warning);
                    }

                    logger.LogInformation("Added {Count} Kubernetes warning events to node {NodeUrl}. Total warnings on node: {TotalWarnings}",
                        warningEvents.Count, targetNode.Url, targetNode.Warnings.Count);
                    
                    // Force recalculation of Health to include new warnings
                    state.InvalidateCache();
                    logger.LogDebug("Invalidated ClusterState cache to recalculate health with new warnings");
                }
                else
                {
                    logger.LogWarning("No target node found to attach {Count} K8s warning events", warningEvents.Count);
                }
            }
            else
            {
                logger.LogInformation("No K8s warning events found in namespace {Namespace}", namespaceToUse ?? "default");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Kubernetes warning events");
        }
    }

    private Dictionary<string, string> CreatePeerToPodMap(IReadOnlyList<NodeInfo> nodes)
    {
        return nodes
            .Where(n => !string.IsNullOrEmpty(n.PeerId) && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId, n => n.PodName!);
    }

    private async Task<List<CollectionInfo>> FetchCollectionsFromApiAsync(
        IReadOnlyList<NodeInfo> nodes,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching collections from Qdrant API");
        var nodeInfos = nodes.Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName));
        var collectionsFromApi = await collectionService.GetCollectionsFromQdrantAsync(nodeInfos, cancellationToken);
        var result = collectionsFromApi.ToList();
        logger.LogInformation("Retrieved {Count} collections from Qdrant API", result.Count);
        return result;
    }

    private bool HasPodsWithNames(IReadOnlyList<NodeInfo> nodes)
    {
        return nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
    }

    private async Task EnrichCollectionsWithStorageInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        List<CollectionInfo> collections,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Enriching collections with storage information from Kubernetes");

        var storageCollections = await FetchStorageCollectionSizesAsync(nodes, cancellationToken);

        logger.LogInformation("Found {Count} collections in storage across all nodes", storageCollections.Count);

        EnrichCollectionsWithStorageData(collections, storageCollections);
    }

    private async Task<Dictionary<(string NodeUrl, string CollectionName), CollectionSize>> FetchStorageCollectionSizesAsync(
        IReadOnlyList<NodeInfo> nodes,
        CancellationToken cancellationToken)
    {
        var storageCollections = new Dictionary<(string NodeUrl, string CollectionName), CollectionSize>();

        foreach (var node in nodes)
        {
            try
            {
                var podName = await ResolvePodNameAsync(node, cancellationToken);

                if (string.IsNullOrEmpty(podName))
                    continue;

                logger.LogInformation("Found pod {PodName} for IP {NodeUrl}, fetching storage info", podName, node.Url);

                var collectionSizes = await collectionService.GetCollectionsSizesForPodAsync(
                    podName,
                    node.Namespace ?? "",
                    node.Url,
                    node.PeerId,
                    cancellationToken);

                foreach (var size in collectionSizes)
                {
                    storageCollections[(size.NodeUrl, size.CollectionName)] = size;
                }

                logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}",
                    collectionSizes.Count(), podName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get collection sizes for node {NodeUrl}", node.Url);
            }
        }

        return storageCollections;
    }

    private async Task<string?> ResolvePodNameAsync(NodeInfo node, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(node.PodName))
            return node.PodName;

        var podName = await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);

        if (string.IsNullOrEmpty(podName))
        {
            logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}",
                node.Url, node.Namespace);
        }

        return podName;
    }

    private void EnrichCollectionsWithStorageData(
        List<CollectionInfo> collections,
        Dictionary<(string NodeUrl, string CollectionName), CollectionSize> storageCollections)
    {
        foreach (var collection in collections)
        {
            var key = (collection.NodeUrl, collection.CollectionName);

            if (storageCollections.TryGetValue(key, out var storageInfo))
            {
                collection.Metrics[MetricConstants.PrettySizeKey] = storageInfo.PrettySize;
                collection.Metrics[MetricConstants.SizeBytesKey] = storageInfo.SizeBytes;

                logger.LogDebug("Enriched collection {CollectionName} on {NodeUrl} with storage data: {Size}",
                    collection.CollectionName, collection.NodeUrl, storageInfo.PrettySize);
            }
            else
            {
                collection.Issues.Add("Collection exists in API but not found in storage");

                logger.LogWarning("Collection {CollectionName} on node {NodeUrl} exists in API but not in storage!",
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
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();

        if (healthyNodes.Count == 0)
        {
            logger.LogWarning("No healthy nodes found, skipping sharding information collection");
            return;
        }

        logger.LogInformation("Enriching collections with clustering info from {HealthyNodeCount} healthy nodes",
            healthyNodes.Count);

        foreach (var healthyNode in healthyNodes)
        {
            await collectionService.EnrichWithClusteringInfoAsync(
                healthyNode.Url,
                collections,
                peerToPodMap,
                cancellationToken);
        }
    }

    private void LogCompletionSummary(List<CollectionInfo> collections)
    {
        var collectionsWithIssues = collections.Count(c => c.Issues.Count > 0);
        logger.LogInformation(
            "Completed GetCollectionsInfoAsync, found {TotalCollections} collections in total ({IssuesCount} with issues)",
            collections.Count, collectionsWithIssues);
    }


    private void DetectClusterSplits(NodeInfo[] nodes)
    {
        var healthyNodes = nodes.Where(n => n.IsHealthy && n.CurrentPeerIds.Count > 0).ToList();

        if (healthyNodes.Count == 0)
        {
            logger.LogInformation("No healthy nodes with peer information to analyze for splits");
            return;
        }

        if (!EstablishMajorityClusterState(healthyNodes))
        {
            return;
        }

        CheckNodesAgainstMajorityState(healthyNodes);
    }

    private bool EstablishMajorityClusterState(List<NodeInfo> healthyNodes)
    {
        if (!_clusterState.TryUpdateMajorityState(healthyNodes))
        {
            logger.LogWarning("Could not establish majority cluster state from {HealthyNodeCount} healthy nodes",
                healthyNodes.Count);
            return false;
        }

        logger.LogInformation("Established majority cluster state with peer IDs: {PeerIds}",
            string.Join(", ", _clusterState.MajorityPeerIds));
        return true;
    }

    private void CheckNodesAgainstMajorityState(List<NodeInfo> healthyNodes)
    {
        foreach (var node in healthyNodes)
        {
            if (!_clusterState.IsNodeConsistentWithMajority(node, out var inconsistencyReason))
            {
                MarkNodeAsInconsistent(node, inconsistencyReason);
            }
            else
            {
                logger.LogDebug("Node {NodeUrl} (PeerId={PeerId}) is consistent with majority cluster state",
                    node.Url, node.PeerId);
            }
        }
    }

    private void MarkNodeAsInconsistent(NodeInfo node, string inconsistencyReason)
    {
        node.IsHealthy = false;
        node.Error = $"Potential cluster split detected: {inconsistencyReason}";
        node.ShortError = GetShortErrorMessage(NodeErrorType.ClusterSplit);
        node.ErrorType = NodeErrorType.ClusterSplit;

        logger.LogWarning(
            "Node {NodeUrl} (PeerId={PeerId}) is inconsistent with majority cluster state. Reason: {Reason}",
            node.Url, node.PeerId, inconsistencyReason);
    }

    private async Task<string?> GetPodNameFromIpAsync(string podUrl, string podNamespace,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(podUrl);
        var podIp = uri.Host;

        logger.LogInformation("Getting pod name for IP {PodIp} in namespace {Namespace}", podIp, podNamespace);

        if (kubernetesManager == null)
        {
            logger.LogDebug("Kubernetes manager not available, cannot resolve pod name");
            return null;
        }

        return await kubernetesManager.GetPodNameByIpAsync(podIp, podNamespace, cancellationToken);
    }

    private static string GetShortErrorMessage(NodeErrorType errorType) => errorType switch
    {
        NodeErrorType.Timeout => "Timeout",
        NodeErrorType.ConnectionError => "Connection Error",
        NodeErrorType.InvalidResponse => "Invalid Response",
        NodeErrorType.ClusterSplit => "Cluster Split",
        NodeErrorType.CollectionsFetchError => "Collections Error",
        NodeErrorType.ConsensusThreadError => "Consensus Error",
        NodeErrorType.MessageSendFailures => "Message Send Failures",
        _ => "Unknown Error"
    };

    private static string FormatMessageSendFailure(object? failure)
    {
        if (failure == null)
            return "unknown error";

        try
        {
            var json = JsonSerializer.Serialize(failure);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract count and latest error
            var count = root.TryGetProperty("Count", out var countProp) ? countProp.GetInt32() : 0;
            var latestError = root.TryGetProperty("LatestError", out var errorProp) ? errorProp.GetString() : null;

            // Parse the latest error to extract just the important message
            if (!string.IsNullOrEmpty(latestError))
            {
                // If it's a simple string (doesn't contain structured data), return it directly
                if (!latestError.Contains("message: \"") && !latestError.Contains("status: "))
                {
                    return count > 1 ? $"{latestError} ({count} failures)" : latestError;
                }
                
                // Try to extract the main error message (e.g., "Can't send Raft message over channel")
                var messageStart = latestError.IndexOf("message: \"", StringComparison.Ordinal);
                if (messageStart >= 0)
                {
                    messageStart += 10; // length of "message: \""
                    var messageEnd = latestError.IndexOf("\"", messageStart, StringComparison.Ordinal);
                    if (messageEnd > messageStart)
                    {
                        var message = latestError.Substring(messageStart, messageEnd - messageStart);
                        // Unescape common escape sequences
                        message = message.Replace("\\u0027", "'").Replace("\\\"", "\"");

                        return count > 1 ? $"{message} ({count} failures)" : message;
                    }
                }

                // Fallback: try to extract status
                var statusStart = latestError.IndexOf("status: ", StringComparison.Ordinal);
                if (statusStart >= 0)
                {
                    statusStart += 8; // length of "status: "
                    var statusEnd = latestError.IndexOf(",", statusStart, StringComparison.Ordinal);
                    if (statusEnd > statusStart)
                    {
                        var status = latestError.Substring(statusStart, statusEnd - statusStart);

                        return count > 1 ? $"{status} error ({count} failures)" : $"{status} error";
                    }
                }
            }

            // If we can't parse it nicely, just show count
            return count > 0 ? $"{count} send failures" : "send failure";
        }
        catch
        {
            // If parsing fails, return a simple message
            return "communication error";
        }
    }
}