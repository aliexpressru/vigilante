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
    IOptions<QdrantOptions> options,
    ILogger<ClusterManager> logger,
    IMeterService meterService)
{
    private readonly QdrantOptions _options = options.Value;
    private readonly ClusterPeerState _clusterState = new();
    private readonly Lazy<IKubernetes?> _kubernetes = new(() => InitializeKubernetesClient(logger));

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetClusterStateAsync");
        
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        logger.LogInformation("Received {NodesCount} nodes from provider", nodes.Count);

        var tasks = nodes.Select(async node =>
        {
            logger.LogInformation("Processing node: Host={Host}, Port={Port}, Namespace={Namespace}, PodName={PodName}", 
                node.Host, node.Port, node.Namespace, node.PodName);
            
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
                
                logger.LogDebug("Requesting cluster info from node {NodeUrl}", nodeInfo.Url);
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
                    
                    logger.LogInformation("Node {NodeUrl} is healthy. PeerId={PeerId}, IsLeader={IsLeader}", 
                        nodeInfo.Url, nodeInfo.PeerId, nodeInfo.IsLeader);
                    
                    // Also check collections availability
                    try
                    {
                        logger.LogDebug("Requesting collections list from node {NodeUrl}", nodeInfo.Url);
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
                        else
                        {
                            logger.LogDebug("Node {NodeUrl} successfully returned collections list", nodeInfo.Url);
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
        
        // If running in test mode (no pods), return test data
        if (state.Nodes.All(n => string.IsNullOrEmpty(n.PodName)))
        {
            logger.LogDebug("Running in local mode (no pods found), returning test data");
            return collectionService.GenerateTestCollectionData();
        }

        var result = new List<CollectionInfo>();
        
        // Create mapping of PeerId to podName
        var peerToPodMap = state.Nodes
            .Where(n => !string.IsNullOrEmpty(n.PeerId) && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId!, n => n.PodName!);
            
        var nodes = state.Nodes;
        logger.LogInformation("Found {NodesCount} nodes to process. Healthy nodes: {HealthyCount}", 
            nodes.Count, nodes.Count(n => n.IsHealthy));
            
        // First, get all collections and their sizes from all nodes
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
                    await collectionService.GetCollectionsSizesForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId!, cancellationToken);
                var sizesList = collectionSizes.ToList();
                logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}", sizesList.Count,
                    podName);

                foreach (var size in sizesList)
                {
                    var metrics = new Dictionary<string, object>
                    {
                        { "prettySize", size.PrettySize },
                        { "sizeBytes", size.SizeBytes }
                    };

                    result.Add(new CollectionInfo
                    {
                        CollectionName = size.CollectionName,
                        NodeUrl = size.NodeUrl,
                        PodName = size.PodName,
                        PeerId = size.PeerId,
                        Metrics = metrics
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get collection sizes for node {nodeUrl}", node.Url);
            }
        }
        
        // Then, get sharding information from a healthy node
        var healthyNode = nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode != null)
        {
            try
            {
                var uri = new Uri(healthyNode.Url);
                var qdrantClient = clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

                // Group collections by name to avoid duplicates
                var collections = result
                    .Select(r => r.CollectionName)
                    .Distinct();

                foreach (var collectionName in collections)
                {
                    try
                    {
                        var clusteringInfo = await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);
                        if (clusteringInfo?.Status?.IsSuccess == true && clusteringInfo.Result != null)
                        {
                            // Update metrics for each node's collection
                            var collectionInfos = result.Where(r => r.CollectionName == collectionName);
                            foreach (var info in collectionInfos)
                            {
                                var shards = new List<ulong>();
                                var shardStates = new Dictionary<string, string>();

                                // If this is the node we made request from, use local shards
                                if (healthyNode.PeerId == info.PeerId)
                                {
                                    if (clusteringInfo.Result.LocalShards != null)
                                    {
                                        foreach (var shard in clusteringInfo.Result.LocalShards)
                                        {
                                            shards.Add(shard.ShardId);
                                            shardStates[shard.ShardId.ToString()] = shard.State.ToString();
                                        }
                                    }
                                }
                                else if (clusteringInfo.Result.RemoteShards != null)
                                {
                                    // Find shards for this node in remote shards
                                    foreach (var remoteShard in clusteringInfo.Result.RemoteShards)
                                    {
                                        if (remoteShard.PeerId.ToString() == info.PeerId)
                                        {
                                            shards.Add(remoteShard.ShardId);
                                            shardStates[remoteShard.ShardId.ToString()] = remoteShard.State.ToString();
                                        }
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
                                            ShardId = t.ShardId,
                                            To = peerToPodMap.TryGetValue(t.To.ToString(), out var podName) ? podName : t.To.ToString(),
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
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to get clustering info for collection {Collection} from node {NodeUrl}", 
                            collectionName, healthyNode.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to setup QdrantClient for node {NodeUrl}", healthyNode.Url);
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
}