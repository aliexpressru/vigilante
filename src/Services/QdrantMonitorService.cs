using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Constants;
using Vigilante.Models;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantMonitorService(
    IClusterManager clusterManager,
    IMeterService meterService,
    IDynamicConfigService dynamicConfigService,
    ISnapshotService snapshotService,
    ILogger<QdrantMonitorService> logger)
    : BackgroundService
{
    internal DynamicConfig _dynamicConfig = new();
    private ClusterStatus? _previousStatus;
    private CancellationTokenSource? _delayCts;
    private readonly object _configLock = new();

    // Snapshot automation state — only accessed from the single monitoring loop, no locks needed
    private readonly Dictionary<string, DateTime> _orphanedAt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Vigilante is now watching over Qdrant cluster");

        _dynamicConfig = await dynamicConfigService.GetConfigAsync(stoppingToken);

        dynamicConfigService.ConfigChanged += OnConfigChanged;

        _ = Task.Run(async () =>
        {
            try
            {
                await dynamicConfigService.StartWatchingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error watching config changes from Kubernetes");
            }
        }, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var state = await clusterManager.GetClusterStateAsync(stoppingToken);

                    TrackClusterStatusChange(state);

                    if (!state.Health.IsHealthy || state.Health.Issues.Any())
                    {
                        logger.LogWarning("Cluster Status: {Status} | Healthy: {HealthyNodes}/{TotalNodes} | Issues: {Issues}",
                            state.Status,
                            state.Health.HealthyNodes,
                            state.Health.TotalNodes,
                            string.Join(", ", state.Health.Issues));
                    }

                    if (state.Health.IsHealthy)
                    {
                        var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, stoppingToken);
                        await ProcessSnapshotAutomationAsync(collections, state.Nodes, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during cluster monitoring");
                }

                await WaitForNextIterationAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error in QdrantMonitorService");
            throw;
        }
        finally
        {
            logger.LogInformation("Vigilante watch duty completed");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Vigilante starting");
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Vigilante stopping");
        dynamicConfigService.ConfigChanged -= OnConfigChanged;
        await base.StopAsync(cancellationToken);
    }

    private void OnConfigChanged(object? sender, DynamicConfig newConfig)
    {
        lock (_configLock)
        {
            _dynamicConfig = newConfig;
            logger.LogInformation(
                "Configuration reloaded: MonitoringIntervalSeconds={Interval}, interrupting current delay",
                newConfig.MonitoringIntervalSeconds);
            _delayCts?.Cancel();
        }
    }

    private async Task WaitForNextIterationAsync(CancellationToken stoppingToken)
    {
        int intervalSeconds;
        lock (_configLock)
        {
            intervalSeconds = _dynamicConfig.MonitoringIntervalSeconds;
            _delayCts?.Dispose();
            _delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _delayCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Delay interrupted due to configuration change");
        }
    }

    internal async Task ProcessSnapshotAutomationAsync(
        IReadOnlyList<CollectionInfo> allCollections,
        IReadOnlyList<NodeInfo> nodes,
        CancellationToken token)
    {
        var snapshotCfg = _dynamicConfig.Snapshot;

        var anyScheduleEnabled = snapshotCfg.Schedule.Enabled
            || snapshotCfg.CollectionOverrides?.Values.Any(s => s.Enabled) == true;

        if (!anyScheduleEnabled && !snapshotCfg.DeleteOrphanedAfterMinutes.HasValue)
        {
            return;
        }

        var byCollection = allCollections
            .GroupBy(c => c.CollectionName)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CollectionInfo>)g.ToList());

        var currentNames = byCollection.Keys.ToHashSet();
        var healthyNodeUrls = nodes
            .Where(n => n.IsHealthy)
            .Select(n => n.Url)
            .ToList();

        var existingSnapshots = await snapshotService.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: token, nodesToUse: nodes);
        var snapshotsByCollection = existingSnapshots
            .GroupBy(s => s.CollectionName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (collectionName, infos) in byCollection)
        {
            var schedule = snapshotCfg.GetEffectiveSchedule(collectionName);
            if (!schedule.Enabled)
            {
                continue;
            }

            var isGreen = IsCollectionGreen(infos);

            if (schedule.IntervalMinutes is null)
            {
                // OnGreenOnce: take a snapshot on each healthy node that doesn't have one yet
                if (isGreen)
                {
                    var existingSnaps = snapshotsByCollection.GetValueOrDefault(collectionName) ?? [];

                    // Match by peerId in snapshot name — works for all storage types (S3, K8s, Qdrant API)
                    var missingNodeUrls = healthyNodeUrls
                        .Where(url =>
                        {
                            var peerId = nodes.FirstOrDefault(n => n.Url == url)?.PeerId;
                            if (string.IsNullOrEmpty(peerId))
                            {
                                return true;
                            }

                            return !existingSnaps.Any(s =>
                                s.SnapshotName.Contains(peerId, StringComparison.OrdinalIgnoreCase));
                        })
                        .ToList();

                    if (missingNodeUrls.Count > 0)
                    {
                        logger.LogInformation(
                            "Collection {CollectionName} is Green with HNSW; taking auto-snapshot on {MissingCount}/{TotalCount} nodes missing a snapshot",
                            collectionName, missingNodeUrls.Count, healthyNodeUrls.Count);

                        await TakeAutoSnapshotAsync(collectionName, missingNodeUrls, schedule, token);
                    }
                }
            }
            else if (isGreen)
            {
                // Interval-based: snapshot every N minutes per node.
                // "Last snapshot time" is derived from the most recent snapshot's CreatedAt for the node —
                // no in-memory tracking needed; works correctly after restarts.
                var now = DateTime.UtcNow;
                var existingSnaps = snapshotsByCollection.GetValueOrDefault(collectionName) ?? [];

                var dueNodeUrls = healthyNodeUrls
                    .Where(url =>
                    {
                        var peerId = nodes.FirstOrDefault(n => n.Url == url)?.PeerId;

                        // If we can't identify the node, treat as due
                        if (string.IsNullOrEmpty(peerId))
                        {
                            return true;
                        }

                        var lastCreatedAt = existingSnaps
                            .Where(s => s.SnapshotName.Contains(peerId, StringComparison.OrdinalIgnoreCase)
                                        && s.CreatedAt.HasValue)
                            .Max(s => s.CreatedAt);

                        return lastCreatedAt is null
                               || (now - lastCreatedAt.Value).TotalMinutes >= schedule.IntervalMinutes.Value;
                    })
                    .ToList();

                if (dueNodeUrls.Count > 0)
                {
                    logger.LogInformation(
                        "Interval snapshot due for collection {CollectionName} on {DueCount}/{TotalCount} nodes (every {Minutes} min)",
                        collectionName, dueNodeUrls.Count, healthyNodeUrls.Count, schedule.IntervalMinutes.Value);

                    await TakeAutoSnapshotAsync(collectionName, dueNodeUrls, schedule, token);
                }
            }
        }

        // Orphaned snapshot cleanup
        if (snapshotCfg.DeleteOrphanedAfterMinutes.HasValue)
        {
            await ProcessOrphanedCollectionsAsync(currentNames, snapshotsByCollection, snapshotCfg.DeleteOrphanedAfterMinutes.Value, token);
        }
    }

    private async Task ProcessOrphanedCollectionsAsync(
        HashSet<string> currentNames,
        Dictionary<string, List<SnapshotInfo>> snapshotsByCollection,
        int deleteAfterMinutes,
        CancellationToken token)
    {
        var now = DateTime.UtcNow;

        // Collections that have snapshots but no longer exist in the cluster
        var collectionsWithSnapshots = snapshotsByCollection.Keys.ToHashSet();

        foreach (var name in collectionsWithSnapshots)
        {
            if (currentNames.Contains(name))
            {
                // Collection exists — cancel any pending orphaned cleanup
                if (_orphanedAt.Remove(name))
                {
                    logger.LogInformation(
                        "Collection {CollectionName} exists again, cancelling orphaned snapshot cleanup", name);
                }

                continue;
            }

            // Collection has snapshots but doesn't exist — track when first detected
            if (!_orphanedAt.ContainsKey(name))
            {
                _orphanedAt[name] = now;
                logger.LogInformation(
                    "Collection {CollectionName} has snapshots but does not exist in cluster, " +
                    "scheduling cleanup in {Minutes} minutes",
                    name, deleteAfterMinutes);
            }
        }

        foreach (var (name, detectedAt) in _orphanedAt.ToList())
        {
            if ((now - detectedAt).TotalMinutes < deleteAfterMinutes)
            {
                continue;
            }

            logger.LogInformation(
                "Deleting orphaned snapshots for collection {CollectionName} (missing for {Minutes} min)",
                name, (int)(now - detectedAt).TotalMinutes);

            try
            {
                foreach (var snapshot in snapshotsByCollection.GetValueOrDefault(name) ?? [])
                {
                    await snapshotService.DeleteSnapshotAsync(
                        name,
                        snapshot.SnapshotName,
                        snapshot.Source,
                        nodeUrl: snapshot.NodeUrl,
                        podName: snapshot.PodName,
                        podNamespace: snapshot.PodNamespace,
                        cancellationToken: token);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete orphaned snapshots for collection {CollectionName}", name);
            }

            _orphanedAt.Remove(name);
        }
    }

    private async Task<IReadOnlyList<string>> TakeAutoSnapshotAsync(
        string collectionName,
        List<string> nodeUrls,
        Schedule schedule,
        CancellationToken token)
    {
        if (nodeUrls.Count == 0)
        {
            logger.LogWarning(
                "No healthy nodes available to create auto-snapshot for collection {CollectionName}",
                collectionName);
            return [];
        }

        try
        {
            var results = await snapshotService.CreateCollectionSnapshotAsync(collectionName, nodeUrls, token, waitForResult: true);
            var succeededNodes = results.Where(kv => kv.Value is not null).Select(kv => kv.Key).ToList();
            var failedCount = results.Count - succeededNodes.Count;

            logger.LogInformation(
                "Auto-snapshot for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes succeeded",
                collectionName, succeededNodes.Count, results.Count);

            if (failedCount > 0)
            {
                var failedNodes = string.Join(", ", results.Where(kv => kv.Value is null).Select(kv => kv.Key));
                clusterManager.ReportIssue(
                    IssueKeyConstants.Snapshot(collectionName),
                    $"Snapshot failed on {failedCount}/{results.Count} nodes: {failedNodes}");
            }
            else
            {
                clusterManager.ClearIssue(IssueKeyConstants.Snapshot(collectionName));
            }

            if (schedule.RetainLastN.HasValue && succeededNodes.Count > 0)
            {
                await snapshotService.EnforceRetentionAsync(collectionName, schedule.RetainLastN.Value, token);
            }

            return succeededNodes;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create auto-snapshot for collection {CollectionName}", collectionName);
            clusterManager.ReportIssue(IssueKeyConstants.Snapshot(collectionName), ex.Message);
            return [];
        }
    }

    private static bool IsCollectionGreen(IReadOnlyList<CollectionInfo> infos)
    {
        return infos.Count > 0
            && infos.All(c => c.Status == QdrantCollectionStatus.Green)
            && infos.All(c => c.HnswM > 0);
    }

    internal void TrackClusterStatusChange(ClusterState state)
    {
        var currentStatus = state.Status;
        var hasIssues = state.Health.Issues.Any();

        if (hasIssues)
        {
            meterService.UpdateClusterNeedsAttention(true);
            _previousStatus = currentStatus;
            return;
        }

        if (_previousStatus.HasValue && _previousStatus.Value != currentStatus)
        {
            switch (_previousStatus.Value)
            {
                case ClusterStatus.Healthy when
                    (currentStatus == ClusterStatus.Degraded || currentStatus == ClusterStatus.Unavailable):
                    logger.LogWarning("Cluster status changed from {PreviousStatus} to {CurrentStatus} - NEEDS ATTENTION",
                        _previousStatus.Value, currentStatus);
                    meterService.UpdateClusterNeedsAttention(true);
                    break;

                case ClusterStatus.Degraded or ClusterStatus.Unavailable
                    when currentStatus == ClusterStatus.Healthy:
                    logger.LogInformation("Cluster status changed from {PreviousStatus} to {CurrentStatus} - recovered!",
                        _previousStatus.Value, currentStatus);
                    meterService.UpdateClusterNeedsAttention(false);
                    break;

                default:
                    logger.LogInformation("Cluster status changed from {PreviousStatus} to {CurrentStatus}",
                        _previousStatus.Value, currentStatus);
                    break;
            }
        }
        else if (!_previousStatus.HasValue)
        {
            if (currentStatus == ClusterStatus.Degraded || currentStatus == ClusterStatus.Unavailable)
            {
                logger.LogWarning("Initial cluster status is {Status} - NEEDS ATTENTION", currentStatus);
                meterService.UpdateClusterNeedsAttention(true);
            }
            else
            {
                logger.LogInformation("Initial cluster status is {Status}", currentStatus);
                meterService.UpdateClusterNeedsAttention(false);
            }
        }

        _previousStatus = currentStatus;
    }
}
