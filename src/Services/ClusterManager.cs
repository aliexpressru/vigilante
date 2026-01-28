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
    private IReadOnlyList<CollectionInfo>? _cachedCollections;
    private readonly Lock _cacheLock = new();

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        var tasks = nodes.Select(node => GetNodeInfoAsync(node, cancellationToken));
        var nodeStatuses = await Task.WhenAll(tasks);

        DetectClusterSplits(nodeStatuses);

        // Mark nodes with MessageSendFailures as unhealthy if they weren't identified as part of a cluster split
        foreach (var node in nodeStatuses)
        {
            if (node.IsHealthy && 
                node.ErrorType == NodeErrorType.MessageSendFailures)
            {
                node.IsHealthy = false;
                logger.LogInformation(ClusterConstants.MarkingNodeUnhealthyMessage, 
                    node.Url);
            }
        }

        var state = new ClusterState
        {
            Nodes = nodeStatuses.ToList(),
            LastUpdated = DateTime.UtcNow,
            StatefulSetName = await nodesProvider.GetStatefulSetNameAsync(cancellationToken)
        };

        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        await AddKubernetesWarningsIfNeededAsync(state, cancellationToken);

        return state;
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(bool clearCache = false,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetCollectionsInfoAsync (clearCache={ClearCache})", clearCache);

        // Check cache first if not clearing
        if (!clearCache)
        {
            lock (_cacheLock)
            {
                if (_cachedCollections != null)
                {
                    logger.LogInformation("Returning cached collections ({Count} items)", _cachedCollections.Count);

                    return _cachedCollections;
                }
            }
        }
        else
        {
            logger.LogInformation("Clearing collections cache");
        }

        var state = await GetClusterStateAsync(cancellationToken);
        var peerToPodMap = state.Nodes
            .Where(n => !string.IsNullOrEmpty(n.PeerId) && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId, n => n.PodName!);

        logger.LogInformation("Found {NodesCount} nodes. Healthy nodes: {HealthyCount}. Nodes with PodName: {PodNameCount}",
            state.Nodes.Count, state.Nodes.Count(n => n.IsHealthy), peerToPodMap.Count);

        // Log node details for debugging
        foreach (var node in state.Nodes)
        {
            logger.LogDebug("Node: URL={Url}, PeerId={PeerId}, PodName={PodName}, IsHealthy={IsHealthy}",
                node.Url, node.PeerId ?? "null", node.PodName ?? "null", node.IsHealthy);
        }

        // Get enriched collections info from CollectionService
        var result = await collectionService.GetEnrichedCollectionsInfoAsync(
            state.Nodes,
            peerToPodMap,
            cancellationToken);

        // Fallback to test data if no collections found
        if (result.Count == 0)
        {
            logger.LogWarning("No collections found from API. This might be because: " +
                "1) No healthy nodes available, " +
                "2) Nodes have no collections, " +
                "3) API connection failed. " +
                "Returning test data (only available in Development)");
            
            // Clear cache if we were asked to refresh and got empty result
            // This prevents returning stale cached data on next request
            if (clearCache)
            {
                lock (_cacheLock)
                {
                    _cachedCollections = null;
                    logger.LogInformation("Cleared collections cache due to empty result");
                }
            }
            
            return testDataProvider.GenerateTestCollectionData();
        }

        var collectionsWithIssues = result.Count(c => c.Issues.Count > 0);
        logger.LogInformation(
            "Completed GetCollectionsInfoAsync, found {TotalCollections} collections in total ({IssuesCount} with issues)",
            result.Count, collectionsWithIssues);

        // Cache the result
        lock (_cacheLock)
        {
            _cachedCollections = result;
            logger.LogInformation("Cached {Count} collections", result.Count);
        }

        return result;
    }

    public async Task<bool> ReplicateShardsAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod? shardTransferMethod,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting shard replication. Source: {SourcePeerId}, Target: {TargetPeerId}, Collection: {Collection}, " +
            "Shards: {ShardIds}, Move: {IsMove}, TransferMethod: {TransferMethod}",
            sourcePeerId, targetPeerId, collectionName, string.Join(", ", shardIds), isMove, 
            shardTransferMethod?.ToString() ?? "Snapshot (default)");

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
            shardTransferMethod,
            cancellationToken);
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionViaApiAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken)
    {
        var nodeUrlsList = nodeUrls.ToList();
        logger.LogInformation(
            "Deleting collection {CollectionName} via API on {NodeCount} specified nodes", 
            collectionName, 
            nodeUrlsList.Count);

        var results = new Dictionary<string, bool>();

        var deleteTasks = nodeUrlsList.Select(async nodeUrl =>
        {
            var success = await collectionService.DeleteCollectionViaApiAsync(
                nodeUrl,
                collectionName,
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
            "Collection {CollectionName} deleted via API: {SuccessCount}/{TotalCount} nodes",
            collectionName, 
            successCount, 
            results.Count);

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionFromDiskAsync(
        string collectionName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        CancellationToken cancellationToken)
    {
        var podsList = pods.ToList();
        logger.LogInformation(
            "Deleting collection {CollectionName} from disk on {PodCount} specified pods", 
            collectionName, 
            podsList.Count);

        var results = new Dictionary<string, bool>();

        var deleteTasks = podsList.Select(async pod =>
        {
            var success = await collectionService.DeleteCollectionFromDiskAsync(
                pod.PodName,
                pod.PodNamespace,
                collectionName,
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
            "Collection {CollectionName} deleted from disk: {SuccessCount}/{TotalCount} pods",
            collectionName, 
            successCount, 
            results.Count);

        return results;
    }

    private async Task<NodeInfo> GetNodeInfoAsync(QdrantNodeConfig node, CancellationToken cancellationToken)
    {
        // Get basic node info (URL, peer ID, pod name, etc.) from the nodes provider
        var nodeInfo = await nodesProvider.GetBasicNodeInfoAsync(node, cancellationToken);

        // Enrich with additional cluster information
        try
        {
            var client = clientFactory.CreateClient(node.Host, node.Port, _options.ApiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var clusterInfo = await client.GetClusterInfo(linkedCts.Token).WaitAsync(timeoutCts.Token);

            if (clusterInfo.Status.IsSuccess && clusterInfo.Result?.PeerId != null)
            {
                await ProcessClusterInfoResultAsync(nodeInfo, clusterInfo.Result, client, linkedCts.Token, cancellationToken);
            }
            else
            {
                HandleNodeError(nodeInfo, node, NodeErrorType.InvalidResponse, 
                    $"Failed to get cluster info: {clusterInfo.Status?.Error ?? "Invalid response"}", cancellationToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            HandleNodeError(nodeInfo, node, NodeErrorType.Timeout, "Request timed out", cancellationToken, ex);
        }
        catch (Exception ex)
        {
            HandleNodeError(nodeInfo, node, NodeErrorType.ConnectionError, ex.Message, cancellationToken, ex);
        }

        return nodeInfo;
    }

    private async Task ProcessClusterInfoResultAsync(
        NodeInfo nodeInfo,
        ClusterInfoResult clusterInfoResult,
        IQdrantHttpClient client,
        CancellationToken linkedToken,
        CancellationToken originalCancellationToken)
    {
        // PeerId is already set by GetBasicNodeInfoAsync, but verify it matches
        var expectedPeerId = clusterInfoResult.PeerId.ToString();
        if (string.IsNullOrEmpty(nodeInfo.PeerId))
        {
            nodeInfo.PeerId = expectedPeerId;
        }
        else if (nodeInfo.PeerId != expectedPeerId)
        {
            logger.LogWarning("PeerId mismatch for node {NodeUrl}: expected {Expected}, got {Actual}",
                nodeInfo.Url, nodeInfo.PeerId, expectedPeerId);
        }

        nodeInfo.IsLeader = clusterInfoResult.RaftInfo?.Leader != null &&
                            clusterInfoResult.RaftInfo.Leader.ToString() == clusterInfoResult.PeerId.ToString();

        await FetchQdrantVersionAsync(nodeInfo, client, linkedToken);
        CheckConsensusErrors(nodeInfo, clusterInfoResult);
        CheckMessageSendFailures(nodeInfo, clusterInfoResult);
        CollectPeerInformation(nodeInfo, clusterInfoResult);
        await CheckCollectionsHealthAsync(nodeInfo, client, linkedToken, originalCancellationToken);
        await FetchQdrantIssuesAsync(nodeInfo, client, linkedToken);

        // Set IsHealthy based on error type:
        // - ConsensusThreadError: immediately unhealthy (critical)
        // - MessageSendFailures: will be evaluated by DetectClusterSplits (might be split, not just unhealthy)
        // - Other errors or no errors: healthy
        if (nodeInfo.ErrorType == NodeErrorType.ConsensusThreadError)
        {
            nodeInfo.IsHealthy = false;
        }
        else if (nodeInfo.ErrorType == NodeErrorType.None || 
                 nodeInfo.ErrorType == NodeErrorType.MessageSendFailures)
        {
            // Healthy for now - MessageSendFailures will be handled by split detection
            nodeInfo.IsHealthy = true;
        }

        if (nodeInfo.Issues.Count > 0)
        {
            nodeInfo.ShortError = GetShortErrorMessage(nodeInfo.ErrorType);
        }
    }

    private async Task FetchQdrantVersionAsync(NodeInfo nodeInfo, IQdrantHttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var instanceDetails = await client.GetInstanceDetails(cancellationToken);
            if (instanceDetails != null)
            {
                nodeInfo.Version = instanceDetails.Version;
                logger.LogDebug("Node {NodeUrl} is running Qdrant version {Version}", nodeInfo.Url, nodeInfo.Version);
            }
            else
            {
                logger.LogWarning("Failed to get version for node {NodeUrl}: response was null", nodeInfo.Url);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch version for node {NodeUrl}", nodeInfo.Url);
        }
    }

    private void CheckConsensusErrors(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult)
    {
        if (clusterInfoResult.ConsensusThreadStatus?.Err != null)
        {
            var consensusError = clusterInfoResult.ConsensusThreadStatus.Err;
            nodeInfo.Issues.Add(ClusterConstants.ConsensusThreadErrorPrefix + consensusError);
            nodeInfo.ErrorType = NodeErrorType.ConsensusThreadError;
            logger.LogWarning("Node {NodeUrl} has consensus thread error: {Error}", nodeInfo.Url, consensusError);
        }
    }

    private void CheckMessageSendFailures(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult)
    {
        if (clusterInfoResult.MessageSendFailures == null || clusterInfoResult.MessageSendFailures.Count == 0)
            return;

        var consensusLastUpdate = clusterInfoResult.ConsensusThreadStatus?.LastUpdate;
        
        // Categorize failures into active and stale
        var activeFailures = new List<(string PeerId, MessageSendFailureUnit Failure)>();
        var staleFailures = new List<(string PeerId, MessageSendFailureUnit Failure)>();

        foreach (var failure in clusterInfoResult.MessageSendFailures)
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

        // Process active failures
        if (activeFailures.Count > 0)
        {
            var failuresStr = string.Join(", ", activeFailures.Select(f =>
                $"{f.PeerId}: {FormatMessageSendFailure(f.Failure)}"));
            nodeInfo.Issues.Add(ClusterConstants.MessageSendFailuresPrefix + failuresStr);

            if (nodeInfo.ErrorType == NodeErrorType.None)
            {
                nodeInfo.ErrorType = NodeErrorType.MessageSendFailures;
            }

            // Don't set IsHealthy = false here - let DetectClusterSplits determine if this is a split
            // If it's not a split, will mark it as unhealthy further up
            logger.LogWarning("Node {NodeUrl} has message send failures: {Failures}", nodeInfo.Url, failuresStr);
        }

        // Process stale failures
        if (staleFailures.Count > 0)
        {
            var staleFailuresStr = string.Join(", ", staleFailures.Select(f =>
                $"{f.PeerId}: {FormatMessageSendFailure(f.Failure)}"));
            nodeInfo.Warnings.Add(ClusterConstants.StaleMessageSendFailuresPrefix + staleFailuresStr);
            logger.LogInformation("Node {NodeUrl} has stale message send failures: {Failures}", nodeInfo.Url,
                staleFailuresStr);
        }
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
        CancellationToken originalCancellationToken)
    {
        try
        {
            var (isHealthy, errorMessage) = await collectionService
                .CheckCollectionsHealthAsync(client, linkedToken);

            if (!isHealthy)
            {
                nodeInfo.Issues.Add(errorMessage ?? "Failed to fetch collections");

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
            // If user cancelled - propagate without marking node as unhealthy
            if (originalCancellationToken.IsCancellationRequested)
                throw;

            // Otherwise it's a timeout - this indicates a real problem, mark node as unhealthy
            logger.LogWarning(ex, "Collections request timed out for node {NodeUrl}", nodeInfo.Url);
            nodeInfo.Issues.Add("Collections request timed out");

            if (nodeInfo.ErrorType == NodeErrorType.None)
            {
                nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
            }

            nodeInfo.IsHealthy = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch collections for node {NodeUrl}", nodeInfo.Url);
            nodeInfo.Issues.Add($"Failed to fetch collections: {ex.Message}");

            if (nodeInfo.ErrorType == NodeErrorType.None)
            {
                nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
            }

            nodeInfo.IsHealthy = false;
        }
    }

    private async Task FetchQdrantIssuesAsync(
        NodeInfo nodeInfo,
        IQdrantHttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
#pragma warning disable QD0001
            var issuesResponse = await client.ReportIssues(cancellationToken);
#pragma warning restore QD0001

            if (issuesResponse.Status.IsSuccess && issuesResponse.Result?.Issues != null)
            {
                var qdrantIssues = issuesResponse.Result.Issues;

                if (qdrantIssues.Length > 0)
                {
                    foreach (var issue in qdrantIssues)
                    {
                        var issueId = issue.Id?.Trim();
                        var description = issue.Description?.Trim();
                        var relatedCollection = issue.RelatedCollection?.Trim();

                        // Build issue message
                        var issueMessage = new System.Text.StringBuilder();

                        if (!string.IsNullOrWhiteSpace(issueId))
                        {
                            issueMessage.Append($"[{issueId}]");
                        }

                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            if (issueMessage.Length > 0)
                            {
                                issueMessage.Append(" ");
                            }

                            issueMessage.Append(description);
                        }

                        if (!string.IsNullOrWhiteSpace(relatedCollection))
                        {
                            issueMessage.Append($" ({ClusterConstants.CollectionIssuePrefix}{relatedCollection})");
                        }

                        var message = issueMessage.ToString();
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            nodeInfo.Issues.Add(message);

                            logger.LogDebug(
                                "Node {NodeUrl} issue: {IssueId} - {Description}",
                                nodeInfo.Url,
                                issueId ?? "unknown",
                                description ?? "no description");
                        }
                    }

                    logger.LogInformation(
                        "Node {NodeUrl} reported {Count} Qdrant issues",
                        nodeInfo.Url,
                        qdrantIssues.Length);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Qdrant issues for node {NodeUrl}", nodeInfo.Url);
        }
    }

    private void HandleNodeError(
        NodeInfo nodeInfo,
        QdrantNodeConfig node,
        NodeErrorType errorType,
        string errorMessage,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        // Re-throw if the original cancellation token was requested (user cancelled)
        // Don't re-throw if it was just a timeout (internal cancellation)
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            throw exception;

        // Set node state
        nodeInfo.PeerId = $"{node.Host}:{node.Port}";
        nodeInfo.IsHealthy = false;
        nodeInfo.Issues.Add(errorMessage);
        nodeInfo.ShortError = GetShortErrorMessage(errorType);
        nodeInfo.ErrorType = errorType;

        // Log the error
        if (exception != null)
        {
            logger.LogWarning(exception, "Failed to get status for node {NodeUrl}", nodeInfo.Url);
        }
        else
        {
            logger.LogWarning("Node {NodeUrl} error: {ErrorMessage}", nodeInfo.Url, errorMessage);
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
                        targetNode.Warnings.Add(ClusterConstants.KubernetesEventPrefix + warning);
                        logger.LogDebug("Added K8s event to node {NodeUrl}: {Warning}", targetNode.Url, warning);
                    }

                    logger.LogInformation(
                        "Added {Count} Kubernetes warning events to node {NodeUrl}. Total warnings on node: {TotalWarnings}",
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
                logger.LogInformation("No K8s warning events found in namespace {Namespace}",
                    namespaceToUse ?? "default");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Kubernetes warning events");
        }
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
                MarkNodeAsInconsistent(node, inconsistencyReason ?? "Unknown inconsistency");
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
        node.Issues.Add($"Potential cluster split detected: {inconsistencyReason}");
        node.ShortError = GetShortErrorMessage(NodeErrorType.ClusterSplit);
        node.ErrorType = NodeErrorType.ClusterSplit;

        logger.LogWarning(
            "Node {NodeUrl} (PeerId={PeerId}) is inconsistent with majority cluster state. Reason: {Reason}",
            node.Url, node.PeerId, inconsistencyReason);
    }


    private static string GetShortErrorMessage(NodeErrorType errorType) => errorType switch
    {
        NodeErrorType.Timeout => ClusterConstants.TimeoutError,
        NodeErrorType.ConnectionError => ClusterConstants.ConnectionError,
        NodeErrorType.InvalidResponse => ClusterConstants.InvalidResponseError,
        NodeErrorType.ClusterSplit => ClusterConstants.ClusterSplitError,
        NodeErrorType.CollectionsFetchError => ClusterConstants.CollectionsError,
        NodeErrorType.ConsensusThreadError => ClusterConstants.ConsensusError,
        NodeErrorType.MessageSendFailures => ClusterConstants.MessageSendFailuresError,
        _ => ClusterConstants.UnknownError
    };

    private static string FormatMessageSendFailure(object? failure)
    {
        if (failure == null)
            return ClusterConstants.UnknownErrorMessage;

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
                if (!latestError.Contains(ClusterConstants.MessagePrefix) && !latestError.Contains(ClusterConstants.StatusPrefix))
                {
                    return count > 1 
                        ? string.Format(ClusterConstants.FailureWithCountFormat, latestError, count) 
                        : latestError;
                }

                // Try to extract the main error message (e.g., "Can't send Raft message over channel")
                var messageStart = latestError.IndexOf(ClusterConstants.MessagePrefix, StringComparison.Ordinal);
                if (messageStart >= 0)
                {
                    messageStart += 10; // length of "message: \""
                    var messageEnd = latestError.IndexOf("\"", messageStart, StringComparison.Ordinal);
                    if (messageEnd > messageStart)
                    {
                        var message = latestError.Substring(messageStart, messageEnd - messageStart);
                        // Unescape common escape sequences
                        message = message.Replace("\\u0027", "'").Replace("\\\"", "\"");

                        return count > 1 
                            ? string.Format(ClusterConstants.FailureWithCountFormat, message, count) 
                            : message;
                    }
                }

                // Fallback: try to extract status
                var statusStart = latestError.IndexOf(ClusterConstants.StatusPrefix, StringComparison.Ordinal);
                if (statusStart >= 0)
                {
                    statusStart += 8; // length of "status: "
                    var statusEnd = latestError.IndexOf(",", statusStart, StringComparison.Ordinal);
                    if (statusEnd > statusStart)
                    {
                        var status = latestError.Substring(statusStart, statusEnd - statusStart);

                        return count > 1 
                            ? string.Format(ClusterConstants.ErrorWithCountFormat, status, count)
                            : status + ClusterConstants.ErrorSuffix;
                    }
                }
            }

            // If we can't parse it nicely, just show count
            return count > 0 
                ? string.Format(ClusterConstants.SendFailuresFormat, count) 
                : ClusterConstants.SendFailureMessage;
        }
        catch
        {
            // If parsing fails, return a simple message
            return ClusterConstants.CommunicationErrorMessage;
        }
    }
}