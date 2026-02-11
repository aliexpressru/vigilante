using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantMonitorService(
    IClusterManager clusterManager,
    IMeterService meterService,
    IDynamicConfigService dynamicConfigService,
    ILogger<QdrantMonitorService> logger)
    : BackgroundService
{
    private DynamicConfig _dynamicConfig = new();
    private ClusterStatus? _previousStatus;
    private CancellationTokenSource? _delayCts;
    private readonly object _configLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Vigilante is now watching over Qdrant cluster");
        
        // Load initial dynamic config
        _dynamicConfig = await dynamicConfigService.GetConfigAsync(stoppingToken);
        logger.LogInformation(
            "Loaded dynamic config: MonitoringIntervalSeconds={Interval}",
            _dynamicConfig.MonitoringIntervalSeconds);
        
        // Subscribe to config changes
        dynamicConfigService.ConfigChanged += OnConfigChanged;
        
        // Start watching for config changes from Kubernetes in background
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
                    
                    // Log only if there are issues or important status changes
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
                        // Clear cache on background refresh to ensure data is up-to-date
                        await clusterManager.GetCollectionsInfoAsync(clearCache: true, stoppingToken);
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

                // Wait with ability to interrupt on config change
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
        
        // Unsubscribe from config changes
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
            
            // Cancel current delay to immediately apply new interval
            _delayCts?.Cancel();
        }
    }

    private async Task WaitForNextIterationAsync(CancellationToken stoppingToken)
    {
        int intervalSeconds;
        lock (_configLock)
        {
            intervalSeconds = _dynamicConfig.MonitoringIntervalSeconds;
            // Create new CTS for this delay
            _delayCts?.Dispose();
            _delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _delayCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Delay was cancelled due to config change, not service stopping
            logger.LogDebug("Delay interrupted due to configuration change");
        }
    }

    internal void TrackClusterStatusChange(ClusterState state)
    {
        var currentStatus = state.Status;
        var hasIssues = state.Health.Issues.Any();
        
        // Always set attention if there are issues
        if (hasIssues)
        {
            meterService.UpdateClusterNeedsAttention(true);
            _previousStatus = currentStatus;
            return;
        }
        
        // Original logic for status changes
        if (_previousStatus.HasValue && _previousStatus.Value != currentStatus)
        {
            switch (_previousStatus.Value)
            {
                // Status changed
                case ClusterStatus.Healthy when 
                    (currentStatus == ClusterStatus.Degraded || currentStatus == ClusterStatus.Unavailable):
                    // Cluster degraded from Healthy - needs attention!
                    logger.LogWarning("Cluster status changed from {PreviousStatus} to {CurrentStatus} - NEEDS ATTENTION",
                        _previousStatus.Value, currentStatus);
                    
                    meterService.UpdateClusterNeedsAttention(true);

                    break;
                case ClusterStatus.Degraded or ClusterStatus.Unavailable
                    when currentStatus == ClusterStatus.Healthy:
                    // Cluster recovered to Healthy - clear attention flag
                    logger.LogInformation("Cluster status changed from {PreviousStatus} to {CurrentStatus} - recovered!",
                        _previousStatus.Value, currentStatus);
                    meterService.UpdateClusterNeedsAttention(false);

                    break;
                default:
                    // Other status transitions
                    logger.LogInformation("Cluster status changed from {PreviousStatus} to {CurrentStatus}",
                        _previousStatus.Value, currentStatus);

                    break;
            }
        }
        else if (!_previousStatus.HasValue)
        {
            // First time - set initial state
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