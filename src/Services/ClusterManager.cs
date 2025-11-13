using k8s;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using System.Text.Json;

namespace Vigilante.Services;

public class ClusterManager(
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    ICollectionService collectionService,
    TestDataProvider testDataProvider,
    IOptions<QdrantOptions> options,
    ILogger<ClusterManager> logger,
    IMeterService meterService)
{
    private readonly QdrantOptions _options = options.Value;
    private readonly ClusterPeerState _clusterState = new();
    private readonly Lazy<IKubernetes?> _kubernetes = new(() => InitializeKubernetesClient(logger));

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);

        var tasks = nodes.Select(async node =>
        {
            var nodeInfo = new NodeInfo
            {
                Url = $"http://{node.Host}:{node.Port}",
                Namespace = node.Namespace,
                PodName = node.PodName,
                LastSeen = DateTime.UtcNow
            };

            try
            {
                var client = clientFactory.CreateClient(node.Host, node.Port, _options.ApiKey);
                
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                var clusterInfoTask = client.GetClusterInfo(linkedCts.Token);
                var clusterInfo = await clusterInfoTask.WaitAsync(timeoutCts.Token);
                if (clusterInfo.Status.IsSuccess && clusterInfo.Result?.PeerId != null)
                {
                    nodeInfo.PeerId = clusterInfo.Result.PeerId.ToString();
                    nodeInfo.IsHealthy = true;
                    nodeInfo.IsLeader = clusterInfo.Result.RaftInfo?.Leader != null &&
                                        clusterInfo.Result.RaftInfo.Leader.ToString() == clusterInfo.Result.PeerId.ToString();
                    
                    var errors = new List<string>();
                    
                    // Check consensus thread status for errors
                    if (clusterInfo.Result.ConsensusThreadStatus?.Err != null)
                    {
                        var consensusError = clusterInfo.Result.ConsensusThreadStatus.Err;
                        errors.Add($"Consensus thread error: {consensusError}");
                        nodeInfo.ErrorType = NodeErrorType.ConsensusThreadError;
                        logger.LogWarning("Node {NodeUrl} has consensus thread error: {Error}", nodeInfo.Url, consensusError);
                    }
                    
                    // Check message send failures
                    if (clusterInfo.Result.MessageSendFailures != null && clusterInfo.Result.MessageSendFailures.Count > 0)
                    {
                        var failures = string.Join(", ", clusterInfo.Result.MessageSendFailures.Select(kvp => 
                        {
                            var formattedError = FormatMessageSendFailure(kvp.Value);
                            return $"{kvp.Key}: {formattedError}";
                        }));
                        errors.Add($"Message send failures: {failures}");
                        // Only set error type if not already set by consensus error
                        if (nodeInfo.ErrorType == NodeErrorType.None)
                        {
                            nodeInfo.ErrorType = NodeErrorType.MessageSendFailures;
                        }
                        logger.LogWarning("Node {NodeUrl} has message send failures: {Failures}", nodeInfo.Url, failures);
                    }
                    
                    // Collect peer information for split detection
                    if (clusterInfo.Result.Peers != null)
                    {
                        nodeInfo.CurrentPeerIds =
                        [
                            ..clusterInfo.Result.Peers.Keys,
                            clusterInfo.Result.PeerId.ToString()
                        ];
                    }
                    
                    // Also check collections availability
                    try
                    {
                        var collectionsTask = collectionService.CheckCollectionsHealthAsync(client, linkedCts.Token);
                        var (isHealthy, errorMessage) = await collectionsTask.WaitAsync(timeoutCts.Token);
                        
                        if (!isHealthy)
                        {
                            errors.Add(errorMessage ?? "Failed to fetch collections");
                            // Only set error type if not already set by consensus or message send errors
                            if (nodeInfo.ErrorType == NodeErrorType.None)
                            {
                                nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                            }
                            logger.LogWarning("Node {NodeUrl} collections check failed: {Error}", nodeInfo.Url, errorMessage);
                            // Mark as unhealthy immediately for collection fetch errors
                            nodeInfo.IsHealthy = false;
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw;

                        logger.LogWarning(ex, "Collections request timed out for node {NodeUrl}", nodeInfo.Url);
                        errors.Add("Collections request timed out");
                        if (nodeInfo.ErrorType == NodeErrorType.None)
                        {
                            nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                        }
                        // Mark as unhealthy immediately for collection fetch errors
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
                        // Mark as unhealthy immediately for collection fetch errors
                        nodeInfo.IsHealthy = false;
                    }
                    
                    // Store errors for later (after split detection), but don't mark as unhealthy yet
                    // unless it's a collection fetch error (which we already handled above)
                    if (errors.Count > 0)
                    {
                        nodeInfo.Error = string.Join("; ", errors);
                        nodeInfo.ShortError = GetShortErrorMessage(nodeInfo.ErrorType);
                    }
                }
                else
                {
                    nodeInfo.PeerId = $"{node.Host}:{node.Port}";
                    nodeInfo.IsHealthy = false;
                    
                    // Extract detailed error from Qdrant Status
                    var errorDetails = clusterInfo.Status?.Error ?? "Invalid response";
                    nodeInfo.Error = $"Failed to get cluster info: {errorDetails}";
                    nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.InvalidResponse);
                    nodeInfo.ErrorType = NodeErrorType.InvalidResponse;
                    logger.LogWarning("Node {NodeUrl} returned invalid cluster info response. Error: {Error}", 
                        nodeInfo.Url, errorDetails);
                }
            }
            catch (OperationCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                logger.LogWarning(ex, "Request timed out for node {NodeUrl}", nodeInfo.Url);
                nodeInfo.PeerId = $"{node.Host}:{node.Port}";
                nodeInfo.IsHealthy = false;
                nodeInfo.Error = "Request timed out";
                nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.Timeout);
                nodeInfo.ErrorType = NodeErrorType.Timeout;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get status for node {NodeUrl}", nodeInfo.Url);
                nodeInfo.PeerId = $"{node.Host}:{node.Port}";
                nodeInfo.IsHealthy = false;
                nodeInfo.Error = ex.Message;
                nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.ConnectionError);
                nodeInfo.ErrorType = NodeErrorType.ConnectionError;
            }

            return nodeInfo;
        });

        var nodeStatuses = await Task.WhenAll(tasks);
        
        // Detect cluster splits after all nodes have been queried
        DetectClusterSplits(nodeStatuses);
        
        // After split detection, mark nodes with consensus/message errors as unhealthy if they weren't already marked
        foreach (var node in nodeStatuses)
        {
            // If node has errors (consensus or message send failures) but is still marked healthy
            // (wasn't marked as split or collection error), mark it as unhealthy now
            if (node.IsHealthy && !string.IsNullOrEmpty(node.Error) && 
                (node.ErrorType == NodeErrorType.ConsensusThreadError || node.ErrorType == NodeErrorType.MessageSendFailures))
            {
                node.IsHealthy = false;
                logger.LogInformation("Marking node {NodeUrl} as unhealthy due to {ErrorType}", node.Url, node.ErrorType);
            }
        }
        
        var state = new ClusterState
        {
            Nodes = nodeStatuses.ToList(),
            LastUpdated = DateTime.UtcNow
        };
        
        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        
        return state;
    }



    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetCollectionsInfoAsync");

        // Get cluster state first
        var state = await GetClusterStateAsync(cancellationToken);
        
        // Create mapping of PeerId to podName
        var peerToPodMap = state.Nodes
            .Where(n => !string.IsNullOrEmpty(n.PeerId) && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId, n => n.PodName!);
            
        var nodes = state.Nodes;
        logger.LogInformation("Found {NodesCount} nodes to process. Healthy nodes: {HealthyCount}", 
            nodes.Count, nodes.Count(n => n.IsHealthy));
        
        // Step 1: ALWAYS get collections from Qdrant API first
        logger.LogInformation("Fetching collections from Qdrant API");
        var nodeInfos = nodes.Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName));
        var collectionsFromApi = await collectionService.GetCollectionsFromQdrantAsync(nodeInfos, cancellationToken);
        var result = collectionsFromApi.ToList();
        
        logger.LogInformation("Retrieved {Count} collections from Qdrant API", result.Count);
        
        // Step 2: If we have pods, get collections from storage and enrich the data
        bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
        
        if (hasPodsWithNames && result.Count > 0)
        {
            logger.LogInformation("Enriching collections with storage information from Kubernetes");
            
            // Create a lookup for collections from storage: (NodeUrl, CollectionName) -> CollectionSize
            var storageCollections = new Dictionary<(string NodeUrl, string CollectionName), CollectionSize>();
            
            foreach (var node in nodes)
            {
                try
                {
                    // Use PodName from config if available, otherwise try to resolve from IP
                    var podName = !string.IsNullOrEmpty(node.PodName) 
                        ? node.PodName 
                        : await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);
                        
                    if (string.IsNullOrEmpty(podName))
                    {
                        logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}", node.Url,
                            node.Namespace);
                        continue;
                    }

                    logger.LogInformation("Found pod {PodName} for IP {NodeUrl}, fetching storage info", podName, node.Url);
                    
                    var collectionSizes =
                        (await collectionService.GetCollectionsSizesForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId, cancellationToken))
                        .ToList();
                    
                    foreach (var size in collectionSizes)
                    {
                        storageCollections[(size.NodeUrl, size.CollectionName)] = size;
                    }
                    
                    logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}", collectionSizes.Count,
                        podName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get collection sizes for node {NodeUrl}", node.Url);
                }
            }
            
            logger.LogInformation("Found {Count} collections in storage across all nodes", storageCollections.Count);
            
            // Step 3: Enrich API collections with storage data and identify issues
            foreach (var collection in result)
            {
                var key = (collection.NodeUrl, collection.CollectionName);
                
                if (storageCollections.TryGetValue(key, out var storageInfo))
                {
                    // Collection exists in both API and storage - enrich with storage data
                    collection.Metrics["prettySize"] = storageInfo.PrettySize;
                    collection.Metrics["sizeBytes"] = storageInfo.SizeBytes;
                    
                    logger.LogDebug("Enriched collection {CollectionName} on {NodeUrl} with storage data: {Size}",
                        collection.CollectionName, collection.NodeUrl, storageInfo.PrettySize);
                }
                else
                {
                    // Collection exists in API but NOT in storage - this is an issue!
                    collection.Issues.Add("Collection exists in API but not found in storage");
                    
                    logger.LogWarning("⚠️ Collection {CollectionName} on node {NodeUrl} exists in API but not in storage!",
                        collection.CollectionName, collection.NodeUrl);
                }
                
                // Get snapshots for this collection (already done in GetCollectionsFromQdrantAsync)
                // but we can verify they match with what's in metrics if needed
            }
        }
        
        // Step 4: If no collections found from API, return test data
        if (result.Count == 0)
        {
            logger.LogDebug("No collections found from API, returning test data");
            return testDataProvider.GenerateTestCollectionData();
        }
        
        // Step 5: Enrich with clustering information from all healthy nodes
        var healthyNodes = nodes.Where(n => n.IsHealthy).ToList();
        if (healthyNodes.Count > 0)
        {
            logger.LogInformation("Enriching collections with clustering info from {HealthyNodeCount} healthy nodes", healthyNodes.Count);
            
            // Query each healthy node to get its local shards information
            foreach (var healthyNode in healthyNodes)
            {
                await collectionService.EnrichWithClusteringInfoAsync(
                    healthyNode.Url,
                    result,
                    peerToPodMap,
                    cancellationToken);
            }
        }
        else
        {
            logger.LogWarning("No healthy nodes found, skipping sharding information collection");
        }

        var collectionsWithIssues = result.Count(c => c.Issues.Count > 0);
        logger.LogInformation("Completed GetCollectionsInfoAsync, found {TotalCollections} collections in total ({IssuesCount} with issues)",
            result.Count, collectionsWithIssues);

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
        logger.LogInformation("Deleting collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}", 
            collectionName, podName, podNamespace);

        return await collectionService.DeleteCollectionFromDiskAsync(podName, podNamespace, collectionName, cancellationToken);
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

            return (NodeUrl: node.Url, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);

        foreach (var result in deleteResults)
        {
            results[result.NodeUrl] = result.Success;
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

    // Private methods

    private static IKubernetes? InitializeKubernetesClient(ILogger<ClusterManager> logger)
    {
        try
        {
            var client = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
            logger.LogInformation("Kubernetes client initialized successfully in ClusterManager");
            return client;
        }
        catch (k8s.Exceptions.KubeConfigException)
        {
            logger.LogInformation("Not running in Kubernetes cluster, pod name resolution will be disabled");
            return null;
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

        // Try to establish the majority peer state
        if (!_clusterState.TryUpdateMajorityState(healthyNodes))
        {
            logger.LogWarning("Could not establish majority cluster state from {HealthyNodeCount} healthy nodes", healthyNodes.Count);
            return;
        }

        logger.LogInformation("Established majority cluster state with peer IDs: {PeerIds}", 
            string.Join(", ", _clusterState.MajorityPeerIds));

        // Check each healthy node against the majority state
        foreach (var node in healthyNodes)
        {
            if (!_clusterState.IsNodeConsistentWithMajority(node, out var inconsistencyReason))
            {
                node.IsHealthy = false;
                node.Error = $"Potential cluster split detected: {inconsistencyReason}";
                node.ShortError = GetShortErrorMessage(NodeErrorType.ClusterSplit);
                node.ErrorType = NodeErrorType.ClusterSplit;
                
                logger.LogWarning(
                    "Node {NodeUrl} (PeerId={PeerId}) is inconsistent with majority cluster state. Reason: {Reason}",
                    node.Url,
                    node.PeerId,
                    inconsistencyReason);
            }
            else
            {
                logger.LogDebug("Node {NodeUrl} (PeerId={PeerId}) is consistent with majority cluster state",
                    node.Url,
                    node.PeerId);
            }
        }
    }

    private async Task<string?> GetPodNameFromIpAsync(string podUrl, string podNamespace, CancellationToken cancellationToken)
    {
        var uri = new Uri(podUrl);
        var podIp = uri.Host;
        
        logger.LogInformation("Getting pod name for IP {PodIp} in namespace {Namespace}", podIp, podNamespace);

        if (_kubernetes.Value == null)
        {
            logger.LogDebug("Kubernetes client not available, cannot resolve pod name");
            return null;
        }

        var pods = await _kubernetes.Value.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: podNamespace,
            fieldSelector: $"status.podIP=={podIp}",
            cancellationToken: cancellationToken);

        logger.LogInformation("Found {PodsCount} pods matching IP {PodIp}", pods.Items.Count, podIp);

        return pods.Items.FirstOrDefault()?.Metadata.Name;
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
            // Serialize to JSON and parse to extract key information
            var json = JsonSerializer.Serialize(failure);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract count and latest error
            var count = root.TryGetProperty("Count", out var countProp) ? countProp.GetInt32() : 0;
            var latestError = root.TryGetProperty("LatestError", out var errorProp) ? errorProp.GetString() : null;

            // Parse the latest error to extract just the important message
            if (!string.IsNullOrEmpty(latestError))
            {
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

    // Snapshot operations

    public async Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating snapshot for collection {CollectionName} on node {NodeUrl}", 
            collectionName, nodeUrl);

        return await collectionService.CreateCollectionSnapshotAsync(nodeUrl, collectionName, cancellationToken);
    }

    public async Task<Dictionary<string, string?>> CreateCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Creating snapshot for collection {CollectionName} on all nodes", collectionName);

        var state = await GetClusterStateAsync(cancellationToken);
        var results = new Dictionary<string, string?>();

        var createTasks = state.Nodes.Select(async node =>
        {
            var snapshotName = await collectionService.CreateCollectionSnapshotAsync(
                node.Url,
                collectionName,
                cancellationToken);

            return (NodeUrl: node.Url, SnapshotName: snapshotName);
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



    public async Task<bool> DeleteCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
            snapshotName, collectionName, nodeUrl);

        return await collectionService.DeleteCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, cancellationToken);
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on all nodes", 
            snapshotName, collectionName);

        var state = await GetClusterStateAsync(cancellationToken);
        var results = new Dictionary<string, bool>();

        var deleteTasks = state.Nodes.Select(async node =>
        {
            var success = await collectionService.DeleteCollectionSnapshotAsync(
                node.Url,
                collectionName,
                snapshotName,
                cancellationToken);

            return (NodeUrl: node.Url, Success: success);
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

    /// <summary>
    /// Downloads snapshot with automatic fallback: tries API first, then disk if API fails
    /// </summary>
    public async Task<Stream?> DownloadSnapshotWithFallbackAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        string? podName,
        string? podNamespace,
        CancellationToken cancellationToken)
    {
        // First, try to download via Qdrant API (preferred method)
        logger.LogInformation("Attempting to download snapshot {SnapshotName} of collection {CollectionName} via Qdrant API", 
            snapshotName, collectionName);

        var snapshotStream = await collectionService.DownloadCollectionSnapshotAsync(
            nodeUrl, 
            collectionName, 
            snapshotName, 
            cancellationToken);

        if (snapshotStream != null)
        {
            logger.LogInformation("✅ Successfully downloaded snapshot {SnapshotName} via Qdrant API", snapshotName);
            return snapshotStream;
        }

        // API failed, try downloading from disk (fallback for disk-only snapshots)
        logger.LogWarning("Failed to download snapshot {SnapshotName} via API, trying disk fallback", snapshotName);

        // Need podName and namespace for disk download
        if (string.IsNullOrEmpty(podName) || string.IsNullOrEmpty(podNamespace))
        {
            logger.LogError("Cannot fallback to disk download: PodName or PodNamespace not provided");
            return null;
        }

        var diskStream = await DownloadSnapshotFromDiskAsync(
            podName,
            podNamespace,
            collectionName,
            snapshotName,
            cancellationToken);

        if (diskStream != null)
        {
            logger.LogInformation("✅ Successfully downloaded snapshot {SnapshotName} from disk (fallback)", snapshotName);
            return diskStream;
        }

        logger.LogError("Failed to download snapshot {SnapshotName} via both API and disk", snapshotName);
        return null;
    }

    private async Task<Stream?> DownloadSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName}", 
            snapshotName, collectionName, podName);

        return await collectionService.DownloadSnapshotFromDiskAsync(podName, podNamespace, collectionName, snapshotName, cancellationToken);
    }



    public async Task<bool> RecoverCollectionFromSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Recovering collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}", 
            collectionName, snapshotName, nodeUrl);

        return await collectionService.RecoverCollectionFromSnapshotAsync(nodeUrl, collectionName, snapshotName, cancellationToken);
    }


    public async Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetSnapshotsInfoAsync");

        // Get cluster state first
        var state = await GetClusterStateAsync(cancellationToken);
        var nodes = state.Nodes;
        
        logger.LogInformation("Found {NodesCount} nodes to process. Healthy nodes: {HealthyCount}", 
            nodes.Count, nodes.Count(n => n.IsHealthy));
        
        var result = new List<SnapshotInfo>();
        bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
        
        // Priority 1: Try to get snapshots from Kubernetes storage (if we have pod names)
        if (hasPodsWithNames)
        {
            logger.LogInformation("Attempting to get snapshots from Kubernetes storage for {NodeCount} nodes", nodes.Count);
            
            foreach (var node in nodes)
            {
                logger.LogInformation(
                    "Processing node for snapshots: URL={NodeUrl}, PeerId={PeerId}, IsHealthy={IsHealthy}, Namespace={Namespace}, PodName={PodName}", 
                    node.Url, node.PeerId, node.IsHealthy, node.Namespace, node.PodName);
                try
                {
                    var podName = await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);
                    if (string.IsNullOrEmpty(podName))
                    {
                        logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}", node.Url,
                            node.Namespace);
                        continue;
                    }

                    logger.LogInformation("Found pod {PodName} for IP {NodeUrl}, retrieving snapshots...", podName, node.Url);
                    
                    var snapshots =
                        await collectionService.GetSnapshotsFromDiskForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId, cancellationToken);
                    var snapshotsList = snapshots.ToList();
                    logger.LogInformation("Retrieved {SnapshotsCount} snapshots from pod {PodName} (Node: {NodeUrl})", snapshotsList.Count,
                        podName, node.Url);

                    if (snapshotsList.Count > 0)
                    {
                        logger.LogDebug("Snapshots from pod {PodName}: {SnapshotNames}", 
                            podName, string.Join(", ", snapshotsList.Select(s => s.SnapshotName)));
                    }

                    result.AddRange(snapshotsList);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get snapshots for node {NodeUrl}", node.Url);
                }
            }
            
            logger.LogInformation("Finished processing all nodes. Total snapshots collected: {Count}", result.Count);
        }
        
        // Priority 2: If we didn't get any snapshots, return test data
        if (!hasPodsWithNames)
        {
            logger.LogDebug("No snapshots found from Kubernetes storage, returning test data");
            return testDataProvider.GenerateTestSnapshotData();
        }

        logger.LogInformation("Retrieved {SnapshotsCount} snapshots total", result.Count);
        return result;
    }

    public async Task<bool> DeleteSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} from disk on pod {PodName}", 
            snapshotName, collectionName, podName);

        return await collectionService.DeleteSnapshotFromDiskAsync(podName, podNamespace, collectionName, snapshotName, cancellationToken);
    }
}