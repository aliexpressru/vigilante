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
    IServiceProvider serviceProvider,
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
                        var snapshotJob = new SnapshotAutomationJob(serviceProvider, state.Nodes, DynamicConfig);
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        jobRegistry.TryAddJob(snapshotJob, cts);
                        await jobRegistry.ProcessPendingJobsAsync(stoppingToken);
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

    internal void TrackClusterStatusChange(ClusterState state)
    {
        var currentStatus = state.Status;
        var hasIssues = state.Health.Issues.Any();
        var needsAttention = hasIssues || currentStatus is ClusterStatus.Degraded or ClusterStatus.Unavailable;

        if (!_previousStatus.HasValue)
        {
            if (needsAttention)
            {
                logger.LogWarning("Initial cluster status is {Status} - NEEDS ATTENTION", currentStatus);
            }
            else
            {
                logger.LogInformation("Initial cluster status is {Status}", currentStatus);
            }
        }
        else if (_previousStatus.Value != currentStatus)
        {
            if (needsAttention)
            {
                logger.LogWarning("Cluster status changed from {PreviousStatus} to {CurrentStatus} - NEEDS ATTENTION",
                    _previousStatus.Value, currentStatus);
            }
            else
            {
                logger.LogInformation("Cluster status changed from {PreviousStatus} to {CurrentStatus} - recovered!",
                    _previousStatus.Value, currentStatus);
            }
        }

        meterService.UpdateClusterNeedsAttention(needsAttention);
        _previousStatus = currentStatus;
    }
}
