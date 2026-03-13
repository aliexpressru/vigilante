using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;

namespace Vigilante.Services.Jobs;

/// <summary>
/// One-shot job: runs snapshot automation (scheduled snapshots + orphaned cleanup) once per tick.
/// No ready-wait; AdvanceAsync runs full logic. Resolves ISnapshotService, IClusterManager, etc. from the service provider when needed.
/// </summary>
public sealed class SnapshotAutomationJob : IJob
{
    public const string JobKey = "snapshot-automation";
    public const string MetadataCurrentAction = "CurrentAction";

    public static class Actions
    {
        public const string LoadingCollections = "Loading collections...";
        public const string LoadingSnapshots = "Loading snapshots...";
        public const string DeletingOrphanedSnapshotsPrefix = "Deleting orphaned snapshots: ";
        public const string EnforcingRetentionPrefix = "Enforcing retention: ";
    }

    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<NodeInfo> _nodes;
    private readonly DynamicConfig _config;
    private readonly object _actionLock = new();
    private volatile string? _currentAction;

    public string Key => JobKey;
    public bool IsWaitingForReady => false;

    public SnapshotAutomationJob(
        IServiceProvider serviceProvider,
        IReadOnlyList<NodeInfo> nodes,
        DynamicConfig config)
    {
        _serviceProvider = serviceProvider;
        _nodes = nodes;
        _config = config;
    }

