using k8s;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

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
                            nodeInfo.IsHealthy = false;
                            nodeInfo.Error = errorMessage ?? "Failed to fetch collections";
                            nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.CollectionsFetchError);
                            nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                            logger.LogWarning("Node {NodeUrl} collections check failed: {Error}", nodeInfo.Url, errorMessage);
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw;

                        logger.LogWarning(ex, "Collections request timed out for node {NodeUrl}", nodeInfo.Url);
                        nodeInfo.IsHealthy = false;
                        nodeInfo.Error = "Collections request timed out";
                        nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.CollectionsFetchError);
                        nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to fetch collections for node {NodeUrl}", nodeInfo.Url);
                        nodeInfo.IsHealthy = false;
                        nodeInfo.Error = $"Failed to fetch collections: {ex.Message}";
                        nodeInfo.ShortError = GetShortErrorMessage(NodeErrorType.CollectionsFetchError);
                        nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
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
        
        var state = new ClusterState
        {
            Nodes = nodeStatuses.ToList(),
            LastUpdated = DateTime.UtcNow
        };
        
        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        
        return state;
    }

    public async Task RecoverClusterAsync()
    {
        logger.LogInformation("🔧 Cluster recovery requested, but auto-recovery is not yet implemented");
        logger.LogInformation("💡 Manual intervention may be required for cluster issues");
        await Task.CompletedTask;
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
        
        var result = new List<CollectionInfo>();
        bool hasPodsWithNames = nodes.Any(n => !string.IsNullOrEmpty(n.PodName));
        
        // Priority 1: Try to get collections from Kubernetes storage (if we have pod names)
        if (hasPodsWithNames)
        {
            logger.LogInformation("Attempting to get collections from Kubernetes storage");
            
            foreach (var node in nodes)
            {
                logger.LogInformation(
                    "Processing node: URL={NodeUrl}, PeerId={PeerId}, IsHealthy={IsHealthy}, Namespace={Namespace}", 
                    node.Url, node.PeerId, node.IsHealthy, node.Namespace);
                try
                {
                    var podName = await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);
                    if (string.IsNullOrEmpty(podName))
                    {
                        logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}", node.Url,
                            node.Namespace);
                        continue;
                    }

                    logger.LogInformation("Found pod {PodName} for IP {NodeUrl}", podName, node.Url);
                    
                    var collectionSizes =
                        await collectionService.GetCollectionsSizesForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId, cancellationToken);
                    var sizesList = collectionSizes.ToList();
                    logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}", sizesList.Count,
                        podName);

                    foreach (var size in sizesList)
                    {
                        // Get snapshots for this collection on this node
                        List<string> snapshots = new List<string>();
                        try
                        {
                            snapshots = await collectionService.ListCollectionSnapshotsAsync(node.Url, size.CollectionName, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to get snapshots for collection {CollectionName} on node {NodeUrl}", 
                                size.CollectionName, node.Url);
                        }

                        var metrics = new Dictionary<string, object>
                        {
                            { "prettySize", size.PrettySize },
                            { "sizeBytes", size.SizeBytes },
                            { "snapshots", snapshots }
                        };

                        result.Add(new CollectionInfo
                        {
                            CollectionName = size.CollectionName,
                            NodeUrl = size.NodeUrl,
                            PodName = size.PodName,
                            PeerId = size.PeerId,
                            PodNamespace = node.Namespace ?? "",
                            Metrics = metrics
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get collection sizes for node {nodeUrl}", node.Url);
                }
            }
        }
        
        // Priority 2: If we didn't get any collections from storage, try Qdrant API
        if (result.Count == 0 && nodes.Count > 0)
        {
            logger.LogInformation("No collections found in Kubernetes storage, trying Qdrant API");
            
            var nodeInfos = nodes.Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName));
            var collectionsFromQdrant = await collectionService.GetCollectionsFromQdrantAsync(nodeInfos, cancellationToken);
            result.AddRange(collectionsFromQdrant);
        }
        
        // Priority 3: If still no collections, return test data
        if (result.Count == 0)
        {
            logger.LogDebug("No collections found from any source, returning test data");
            return testDataProvider.GenerateTestCollectionData();
        }
        
        // Enrich with clustering information from all healthy nodes
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

        logger.LogInformation("Completed GetCollectionsInfoAsync, found {TotalCollections} collections in total",
            result.Count);

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
        _ => "Unknown Error"
    };

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

    public async Task<Dictionary<string, List<string>>> ListCollectionSnapshotsOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Listing snapshots for collection {CollectionName} on all nodes", collectionName);

        var state = await GetClusterStateAsync(cancellationToken);
        var results = new Dictionary<string, List<string>>();

        var listTasks = state.Nodes.Select(async node =>
        {
            var snapshots = await collectionService.ListCollectionSnapshotsAsync(
                node.Url,
                collectionName,
                cancellationToken);

            return (NodeUrl: node.Url, Snapshots: snapshots);
        });

        var listResults = await Task.WhenAll(listTasks);

        foreach (var result in listResults)
        {
            results[result.NodeUrl] = result.Snapshots;
        }

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

    public async Task<Stream?> DownloadCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
            snapshotName, collectionName, nodeUrl);

        return await collectionService.DownloadCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, cancellationToken);
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

    public async Task<bool> RecoverCollectionFromUploadedSnapshotAsync(
        string nodeUrl,
        string collectionName,
        Stream snapshotData,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Recovering collection {CollectionName} from uploaded snapshot on node {NodeUrl}", 
            collectionName, nodeUrl);

        return await collectionService.RecoverCollectionFromUploadedSnapshotAsync(nodeUrl, collectionName, snapshotData, cancellationToken);
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
            logger.LogInformation("Attempting to get snapshots from Kubernetes storage");
            
            foreach (var node in nodes)
            {
                logger.LogInformation(
                    "Processing node: URL={NodeUrl}, PeerId={PeerId}, IsHealthy={IsHealthy}, Namespace={Namespace}", 
                    node.Url, node.PeerId, node.IsHealthy, node.Namespace);
                try
                {
                    var podName = await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);
                    if (string.IsNullOrEmpty(podName))
                    {
                        logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}", node.Url,
                            node.Namespace);
                        continue;
                    }

                    logger.LogInformation("Found pod {PodName} for IP {NodeUrl}", podName, node.Url);
                    
                    var snapshots =
                        await collectionService.GetSnapshotsFromDiskForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId, cancellationToken);
                    var snapshotsList = snapshots.ToList();
                    logger.LogInformation("Retrieved {SnapshotsCount} snapshots from pod {PodName}", snapshotsList.Count,
                        podName);

                    result.AddRange(snapshotsList);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to get snapshots for node {nodeUrl}", node.Url);
                }
            }
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