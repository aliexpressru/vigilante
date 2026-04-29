using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;
using ISnapshotAutomationStatus = Vigilante.Services.Interfaces.ISnapshotAutomationStatus;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;

namespace Vigilante.Services.Jobs;

/// <summary>
/// One-shot job: runs snapshot automation (scheduled snapshots + orphaned cleanup) once per tick.
/// No ready-wait; AdvanceAsync runs full logic. Resolves ISnapshotService, IClusterManager, etc. from the service provider when needed.
/// </summary>
public sealed class SnapshotAutomationJob : IJob
{
    public const string JobKey = "snapshot-automation";
    private const int AbortMultipartUploadsOlderThanMinutes = 24 * 60;
    private static readonly TimeSpan MultipartCleanupTimeout = TimeSpan.FromSeconds(30);

    public static class Actions
    {
        public const string LoadingCollections = "Loading collections...";
        public const string LoadingSnapshots = "Loading snapshots...";
        public const string DeletingOrphanedSnapshotsPrefix = "Deleting orphaned snapshots: ";
        public const string CleaningMultipartUploads = "Cleaning multipart uploads...";

        public static string CreatingSnapshot(string collection, int nodeCount) =>
            $"Creating snapshot «{collection}» on {nodeCount} node(s)...";
    }

    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<NodeInfo> _nodes;
    private readonly DynamicConfig _config;
    private readonly DateTime _startedAtUtc;
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
        _startedAtUtc = DateTime.UtcNow;
    }

    public Task<bool?> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(true);

    public void OnReady() { }

    public async Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken)
    {
        var clusterManager = _serviceProvider.GetRequiredService<IClusterManager>();
        var jobRegistry = _serviceProvider.GetRequiredService<IJobRegistry>();
        var snapshotService = _serviceProvider.GetRequiredService<ISnapshotService>();
        var orphanedState = _serviceProvider.GetRequiredService<SnapshotOrphanedState>();
        var logger = _serviceProvider.GetRequiredService<ILogger<SnapshotAutomationJob>>();

        var snapshotCfg = _config.Snapshot;
        var anyScheduleEnabled = snapshotCfg.Schedule.Enabled
            || snapshotCfg.CollectionOverrides?.Values.Any(s => s.Enabled) == true;
        var multipartCleanupEnabled = AbortMultipartUploadsOlderThanMinutes > 0;

        if (!anyScheduleEnabled
            && !snapshotCfg.DeleteOrphanedAfterMinutes.HasValue
            && !multipartCleanupEnabled)
            return (false, true, null);

        var automationStatus = _serviceProvider.GetRequiredService<ISnapshotAutomationStatus>();
        automationStatus.BeginRun();
        try
        {
            SetCurrentAction(Actions.LoadingCollections);
            var collections = await clusterManager.GetCollectionsInfoAsync(clearCache: true, cancellationToken)
                .ConfigureAwait(false);

            await PruneStaleCollectionOverridesIfNeededAsync(automationStatus, logger, cancellationToken)
                .ConfigureAwait(false);

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

        var snapshotCreatePrefix = PendingSnapshotCreationJob.KeyPrefix;
        var startedSnapshotForAnyCollection = false;
        foreach (var (collectionName, infos) in byCollection)
        {
            var hasPendingSnapshotCreate = jobRegistry
                .GetPendingJobs()
                .Any(p =>
                    p.Job.Key.Length > snapshotCreatePrefix.Length
                    && p.Job.Key.StartsWith(snapshotCreatePrefix, StringComparison.Ordinal));
            if (hasPendingSnapshotCreate)
            {
                logger.LogInformation(
                    "Skipping auto-snapshot for {CollectionName}: another collection snapshot create is already pending",
                    collectionName);
                continue;
            }

            var schedule = snapshotCfg.GetEffectiveSchedule(collectionName);
            if (!schedule.Enabled)
            {
                logger.LogDebug(
                    "Skipping auto-snapshot for {CollectionName}: schedule is disabled",
                    collectionName);
                continue;
            }

            var isReadyForSnapshot = IsCollectionReadyForSnapshot(infos);
            if (!isReadyForSnapshot)
            {
                var statusSummary = string.Join(", ", infos
                    .Select(i => i.Status?.ToString() ?? "null")
                    .Distinct());
                var minHnsw = infos.Min(i => i.HnswM ?? 0UL);
                var hasActiveTransfers = infos.Any(c =>
                    c.Warnings.Any(w => w.Contains(CollectionWarningConstants.ActiveTransfersWarning, StringComparison.OrdinalIgnoreCase)));
                var hasRunningOptimizations = infos.Any(c =>
                    c.RunningOptimizations.Count > 0
                    || c.Warnings.Any(w => w.Contains(CollectionWarningConstants.RunningOptimizationsPrefix, StringComparison.OrdinalIgnoreCase)));

                logger.LogInformation(
                    "Skipping auto-snapshot for {CollectionName}: not ready (statuses={Statuses}, minHnswM={MinHnswM}, activeTransfers={HasActiveTransfers}, runningOptimizations={HasRunningOptimizations})",
                    collectionName,
                    statusSummary,
                    minHnsw,
                    hasActiveTransfers,
                    hasRunningOptimizations);
            }

            if (schedule.IntervalMinutes is null)
            {
                if (isReadyForSnapshot)
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
                        startedSnapshotForAnyCollection = await TakeAutoSnapshotAsync(snapshotService, clusterManager, logger, automationStatus, collectionName, missingNodeUrls, schedule, cancellationToken);
                    }
                }
            }
            else if (isReadyForSnapshot)
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
                        return IsIntervalSnapshotDue(
                            now,
                            lastCreatedAt,
                            schedule.IntervalMinutes.Value,
                            schedule.StartAt);
                    })
                    .ToList();

                if (dueNodeUrls.Count > 0)
                {
                    logger.LogInformation(
                        "Interval snapshot due for collection {CollectionName} on {DueCount}/{TotalCount} nodes (every {Minutes} min)",
                        collectionName, dueNodeUrls.Count, healthyNodeUrls.Count, schedule.IntervalMinutes.Value);
                    startedSnapshotForAnyCollection = await TakeAutoSnapshotAsync(snapshotService, clusterManager, logger, automationStatus, collectionName, dueNodeUrls, schedule, cancellationToken);
                }
            }

            if (startedSnapshotForAnyCollection)
                break;
        }

        if (snapshotCfg.DeleteOrphanedAfterMinutes.HasValue)
        {
            await ProcessOrphanedCollectionsAsync(snapshotService, orphanedState, logger, automationStatus, currentNames, snapshotsByCollection, snapshotCfg.DeleteOrphanedAfterMinutes.Value, cancellationToken);
        }

        await CleanupIncompleteMultipartUploadsAsync(
            logger,
            automationStatus,
            cancellationToken);

            SetCurrentAction(null);
            automationStatus.EndRun(true);
            return (false, true, null);
        }
        catch (Exception)
        {
            automationStatus.EndRun(false);
            throw;
        }
    }

    private void SetCurrentAction(string? action)
    {
        lock (_actionLock)
        {
            _currentAction = action;
        }

        _serviceProvider.GetService<ISnapshotAutomationStatus>()?.SetCurrentAction(action);
    }

    private async Task ProcessOrphanedCollectionsAsync(
        ISnapshotService snapshotService,
        SnapshotOrphanedState orphanedState,
        ILogger<SnapshotAutomationJob> logger,
        ISnapshotAutomationStatus automationStatus,
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

            var toDelete = snapshotsByCollection.GetValueOrDefault(name) ?? [];
            SetCurrentAction(Actions.DeletingOrphanedSnapshotsPrefix + name);
            try
            {
                foreach (var snapshot in toDelete)
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

                if (toDelete.Count > 0)
                    automationStatus.AppendRunNote($"Orphan cleanup «{name}»: deleted {toDelete.Count} snapshot(s)");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete orphaned snapshots for collection {CollectionName}", name);
            }

            orphanedAt.Remove(name);
        }
    }

    private async Task<bool> TakeAutoSnapshotAsync(
        ISnapshotService snapshotService,
        IClusterManager clusterManager,
        ILogger<SnapshotAutomationJob> logger,
        ISnapshotAutomationStatus automationStatus,
        string collectionName,
        List<string> nodeUrls,
        Schedule schedule,
        CancellationToken token)
    {
        if (nodeUrls.Count == 0)
        {
            logger.LogWarning("No healthy nodes available to create auto-snapshot for collection {CollectionName}", collectionName);
            return false;
        }

        try
        {
            var retentionPeerIds = _nodes
                .Where(n => !string.IsNullOrEmpty(n.PeerId))
                .Select(n => n.PeerId!)
                .ToHashSet();

            SetCurrentAction(Actions.CreatingSnapshot(collectionName, nodeUrls.Count));
            var batch = await snapshotService.CreateCollectionSnapshotAsync(
                    collectionName,
                    nodeUrls,
                    token,
                    waitForResult: false,
                    retainLastNAfterVisible: schedule.RetainLastN,
                    retentionClusterPeerIds: retentionPeerIds)
                .ConfigureAwait(false);
            if (batch.SkippedDuplicatePending)
            {
                logger.LogInformation(
                    "Auto-snapshot skipped for {CollectionName}: snapshot create already pending or in flight",
                    collectionName);
                automationStatus.AppendRunNote(
                    $"Snapshot «{collectionName}»: skipped — previous snapshot create still in progress");
                SetCurrentAction($"Snapshot «{collectionName}»: skipped (create already in progress)");
                return false;
            }

            var results = batch.Results;
            var succeededCount = results.Count(kv => kv.Value is not null);
            var failedCount = results.Count - succeededCount;

            logger.LogInformation(
                "Auto-snapshot for collection {CollectionName}: {SuccessCount}/{TotalCount} nodes accepted (async completion)",
                collectionName, succeededCount, results.Count);

            if (failedCount > 0)
            {
                var failedNodes = string.Join(", ", results.Where(kv => kv.Value is null).Select(kv => kv.Key));
                clusterManager.ReportIssue(
                    IssueKeyConstants.Snapshot(collectionName),
                    $"Snapshot failed on {failedCount}/{results.Count} nodes: {failedNodes}");
                automationStatus.AppendRunNote($"Snapshot «{collectionName}»: failed on {failedCount}/{results.Count} nodes");
            }
            else
            {
                clusterManager.ClearIssue(IssueKeyConstants.Snapshot(collectionName));
            }

            if (succeededCount > 0)
            {
                automationStatus.AppendRunNote(
                    $"Snapshot «{collectionName}»: started on {succeededCount}/{results.Count} node(s) (appears when ready)");
                SetCurrentAction($"Snapshot «{collectionName}» started ({succeededCount} node(s)); waiting for files…");
                return true;
            }
            else if (failedCount == results.Count && results.Count > 0)
                SetCurrentAction($"Snapshot «{collectionName}» failed on all nodes");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create auto-snapshot for collection {CollectionName}", collectionName);
            clusterManager.ReportIssue(IssueKeyConstants.Snapshot(collectionName), ex.Message);
            automationStatus.AppendRunNote($"Snapshot «{collectionName}»: error — {ex.Message}");
            return false;
        }
    }

    private async Task CleanupIncompleteMultipartUploadsAsync(
        ILogger<SnapshotAutomationJob> logger,
        ISnapshotAutomationStatus automationStatus,
        CancellationToken token)
    {
        var s3SnapshotService = _serviceProvider.GetService<IS3SnapshotService>();
        if (s3SnapshotService is null)
            return;

        SetCurrentAction(Actions.CleaningMultipartUploads);
        var ns = _nodes.FirstOrDefault()?.Namespace;
        logger.LogInformation(
            "Starting multipart cleanup (olderThanMinutes={OlderThanMinutes}, timeoutSeconds={TimeoutSeconds})",
            AbortMultipartUploadsOlderThanMinutes,
            MultipartCleanupTimeout.TotalSeconds);

        (int found, int aborted, int failed) cleanupResult;
        try
        {
            using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cleanupCts.CancelAfter(MultipartCleanupTimeout);
            cleanupResult = await s3SnapshotService
                .CleanupIncompleteMultipartUploadsAsync(
                    AbortMultipartUploadsOlderThanMinutes,
                    ns,
                    cleanupCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(
                "Multipart cleanup exceeded timeout of {TimeoutSeconds}s and was skipped for this run",
                MultipartCleanupTimeout.TotalSeconds);
            automationStatus.AppendRunNote(
                $"Multipart uploads: skipped (timeout after {(int)MultipartCleanupTimeout.TotalSeconds}s)");
            return;
        }

        var (found, aborted, failed) = cleanupResult;

        if (found > 0 || failed > 0)
        {
            var note = $"Multipart uploads: found={found}, aborted={aborted}, failed={failed}";
            automationStatus.AppendRunNote(note);
        }

        if (failed > 0)
        {
            logger.LogWarning(
                "Multipart cleanup completed with errors: found={Found}, aborted={Aborted}, failed={Failed}",
                found,
                aborted,
                failed);
        }
        else
        {
            logger.LogInformation(
                "Multipart cleanup completed: found={Found}, aborted={Aborted}, failed={Failed}",
                found,
                aborted,
                failed);
        }
    }

    /// <summary>
    /// Drops per-collection snapshot overrides when those collections no longer exist in Qdrant.
    /// Uses a raw multi-node listing; skips if any node fails so we do not wipe config on API errors.
    /// </summary>
    private async Task PruneStaleCollectionOverridesIfNeededAsync(
        ISnapshotAutomationStatus automationStatus,
        ILogger<SnapshotAutomationJob> logger,
        CancellationToken cancellationToken)
    {
        var overrides = _config.Snapshot.CollectionOverrides;
        if (overrides is not { Count: > 0 })
            return;

        var nodesTuple = _nodes
            .Where(n => n.IsHealthy)
            .Select(n => (n.Url, n.PeerId, n.Namespace, n.PodName))
            .ToList();
        if (nodesTuple.Count == 0)
            return;

        var collectionService = _serviceProvider.GetRequiredService<ICollectionService>();
        var (rawList, listingHealthy, _) = await collectionService
            .GetCollectionsFromQdrantAsync(nodesTuple, cancellationToken, clearCache: false)
            .ConfigureAwait(false);

        if (!listingHealthy)
        {
            logger.LogDebug("Skipping collection override prune: Qdrant listing not fully healthy");
            return;
        }

        var live = rawList.Select(c => c.CollectionName).ToHashSet(StringComparer.Ordinal);
        var stale = overrides.Keys.Where(k => !live.Contains(k)).ToList();
        if (stale.Count == 0)
            return;

        var dynamicConfigService = _serviceProvider.GetRequiredService<IDynamicConfigService>();
        var fullConfig = await dynamicConfigService.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var snap = fullConfig.Snapshot;
        if (snap.CollectionOverrides is null)
            return;

        foreach (var k in stale)
            snap.CollectionOverrides.Remove(k);

        if (snap.CollectionOverrides.Count == 0)
            snap.CollectionOverrides = null;

        await dynamicConfigService.UpdateConfigAsync(fullConfig, cancellationToken).ConfigureAwait(false);
        automationStatus.AppendRunNote($"Removed overrides (collections gone): {string.Join(", ", stale)}");
        logger.LogInformation(
            "Removed snapshot collection overrides for collections no longer in cluster: {Collections}",
            string.Join(", ", stale));
    }

    private static bool IsCollectionReadyForSnapshot(IReadOnlyList<CollectionInfo> infos)
    {
        var hasActiveTransfers = infos.Any(c =>
            c.Warnings.Any(w => w.Contains(CollectionWarningConstants.ActiveTransfersWarning, StringComparison.OrdinalIgnoreCase)));
        var hasRunningOptimizations = infos.Any(c =>
            c.RunningOptimizations.Count > 0
            || c.Warnings.Any(w => w.Contains(CollectionWarningConstants.RunningOptimizationsPrefix, StringComparison.OrdinalIgnoreCase)));

        return infos.Count > 0
            && infos.All(c => c.Status == QdrantCollectionStatus.Green)
            && infos.All(c => c.HnswM > 0)
            && !hasActiveTransfers
            && !hasRunningOptimizations;
    }

    internal static bool IsIntervalSnapshotDue(
        DateTime nowUtc,
        DateTime? lastCreatedAtUtc,
        int intervalMinutes,
        DateTimeOffset? startAt)
    {
        // Backward-compatible mode: no anchor time configured.
        if (startAt is null)
        {
            return lastCreatedAtUtc is null
                || (nowUtc - lastCreatedAtUtc.Value).TotalMinutes >= intervalMinutes;
        }

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var startTime = startAt.Value.UtcDateTime.TimeOfDay;
        var anchor = nowUtc.Date.Add(startTime);
        if (nowUtc < anchor)
            anchor = anchor.AddDays(-1);

        var elapsed = nowUtc - anchor;
        var periodsSinceAnchor = (long)Math.Floor(elapsed.Ticks / (double)interval.Ticks);
        var currentWindowStart = anchor.AddTicks(periodsSinceAnchor * interval.Ticks);

        // If there was no snapshot yet or the last one is from an older window, it's due.
        return lastCreatedAtUtc is null || lastCreatedAtUtc.Value < currentWindowStart;
    }

    public IReadOnlyDictionary<string, object?>? GetMetadata()
    {
        lock (_actionLock)
        {
            if (string.IsNullOrEmpty(_currentAction))
                return null;
            return new Dictionary<string, object?>
            {
                [JobMetadataKeys.CurrentAction] = _currentAction,
                [JobMetadataKeys.StartedAtUtc] = _startedAtUtc
            };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
