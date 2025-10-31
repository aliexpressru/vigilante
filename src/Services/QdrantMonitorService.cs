using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantMonitorService(
    ClusterManager clusterManager,
    IOptions<QdrantOptions> options,
    ILogger<QdrantMonitorService> logger)
    : BackgroundService
{
    private readonly QdrantOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 Vigilante is now watching over Qdrant cluster");
        
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var state = await clusterManager.GetClusterStateAsync(stoppingToken);
                    
                    // Log only if there are issues or important status changes
                    if (!state.Health.IsHealthy || state.Health.Issues.Any())
                    {
                        logger.LogWarning("⚠️ Cluster Status: {Status} | Healthy: {HealthyNodes}/{TotalNodes} | Issues: {Issues}",
                            state.Status,
                            state.Health.HealthyNodes,
                            state.Health.TotalNodes,
                            string.Join(", ", state.Health.Issues));
                    }

                    if (state.Health.IsHealthy)
                    {
                        await clusterManager.GetCollectionsInfoAsync(stoppingToken);
                    }

                    if (_options.EnableAutoRecovery && !state.Health.IsHealthy)
                    {
                        logger.LogWarning("🔧 Initiating cluster recovery procedures");
                        await clusterManager.RecoverClusterAsync();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Error during cluster monitoring");
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.MonitoringIntervalSeconds), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Fatal error in QdrantMonitorService");
            throw;
        }
        finally
        {
            logger.LogInformation("🛑 Vigilante watch duty completed");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("🎯 Vigilante starting");
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("🛑 Vigilante stopping");
        await base.StopAsync(cancellationToken);
    }
}