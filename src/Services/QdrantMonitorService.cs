using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantMonitorService(
    ClusterManager clusterManager,
    IOptions<QdrantOptions> options,
    ILogger<QdrantMonitorService> logger,
    ICollectionService collectionsSizeService)
    : BackgroundService
{
    private readonly QdrantOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 QdrantMonitorService is starting");
        logger.LogInformation("🛡️ Vigilante is now watching over Qdrant cluster");
        
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    logger.LogDebug("Starting cluster status check");
                    var state = await clusterManager.GetClusterStateAsync(stoppingToken);
                    logger.LogInformation("🔍 Cluster scan completed. Status: {Status} | Healthy nodes: {HealthyNodes}/{TotalNodes}",
                        state.Status,
                        state.Health.HealthyNodes,
                        state.Health.TotalNodes);

                    if (state.Health.Issues.Any())
                    {
                        logger.LogWarning("⚠️ Cluster issues detected: {Issues}", string.Join(", ", state.Health.Issues));
                    }

                    if (state.Health.IsHealthy)
                    {
                        logger.LogDebug("Collecting size information for collections");
                        var sizes = await collectionsSizeService.GetCollectionsInfoAsync(stoppingToken);
                        logger.LogInformation("📊 Collection sizes updated. Found {Count} collections across all nodes", 
                            sizes.Count);
                    }

                    if (_options.EnableAutoRecovery && !state.Health.IsHealthy)
                    {
                        logger.LogWarning("⚠️ Cluster unhealthy - initiating recovery procedures");
                        await clusterManager.RecoverClusterAsync();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown, no need to log error
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Error during cluster monitoring - Vigilante continues watching");
                }

                var monitoringInterval = _options.MonitoringIntervalSeconds;
                logger.LogDebug("Waiting {Interval} seconds before next check", monitoringInterval);
                await Task.Delay(TimeSpan.FromSeconds(monitoringInterval), stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Fatal error in QdrantMonitorService");
            throw; // Rethrow to let the hosting layer know about the failure
        }
        finally
        {
            logger.LogInformation("🛡️ Vigilante watch duty completed");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("🎯 QdrantMonitorService.StartAsync called");
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("🛑 QdrantMonitorService.StopAsync called");
        await base.StopAsync(cancellationToken);
    }
}