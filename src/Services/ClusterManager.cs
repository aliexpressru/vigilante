using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aer.QdrantClient.Http.Models.Shared;
using System.Globalization;
using ClusterInfoResult = Aer.QdrantClient.Http.Models.Responses.GetClusterInfoResponse.ClusterInfo;
using MessageSendFailureUnit = Aer.QdrantClient.Http.Models.Responses.GetClusterInfoResponse.MessageSendFailureUnit;
using Vigilante.Services.Jobs.Snapshots;

namespace Vigilante.Services;

public partial class ClusterManager(
    IQdrantNodesProvider nodesProvider,
    IQdrantClientFactory clientFactory,
    ICollectionService collectionService,
    ISnapshotService snapshotService,
    IDynamicConfigService dynamicConfigService,
    TestDataProvider testDataProvider,
    IOptions<QdrantOptions> options,
    IJobRegistry jobRegistry,
    ILogger<ClusterManager> logger,
    IMeterService meterService,
    IKubernetesManager? kubernetesManager) : IClusterManager
{
    private static readonly Regex _localDockerQdrantHostRegex =
        LocalDockerQdrantHostRegex();

    private static readonly TimeSpan _externalIssueTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _jobErrorTtl = TimeSpan.FromMinutes(30);

    private readonly QdrantOptions _options = options.Value;
    private readonly ClusterPeerState _clusterState = new();
    private readonly ConcurrentDictionary<string, (DateTime Time, string Message)> _externalIssues = new();
    private readonly ConcurrentDictionary<ulong, byte> _excludedPeerIds = new();

    public async Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodesProvider.GetNodesAsync(cancellationToken);
        var tasks = nodes.Select(node => GetNodeInfoAsync(node, cancellationToken));
        var nodeStatuses = await Task.WhenAll(tasks);
        var collectStorageTask = CollectNodesStorageUsageAsync(nodeStatuses, cancellationToken);
        var collectMemoryTask = CollectNodesMemoryUsageAsync(nodeStatuses, cancellationToken);

        DetectClusterSplits(nodeStatuses);

        // Exclude peers we explicitly removed via RemovePeer (they no longer respond; don't show as unhealthy).
        // Also exclude nodes not in current cluster (e.g. after restart, or removed peer) so we don't show
        // non-responding nodes that are simply not in the cluster anymore.
        var visibleNodes = FilterNodesToCurrentCluster(nodeStatuses);

        // Mark nodes with MessageSendFailures as unhealthy if they weren't identified as part of a cluster split
        foreach (var node in visibleNodes)
        {
            if (node.IsHealthy &&
                node.ErrorType == NodeErrorType.MessageSendFailures)
            {
                node.IsHealthy = false;
                logger.LogInformation(ClusterConstants.MarkingNodeUnhealthyMessage,
                    node.Url);
            }
        }

        // Sort nodes: by PodName if available and not 'unknown', otherwise by PeerId
        var sortedNodes = visibleNodes
            .OrderBy(n => NodeSortingExtensions.GetNodeSortKey(n.PodName, n.PeerId))
            .ToList();

        var state = new ClusterState
        {
            Nodes = sortedNodes,
            LastUpdated = DateTime.UtcNow,
            StatefulSetName = await nodesProvider.GetStatefulSetNameAsync(cancellationToken)
        };

        var storageByNodeUrl = await collectStorageTask;
        var memoryByNodeUrl = await collectMemoryTask;
        ApplyStorageUsage(nodeStatuses, storageByNodeUrl);
        ApplyMemoryUsage(nodeStatuses, memoryByNodeUrl);

        var dynamicConfig = await dynamicConfigService.GetConfigAsync(cancellationToken);
        AddStorageUsageIssuesIfNeeded(state.Nodes, dynamicConfig.DiskUsageAlertThresholdPercent);
        AddMemoryUsageIssuesIfNeeded(state.Nodes, dynamicConfig.RamUsageAlertThresholdPercent);

        meterService.UpdateAliveNodes(state.Nodes.Count(n => n.IsHealthy));
        await AddKubernetesWarningsIfNeededAsync(state, cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var (key, (time, message)) in _externalIssues)
        {
            if (now - time <= _externalIssueTtl)
            {
                state.Health.Issues.Add($"[{key}] {message}");
            }
        }

        // Job failures from JobRegistry (TTL); surface via ReportIssue for cluster state
        var jobErrors = jobRegistry.GetActiveErrorsAndPruneExpired(now, _jobErrorTtl);
        foreach (var (key, message) in jobErrors)
        {
            var prefix = key.StartsWith(PendingSnapshotCreationJob.KeyPrefix, StringComparison.Ordinal)
                ? "Snapshot creation failed: "
                : ClusterConstants.RestoreReplicationFactorFailedPrefix;

            state.Health.Issues.Add($"[{IssueKeyConstants.JobFailure(key)}] {prefix}{message}");
        }

        return state;
    }

    private async Task<Dictionary<string, NodeStorageInfo>> CollectNodesStorageUsageAsync(
        IReadOnlyList<NodeInfo> nodes,
        CancellationToken cancellationToken)
    {
        var storageByNodeUrl = new Dictionary<string, NodeStorageInfo>(StringComparer.Ordinal);

        if (kubernetesManager == null)
        {
            return storageByNodeUrl;
        }

        var tasks = nodes
            .Select(async node =>
            {
                try
                {
                    var usage = await kubernetesManager.GetQdrantStorageUsageAsync(
                        podName: node.PodName,
                        namespaceParameter: node.Namespace,
                        storagePath: QdrantConstants.StorageRootPath,
                        nodeUrl: node.Url,
                        cancellationToken: cancellationToken);

                    if (usage == null)
                    {
                        return (NodeUrl: node.Url, Storage: null);
                    }

                    return (
                        NodeUrl: node.Url,
                        Storage: new NodeStorageInfo
                        {
                            UsedBytes = usage.UsedBytes,
                            CapacityBytes = usage.PvcCapacityBytes,
                            UsagePercent = usage.UsagePercent
                        });
                }
                catch (Exception)
                {
                    return (NodeUrl: node.Url, Storage: (NodeStorageInfo?)null);
                }
            });

        var collected = await Task.WhenAll(tasks);
        foreach (var (NodeUrl, Storage) in collected)
        {
            if (Storage != null)
            {
                storageByNodeUrl[NodeUrl] = Storage;
            }
        }

        return storageByNodeUrl;
    }

    private static void ApplyStorageUsage(
        IEnumerable<NodeInfo> nodes,
        Dictionary<string, NodeStorageInfo> storageByNodeUrl)
    {
        foreach (var node in nodes)
        {
            if (!storageByNodeUrl.TryGetValue(node.Url, out var storage))
            {
                continue;
            }

            node.Storage = storage;
        }
    }

    private async Task<Dictionary<string, NodeMemoryInfo>> CollectNodesMemoryUsageAsync(
        IReadOnlyList<NodeInfo> nodes,
        CancellationToken cancellationToken)
    {
        var memoryByNodeUrl = new Dictionary<string, NodeMemoryInfo>(StringComparer.Ordinal);

        if (kubernetesManager == null)
        {
            return memoryByNodeUrl;
        }

        var tasks = nodes
            .Select(async node =>
            {
                try
                {
                    var usage = await kubernetesManager.GetQdrantMemoryUsageAsync(
                        podName: node.PodName,
                        namespaceParameter: node.Namespace,
                        nodeUrl: node.Url,
                        cancellationToken: cancellationToken);

                    if (usage == null)
                    {
                        return (NodeUrl: node.Url, Memory: (NodeMemoryInfo?)null);
                    }

                    return (
                        NodeUrl: node.Url,
                        Memory: new NodeMemoryInfo
                        {
                            UsedBytes = usage.UsedBytes,
                            RequestBytes = usage.RequestBytes,
                            LimitBytes = usage.LimitBytes,
                            UsagePercent = usage.UsagePercent
                        });
                }
                catch (Exception)
                {
                    return (NodeUrl: node.Url, Memory: (NodeMemoryInfo?)null);
                }
            });

        var collected = await Task.WhenAll(tasks);
        foreach (var item in collected)
        {
            if (item.Memory != null)
            {
                memoryByNodeUrl[item.NodeUrl] = item.Memory;
            }
        }

        return memoryByNodeUrl;
    }

    private static void ApplyMemoryUsage(
        IEnumerable<NodeInfo> nodes,
        Dictionary<string, NodeMemoryInfo> memoryByNodeUrl)
    {
        foreach (var node in nodes)
        {
            if (!memoryByNodeUrl.TryGetValue(node.Url, out var memory))
            {
                continue;
            }

            node.Memory = memory;
        }
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(bool clearCache = false,
        CancellationToken cancellationToken = default)
    {
        var state = await GetClusterStateAsync(cancellationToken);

        var peerToPodMap = state.Nodes
            .Where(n => n.PeerId != 0 && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId, n => n.PodName!);

        // Get enriched collections info from CollectionService
        // CollectionService handles caching internally
        var result = await collectionService.GetEnrichedCollectionsInfoAsync(
            state.Nodes,
            peerToPodMap,
            cancellationToken,
            clearCache);

        // Fallback to test data if no collections found
        if (result.Count == 0)
        {
            logger.LogWarning("No collections found from API. This might be because: " +
                "1) No healthy nodes available, " +
                "2) Nodes have no collections, " +
                "3) API connection failed. " +
                "Returning test data (only available in Development)");

            return testDataProvider.GenerateTestCollectionData();
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

    public async Task<bool> AbortShardTransferAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint shardId,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Aborting shard transfer. Source: {SourcePeerId}, Target: {TargetPeerId}, Collection: {Collection}, ShardId: {ShardId}",
            sourcePeerId, targetPeerId, collectionName, shardId);

        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);

        if (healthyNode == null)
        {
            logger.LogError("No healthy nodes found to abort shard transfer");

            return false;
        }

        return await collectionService.AbortShardTransferAsync(
            healthyNode.Url,
            sourcePeerId,
            targetPeerId,
            collectionName,
            shardId,
            cancellationToken);
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionViaApiAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        bool? deleteSnapshots = null,
        CancellationToken cancellationToken = default)
    {
        var nodeUrlsList = nodeUrls.ToList();
        logger.LogInformation(
            "Deleting collection {CollectionName} via API on {NodeCount} specified nodes",
            collectionName,
            nodeUrlsList.Count);

        var deleteTasks = nodeUrlsList.Select(async nodeUrl =>
        {
            var success = await collectionService.DeleteCollectionViaApiAsync(
                nodeUrl,
                collectionName,
                cancellationToken);

            return (NodeUrl: nodeUrl, Success: success);
        });

        var deleteResults = await Task.WhenAll(deleteTasks);
        var results = deleteResults.ToDictionary(r => r.NodeUrl, r => r.Success);

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Collection {CollectionName} deleted via API: {SuccessCount}/{TotalCount} nodes",
            collectionName, successCount, results.Count);

        if (successCount > 0)
        {
            await DeleteSnapshotsIfRequestedAsync(collectionName, deleteSnapshots, cancellationToken);
        }

        return results;
    }

    public async Task<Dictionary<string, bool>> DeleteCollectionFromDiskAsync(
        string collectionName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        bool? deleteSnapshots = null,
        CancellationToken cancellationToken = default)
    {
        var podsList = pods.ToList();
        logger.LogInformation(
            "Deleting collection {CollectionName} from disk on {PodCount} specified pods",
            collectionName,
            podsList.Count);

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
        var results = deleteResults.ToDictionary(r => r.PodName, r => r.Success);

        var successCount = results.Values.Count(s => s);
        logger.LogInformation(
            "Collection {CollectionName} deleted from disk: {SuccessCount}/{TotalCount} pods",
            collectionName, successCount, results.Count);

        if (successCount > 0)
        {
            await DeleteSnapshotsIfRequestedAsync(collectionName, deleteSnapshots, cancellationToken);
        }

        return results;
    }

    public async Task<bool> SetCollectionAliasAsync(string collectionName, string aliasName, CancellationToken cancellationToken = default)
    {
        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode == null)
        {
            logger.LogWarning("No healthy node available to set alias {AliasName} for collection {CollectionName}", aliasName, collectionName);
            return false;
        }

        return await collectionService.SetCollectionAliasAsync(healthyNode.Url, collectionName, aliasName, cancellationToken);
    }

    public async Task<bool> RenameCollectionAliasAsync(string oldAliasName, string newAliasName, CancellationToken cancellationToken = default)
    {
        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode == null)
        {
            logger.LogWarning("No healthy node available to rename alias {OldAliasName} to {NewAliasName}", oldAliasName, newAliasName);
            return false;
        }

        return await collectionService.RenameCollectionAliasAsync(healthyNode.Url, oldAliasName, newAliasName, cancellationToken);
    }

    public async Task<bool> DeleteCollectionAliasAsync(string aliasName, CancellationToken cancellationToken = default)
    {
        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode == null)
        {
            logger.LogWarning("No healthy node available to delete alias {AliasName}", aliasName);
            return false;
        }

        return await collectionService.DeleteCollectionAliasAsync(healthyNode.Url, aliasName, cancellationToken);
    }

    public async Task<bool> DropShardsFromPeerAsync(
        string collectionName,
        ulong peerId,
        uint[] shardIds,
        bool isDryRun,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Starting drop shards operation. Collection: {Collection}, PeerId: {PeerId}, " +
            "Shards: {ShardIds}, DryRun: {IsDryRun}",
            collectionName, peerId, string.Join(", ", shardIds), isDryRun);

        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);

        if (healthyNode == null)
        {
            logger.LogError("No healthy nodes found to perform shard drop operation");
            return false;
        }

        return await collectionService.DropShardsFromPeerAsync(
            healthyNode.Url,
            collectionName,
            peerId,
            shardIds,
            isDryRun,
            cancellationToken);
    }

    public async Task<bool> StartReshardingAsync(
        string collectionName,
        ReshardingOperationDirection direction,
        ulong? peerId,
        CancellationToken cancellationToken)
    {
        var peerInfo = peerId.HasValue ? $", PeerId: {peerId.Value}" : ", All peers";
        logger.LogInformation(
            "Starting resharding operation. Collection: {Collection}, Direction: {Direction}{PeerInfo}",
            collectionName, direction, peerInfo);

        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);

        if (healthyNode == null)
        {
            logger.LogError("No healthy nodes found to perform resharding operation");
            return false;
        }

        return await collectionService.StartReshardingAsync(
            healthyNode.Url,
            collectionName,
            direction,
            peerId,
            cancellationToken);
    }

    public async Task<bool> RemovePeerAsync(
        ulong peerId,
        bool isForceDropOperation = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Removing peer {PeerId} from cluster. ForceDrop: {IsForceDrop}, Timeout: {Timeout}",
            peerId, isForceDropOperation, timeout?.ToString() ?? "default");

        var state = await GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);

        if (healthyNode == null)
        {
            logger.LogError("No healthy nodes found to perform remove peer operation");
            return false;
        }

        try
        {
            var client = clientFactory.CreateClientFromUrl(healthyNode.Url, _options.ApiKey);
            var response = await client.RemovePeer(
                peerId,
                cancellationToken,
                isForceDropOperation,
                timeout,
                clusterName: null);

            if (response?.Status?.IsSuccess == true)
            {
                _excludedPeerIds.TryAdd(peerId, 0);
                logger.LogInformation("Peer {PeerId} removed from cluster successfully; excluded from cluster state", peerId);
                return true;
            }

            logger.LogError("Failed to remove peer {PeerId}: {Error}",
                peerId, response?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove peer {PeerId} from cluster", peerId);
            return false;
        }
    }

    private async Task<NodeInfo> GetNodeInfoAsync(QdrantNodeConfig node, CancellationToken cancellationToken)
    {
        // Get basic node info (URL, peer ID, pod name, etc.) from the nodes provider
        var nodeInfo = await nodesProvider.GetBasicNodeInfoAsync(node, cancellationToken);
        nodeInfo.BrowserUrl = BuildBrowserUrl(nodeInfo.Url);

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

    private static string BuildBrowserUrl(string rawNodeUrl)
    {
        if (!Uri.TryCreate(rawNodeUrl, UriKind.Absolute, out var uri))
        {
            return rawNodeUrl;
        }

        var match = _localDockerQdrantHostRegex.Match(uri.Host);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var nodeNumber))
        {
            return rawNodeUrl;
        }

        // Docker compose convention: qdrant-N:6333 is published as localhost:(6333 + N*10)
        if (uri.Port != 6333)
        {
            return rawNodeUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "localhost",
            Port = 6333 + (nodeNumber * 10)
        };

        return builder.Uri.ToString();
    }

    private async Task ProcessClusterInfoResultAsync(
        NodeInfo nodeInfo,
        ClusterInfoResult clusterInfoResult,
        IQdrantHttpClient client,
        CancellationToken linkedToken,
        CancellationToken originalCancellationToken)
    {
        // PeerId is already set by GetBasicNodeInfoAsync, but verify it matches
        var expectedPeerId = clusterInfoResult.PeerId;

        if (nodeInfo.PeerId == 0)
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
        await CheckCollectionsHealthAsync(nodeInfo, linkedToken, originalCancellationToken);
        await FetchQdrantIssuesAsync(nodeInfo, client, linkedToken);

        // Set IsHealthy based on error type:
        // - ConsensusThreadError: immediately unhealthy (critical)
        // - MessageSendFailures: will be evaluated by DetectClusterSplits (might be split, not just unhealthy)
        // - Other errors or no errors: healthy
        if (nodeInfo.ErrorType == NodeErrorType.ConsensusThreadError)
        {
            nodeInfo.IsHealthy = false;
        }
        else if (nodeInfo.ErrorType is NodeErrorType.None or
                 NodeErrorType.MessageSendFailures)
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
        {
            return;
        }

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

    private static void CollectPeerInformation(NodeInfo nodeInfo, ClusterInfoResult clusterInfoResult)
    {
        if (clusterInfoResult.Peers != null)
        {
            nodeInfo.CurrentPeerIds =
            [
                ..clusterInfoResult.ParsedPeers.Keys,
                clusterInfoResult.PeerId
            ];
        }
    }

    private async Task CheckCollectionsHealthAsync(
        NodeInfo nodeInfo,
        CancellationToken linkedToken,
        CancellationToken originalCancellationToken)
    {
        try
        {
            // Use GetCollectionsFromQdrantAsync directly - it reuses cached data and returns health status
            var (_, isHealthy, errorMessage) = await collectionService.GetCollectionsFromQdrantAsync(
                [(nodeInfo.Url, nodeInfo.PeerId, nodeInfo.Namespace, nodeInfo.PodName)],
                linkedToken,
                clearCache: false); // Use cache for health checks

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
            {
                throw;
            }

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

            if (issuesResponse != null
                && issuesResponse.Status.IsSuccess
                && issuesResponse.Result?.Issues != null)
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
                                issueMessage.Append(' ');
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
                        }
                    }
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
        {
            throw exception;
        }

        // Set node state
        nodeInfo.PeerId = 0;
        nodeInfo.IsHealthy = false;
        nodeInfo.Issues.Add(errorMessage);
        nodeInfo.ShortError = GetShortErrorMessage(errorType);
        nodeInfo.ErrorType = errorType;

        // Log the error
        if (exception != null)
        {
            logger.LogWarning(exception, "Failed to get status for node {NodeUrl}, Host : {Host}, Port: {Port}", nodeInfo.Url, node.Host, node.Port);
        }
        else
        {
            logger.LogWarning("Node {NodeUrl}, Host : {Host}, Port: {Port} error: {ErrorMessage}", node.Host, node.Port, nodeInfo.Url, errorMessage);
        }
    }

    private async Task AddKubernetesWarningsIfNeededAsync(ClusterState state, CancellationToken cancellationToken)
    {
        if (kubernetesManager == null)
        {
            return;
        }

        if (state.Status != ClusterStatus.Degraded)
        {
            return;
        }

        var namespaceToUse = state.Nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.Namespace))?.Namespace;

        try
        {
            var warningEvents = await kubernetesManager.GetWarningEventsAsync(namespaceToUse, cancellationToken);

            if (warningEvents is { Count: > 0 })
            {
                var targetNode = state.Nodes.FirstOrDefault(n => !n.IsHealthy) ?? state.Nodes.FirstOrDefault();

                if (targetNode != null)
                {
                    foreach (var warning in warningEvents)
                    {
                        targetNode.Warnings.Add(ClusterConstants.KubernetesEventPrefix + warning);
                    }

                    // Force recalculation of Health to include new warnings
                    state.InvalidateCache();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Kubernetes warning events");
        }
    }

    private static void AddStorageUsageIssuesIfNeeded(
        IEnumerable<NodeInfo> nodes,
        decimal thresholdPercent)
    {
        foreach (var node in nodes)
        {
            var usagePercent = node.Storage.UsagePercent;
            if (!usagePercent.HasValue || usagePercent.Value < thresholdPercent)
            {
                continue;
            }

            var usageText = usagePercent.Value.ToString("F2", CultureInfo.InvariantCulture);
            var thresholdText = thresholdPercent.ToString("F2", CultureInfo.InvariantCulture);
            node.Issues.Add(
                $"Disk usage is {usageText}% (threshold: {thresholdText}%)");
        }
    }

    private static void AddMemoryUsageIssuesIfNeeded(
        IEnumerable<NodeInfo> nodes,
        decimal thresholdPercent)
    {
        foreach (var node in nodes)
        {
            var usagePercent = node.Memory.UsagePercent;
            if (!usagePercent.HasValue || usagePercent.Value < thresholdPercent)
            {
                continue;
            }

            var usageText = usagePercent.Value.ToString("F2", CultureInfo.InvariantCulture);
            var thresholdText = thresholdPercent.ToString("F2", CultureInfo.InvariantCulture);
            node.Issues.Add(
                $"RAM usage is {usageText}% (threshold: {thresholdText}%)");
        }
    }

    /// <summary>
    /// Filters out nodes that should not be shown: peers explicitly removed via RemovePeer,
    /// and nodes not in the current cluster (e.g. non-responding after restart or removal).
    /// </summary>
    private NodeInfo[] FilterNodesToCurrentCluster(NodeInfo[] nodeStatuses)
    {
        var majorityPeerIds = _clusterState.MajorityPeerIds;

        var filtered = nodeStatuses.Where(n =>
        {
            if (_excludedPeerIds.ContainsKey(n.PeerId))
            {
                return false;
            }

            // Only filter by majority when PeerId is 0 (node never responded). Nodes with numeric PeerId
            // (responded at least once) stay visible so we can show them as unhealthy (e.g. cluster split minority).
            if (majorityPeerIds.Count > 0
                && n.PeerId == 0
                && !majorityPeerIds.Contains(n.PeerId))
            {
                return false;
            }

            return true;
        }).ToArray();

        if (filtered.Length < nodeStatuses.Length)
        {
            logger.LogInformation(
                "Filtered cluster state: {Visible} of {Total} nodes (excluded removed/out-of-cluster peers)",
                filtered.Length, nodeStatuses.Length);
        }

        return filtered;
    }

    private void DetectClusterSplits(NodeInfo[] nodes)
    {
        var healthyNodes = nodes.Where(n => n.IsHealthy && n.CurrentPeerIds.Count > 0).ToList();

        if (healthyNodes.Count == 0)
        {
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

    private async Task DeleteSnapshotsIfRequestedAsync(
        string collectionName,
        bool? deleteSnapshots,
        CancellationToken cancellationToken)
    {
        var config = await dynamicConfigService.GetConfigAsync(cancellationToken);
        if (!(deleteSnapshots ?? config.Snapshot.DeleteWithCollection))
        {
            return;
        }

        try
        {
            var state = await GetClusterStateAsync(cancellationToken);
            var allSnapshots = await snapshotService.GetSnapshotsInfoAsync(cancellationToken, clearCache: true, state.Nodes);
            var collectionSnapshots = allSnapshots.Where(s => s.CollectionName == collectionName).ToList();

            if (collectionSnapshots.Count == 0)
            {
                logger.LogInformation("No snapshots found for collection {CollectionName}", collectionName);
                return;
            }

            var deletedCount = 0;
            foreach (var snapshot in collectionSnapshots)
            {
                var success = await snapshotService.DeleteSnapshotAsync(
                    collectionName,
                    snapshot.SnapshotName,
                    snapshot.Source,
                    nodeUrl: snapshot.NodeUrl,
                    podName: snapshot.PodName,
                    podNamespace: snapshot.PodNamespace,
                    cancellationToken: cancellationToken);

                if (success)
                {
                    deletedCount++;
                }
            }

            logger.LogInformation(
                "Deleted {DeletedCount}/{TotalCount} snapshots for collection {CollectionName} after collection deletion",
                deletedCount, collectionSnapshots.Count, collectionName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete snapshots for collection {CollectionName} after collection deletion",
                collectionName);
        }
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
        {
            return ClusterConstants.UnknownErrorMessage;
        }

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
                    var messageEnd = latestError.IndexOf('\"', messageStart);
                    if (messageEnd > messageStart)
                    {
                        var message = latestError[messageStart..messageEnd];
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
                    var statusEnd = latestError.IndexOf(',', statusStart);
                    if (statusEnd > statusStart)
                    {
                        var status = latestError[statusStart..statusEnd];

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

    public void ReportIssue(string key, string message)
    {
        _externalIssues[key] = (DateTime.UtcNow, message);
    }

    public void ClearIssue(string key)
    {
        _externalIssues.TryRemove(key, out _);
    }

    [GeneratedRegex("^qdrant-(\\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalDockerQdrantHostRegex();
}

