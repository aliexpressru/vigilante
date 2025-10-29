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


    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetClusterStateAsync");
        
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        logger.LogInformation("Received {NodesCount} nodes from provider", nodes.Count);

        var tasks = nodes.Select(async node =>
        {
            logger.LogInformation("Processing node: Host={Host}, Port={Port}, Namespace={Namespace}, PodName={PodName}", 
                node.Host, node.Port, node.Namespace, node.PodName);
            
            var client = clientFactory.CreateClient(node.Host, node.Port, _options.ApiKey);

            var nodeInfo = new NodeInfo
            {
                Url = $"http://{node.Host}:{node.Port}",
                Namespace = node.Namespace,
                PodName = node.PodName,
                LastSeen = DateTime.UtcNow
            };

            try
            {
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
                        var isHealthy = await collectionsTask.WaitAsync(timeoutCts.Token);
                        
                        if (!isHealthy)
                        {
                            nodeInfo.IsHealthy = false;
                            nodeInfo.Error = "Failed to fetch collections: Invalid response";
                            nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                            logger.LogWarning("Node {NodeUrl} returned invalid collections response", nodeInfo.Url);
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
                        nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to fetch collections for node {NodeUrl}", nodeInfo.Url);
                        nodeInfo.IsHealthy = false;
                        nodeInfo.Error = $"Failed to fetch collections: {ex.Message}";
                        nodeInfo.ErrorType = NodeErrorType.CollectionsFetchError;
                    }
                }
                else
                {
                    nodeInfo.PeerId = $"{node.Host}:{node.Port}";
                    nodeInfo.IsHealthy = false;
                    nodeInfo.Error = "Failed to get cluster info: Invalid response";
                    nodeInfo.ErrorType = NodeErrorType.InvalidResponse;
                    logger.LogWarning("Node {NodeUrl} returned invalid cluster info response", nodeInfo.Url);
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
                nodeInfo.ErrorType = NodeErrorType.Timeout;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get status for node {NodeUrl}", nodeInfo.Url);
                nodeInfo.PeerId = $"{node.Host}:{node.Port}";
                nodeInfo.IsHealthy = false;
                nodeInfo.Error = ex.Message;
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

    public async Task RecoverClusterAsync()
    {
        logger.LogInformation("🔧 Cluster recovery requested, but auto-recovery is not yet implemented");
        logger.LogInformation("💡 Manual intervention may be required for cluster issues");
        await Task.CompletedTask;
    }
}