    public Task<bool?> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);

    public void OnReady() { }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        var clusterManager = _serviceProvider.GetRequiredService<IClusterManager>();
        var snapshotService = _serviceProvider.GetRequiredService<ISnapshotService>();
        var orphanedState = _serviceProvider.GetRequiredService<SnapshotOrphanedState>();
        var logger = _serviceProvider.GetRequiredService<ILogger<SnapshotAutomationJob>>();

        SetCurrentAction(Actions.LoadingCollections);
        var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, cancellationToken);

        var snapshotCfg = _config.Snapshot;

        var anyScheduleEnabled = snapshotCfg.Schedule.Enabled
            || snapshotCfg.CollectionOverrides?.Values.Any(s => s.Enabled) == true;

        if (!anyScheduleEnabled && !snapshotCfg.DeleteOrphanedAfterMinutes.HasValue)
        {
            SetCurrentAction(null);
            return (false, true, null);
        }

        SetCurrentAction(Actions.LoadingSnapshots);

        var byCollection = collections
            .GroupBy(c => c.CollectionName)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CollectionInfo>)g.ToList());

        var currentNames = byCollection.Keys.ToHashSet();
        var healthyNodeUrls = _nodes
            .Where(n => n.IsHealthy)
            .Select(n => n.Url)
            .ToList();

        var existingSnapshots = await snapshotService.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: cancellationToken, nodesToUse: _nodes);
        var snapshotsByCollection = existingSnapshots
            .GroupBy(s => s.CollectionName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (collectionName, infos) in byCollection)
        {
            var schedule = snapshotCfg.GetEffectiveSchedule(collectionName);
            if (!schedule.Enabled)
                continue;

            var isGreen = IsCollectionGreen(infos);

            if (schedule.IntervalMinutes is null)
            {
                if (isGreen)
                {
                    var existingSnaps = snapshotsByCollection.GetValueOrDefault(collectionName) ?? [];
                    var missingNodeUrls = healthyNodeUrls
                        .Where(url =>
                        {
                            var peerId = _nodes.FirstOrDefault(n => n.Url == url)?.PeerId;
                            if (string.IsNullOrEmpty(peerId))
                                return true;
                            return !existingSnaps.Any(s =>
                                s.SnapshotName.Contains(peerId, StringComparison.OrdinalIgnoreCase));
                        })
                        .ToList();

                    if (missingNodeUrls.Count > 0)
                    {
                        logger.LogInformation(
                            "Collection {CollectionName} is Green with HNSW; taking auto-snapshot on {MissingCount}/{TotalCount} nodes missing a snapshot",
                            collectionName, missingNodeUrls.Count, healthyNodeUrls.Count);
                        await TakeAutoSnapshotAsync(snapshotService, clusterManager, logger, collectionName, missingNodeUrls, schedule, cancellationToken);
                    }
                }
            }
            else if (isGreen)
            {
                var now = DateTime.UtcNow;
                var existingSnaps = snapshotsByCollection.GetValueOrDefault(collectionName) ?? [];
                var dueNodeUrls = healthyNodeUrls
                    .Where(url =>
                    {
                        var peerId = _nodes.FirstOrDefault(n => n.Url == url)?.PeerId;
                        if (string.IsNullOrEmpty(peerId))
                            return true;
                        var lastCreatedAt = existingSnaps
                            .Where(s => s.SnapshotName.Contains(peerId, StringComparison.OrdinalIgnoreCase) && s.CreatedAt.HasValue)
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
                    await TakeAutoSnapshotAsync(snapshotService, clusterManager, logger, collectionName, dueNodeUrls, schedule, cancellationToken);
                }
            }
        }

        if (snapshotCfg.DeleteOrphanedAfterMinutes.HasValue)
        {
            await ProcessOrphanedCollectionsAsync(snapshotService, orphanedState, logger, currentNames, snapshotsByCollection, snapshotCfg.DeleteOrphanedAfterMinutes.Value, cancellationToken);
        }

        SetCurrentAction(null);
        return (false, true, null);
    }

    private void SetCurrentAction(string? action)
    {
        lock (_actionLock)
        {
            _currentAction = action;
        }
    }

    private async Task ProcessOrphanedCollectionsAsync(
        ISnapshotService snapshotService,
        SnapshotOrphanedState orphanedState,
        ILogger<SnapshotAutomationJob> logger,
        HashSet<string> currentNames,
        Dictionary<string, List<SnapshotInfo>> snapshotsByCollection,
        int deleteAfterMinutes,
        CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var orphanedAt = orphanedState.OrphanedAt;
        var collectionsWithSnapshots = snapshotsByCollection.Keys.ToHashSet();

        foreach (var name in collectionsWithSnapshots)
        {
            if (currentNames.Contains(name))
            {
                if (orphanedAt.Remove(name))
                    logger.LogInformation("Collection {CollectionName} exists again, cancelling orphaned snapshot cleanup", name);
                continue;
            }

            if (!orphanedAt.ContainsKey(name))
            {
                orphanedAt[name] = now;
                logger.LogInformation(
                    "Collection {CollectionName} has snapshots but does not exist in cluster, scheduling cleanup in {Minutes} minutes",
                    name, deleteAfterMinutes);
            }
        }

        foreach (var (name, detectedAt) in orphanedAt.ToList())
        {
            if ((now - detectedAt).TotalMinutes < deleteAfterMinutes)
                continue;

            logger.LogInformation(
                "Deleting orphaned snapshots for collection {CollectionName} (missing for {Minutes} min)",
                name, (int)(now - detectedAt).TotalMinutes);

            SetCurrentAction(Actions.DeletingOrphanedSnapshotsPrefix + name);
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

            orphanedAt.Remove(name);
        }
    }

    private async Task TakeAutoSnapshotAsync(
        ISnapshotService snapshotService,
        IClusterManager clusterManager,
        ILogger<SnapshotAutomationJob> logger,
        string collectionName,
        List<string> nodeUrls,
        Schedule schedule,
        CancellationToken token)
    {
        if (nodeUrls.Count == 0)
        {
            logger.LogWarning("No healthy nodes available to create auto-snapshot for collection {CollectionName}", collectionName);
            return;
        }

        try
        {
            var results = await snapshotService.CreateCollectionSnapshotAsync(collectionName, nodeUrls, token, waitForResult: true);
            var succeededCount = results.Count(kv => kv.Value is not null);
            var failedCount = results.Count - succeededCount;

            logger.LogInformation(
                "Auto-snapshot for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes succeeded",
                collectionName, succeededCount, results.Count);

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

            if (schedule.RetainLastN.HasValue && succeededCount > 0)
            {
                SetCurrentAction(Actions.EnforcingRetentionPrefix + collectionName);
                var currentPeerIds = _nodes
                    .Where(n => !string.IsNullOrEmpty(n.PeerId))
                    .Select(n => n.PeerId!)
                    .ToHashSet();
                await snapshotService.EnforceRetentionAsync(collectionName, schedule.RetainLastN.Value, currentPeerIds, token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create auto-snapshot for collection {CollectionName}", collectionName);
            clusterManager.ReportIssue(IssueKeyConstants.Snapshot(collectionName), ex.Message);
        }
    }

    private static bool IsCollectionGreen(IReadOnlyList<CollectionInfo> infos)
    {
        return infos.Count > 0
            && infos.All(c => c.Status == QdrantCollectionStatus.Green)
            && infos.All(c => c.HnswM > 0);
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        lock (_actionLock)
        {
            if (string.IsNullOrEmpty(_currentAction))
                return null;
            return new Dictionary<string, object?> { [MetadataCurrentAction] = _currentAction };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
