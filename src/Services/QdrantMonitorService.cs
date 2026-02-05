using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantMonitorService(
    IClusterManager clusterManager,
    IMeterService meterService,
    IOptions<QdrantOptions> options,
    ILogger<QdrantMonitorService> logger)
    : BackgroundService
{
    private readonly QdrantOptions _options = options.Value;
    private ClusterStatus? _previousStatus;
    private bool _previousHadIssues;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Vigilante is now watching over Qdrant cluster");
        
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

                    if (_options.EnableAutoRecovery && !state.Health.IsHealthy)
                    {
                        logger.LogWarning("Auto-recovery is enabled but not yet implemented");
                        // TODO: Implement auto-recovery logic
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

                await Task.Delay(TimeSpan.FromSeconds(_options.MonitoringIntervalSeconds), stoppingToken);
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
        await base.StopAsync(cancellationToken);
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
            _previousHadIssues = true;
            return;
        }
        
        // If we previously had issues but now don't, and cluster is healthy, clear the attention flag
        if (_previousHadIssues && !hasIssues && currentStatus == ClusterStatus.Healthy)
        {
            meterService.UpdateClusterNeedsAttention(false);
            _previousHadIssues = false;
            _previousStatus = currentStatus;
            return;
        }
        
        _previousHadIssues = false;
        
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