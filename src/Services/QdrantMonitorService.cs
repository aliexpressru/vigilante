using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Vigilante.Services;

public class QdrantMonitorService(
    IClusterManager clusterManager,
    IJobRegistry jobRegistry,
    IMeterService meterService,
    IDynamicConfigService dynamicConfigService,
    ISnapshotService snapshotService,
    SnapshotOrphanedState snapshotOrphanedState,
    ILogger<SnapshotAutomationJob> snapshotJobLogger,
    ILogger<QdrantMonitorService> logger)
    : BackgroundService
{
    internal DynamicConfig DynamicConfig = new();
    private ClusterStatus? _previousStatus;
    private CancellationTokenSource? _delayCts;
    private readonly object _configLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Vigilante is now watching over Qdrant cluster");

        DynamicConfig = await dynamicConfigService.GetConfigAsync(stoppingToken);

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
                        var snapshotJob = new SnapshotAutomationJob(snapshotService, clusterManager, snapshotOrphanedState, state.Nodes, DynamicConfig, snapshotJobLogger);
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        jobRegistry.TryAddJob(snapshotJob, cts);
                        await ProcessPendingJobsAsync(stoppingToken);
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
            DynamicConfig = newConfig;
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
            intervalSeconds = DynamicConfig.MonitoringIntervalSeconds;
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

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        var jobs = jobRegistry.GetPendingJobs();
        if (jobs.Count == 0)
            return;

        var tasks = jobs.Select(pending => ProcessOneJobAsync(pending, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessOneJobAsync(PendingJob pending, CancellationToken stoppingToken)
    {
        var job = pending.Job;
        var key = job.Key;
        var cts = pending.CancellationTokenSource;

        try
        {
            if (stoppingToken.IsCancellationRequested)
                return;

            if (cts.Token.IsCancellationRequested)
            {
                await jobRegistry.RemoveJobAsync(key);
                return;
            }

            if (job.IsWaitingForReady)
            {
                var ready = await job.CheckReadyAsync(cts.Token);
                if (ready == true)
                    job.OnReady();
            }
            else
            {
                var (hasMore, success, error) = await job.AdvanceAsync(cts.Token);
                if (!hasMore)
                {
                    await jobRegistry.RemoveJobAsync(key);
                    if (success)
                        logger.LogInformation("Job completed for key {Key}", key);
                }
                else if (!success)
                {
                    jobRegistry.RecordJobFailure(key, error ?? "Unknown");
                    await jobRegistry.RemoveJobAsync(key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Job cancelled for key {Key}", key);
            await jobRegistry.RemoveJobAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job failed for key {Key}", key);
            jobRegistry.RecordJobFailure(key, ex.Message);
            await jobRegistry.RemoveJobAsync(key);
        }
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
