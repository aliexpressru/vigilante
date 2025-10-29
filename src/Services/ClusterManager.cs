using Aer.QdrantClient.Http.Models.Responses;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class ClusterManager(
    IMemoryCache cache,
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    IOptions<QdrantOptions> options,
    ILogger<ClusterManager> logger,
    IMeterService meterService)
{
    private readonly QdrantOptions _options = options.Value;
    private readonly ClusterPeerState _clusterState = new();

    private const string CacheKey = "qdrant_cluster_status";
    private static readonly TimeSpan CacheTimeout = TimeSpan.FromMinutes(1);

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting GetClusterStateAsync");
        
        if (cache.TryGetValue(CacheKey, out ClusterState? cachedStatus) && cachedStatus != null)
        {
            logger.LogInformation("Returning cached cluster state with {NodesCount} nodes, last updated at {LastUpdated}", 
                cachedStatus.Nodes.Count, cachedStatus.LastUpdated);
            return cachedStatus;
        }

        logger.LogInformation("Cache miss, requesting nodes from IQdrantNodesProvider");
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        logger.LogInformation("Received {NodesCount} nodes from provider", nodes.Count());

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

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(CacheTimeout);

        cache.Set(CacheKey, state, cacheOptions);
        
        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        
        return state;
    }

    private void DetectClusterSplits(NodeInfo[] nodes)
    {
        // Only analyze nodes that responded successfully
        var healthyNodes = nodes.Where(n => n.IsHealthy && n.CurrentPeerIds.Any()).ToList();
        
        if (!healthyNodes.Any())
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