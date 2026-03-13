using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Vigilante.Services;

/// <summary>
/// Entry point for starting/cancelling restore replication factor jobs. Uses cluster state and job registry; all job infra is at monitor level.
/// </summary>
public sealed class RestoreReplicationFactorJobService(
    IClusterManager clusterManager,
    IJobRegistry jobRegistry,
    IQdrantClientFactory clientFactory,
    IOptions<QdrantOptions> options,
    IServiceProvider serviceProvider,
    ILogger<RestoreReplicationFactorJobService> logger) : IRestoreReplicationFactorJobService
{
    private readonly QdrantOptions _options = options.Value;

    public async Task<RestoreReplicationFactorStartResult> RequestRestoreReplicationFactorAsync(
        string collectionName,
        ShardTransferMethod? shardTransferMethod,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        var state = await clusterManager.GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode == null)
        {
            return new RestoreReplicationFactorStartResult(
                ApiError: true,
                AlreadyInProgress: false,
                Message: "No healthy node available");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var client = clientFactory.CreateClientFromUrl(healthyNode.Url, _options.ApiKey);
        var transferMethod = shardTransferMethod ?? ShardTransferMethod.Snapshot;
        var (job, initialFailureMessage) = await RestoreReplicationFactorJob.CreateAsync(serviceProvider, client, collectionName, transferMethod, timeout, cts.Token);

        if (initialFailureMessage != null)
        {
            cts.Dispose();
            jobRegistry.RecordJobFailure(collectionName, initialFailureMessage);
            return new RestoreReplicationFactorStartResult(
                ApiError: true,
                AlreadyInProgress: false,
                Message: initialFailureMessage);
        }

        if (job == null)
        {
            cts.Dispose();
            return new RestoreReplicationFactorStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Collection does not require restore replication factor");
        }

        if (!jobRegistry.TryAddJob(job, cts))
        {
            await job.DisposeAsync();
            cts.Dispose();
            return new RestoreReplicationFactorStartResult(
                ApiError: false,
                AlreadyInProgress: true,
                Message: "Restore replication factor already in progress for this collection");
        }

        logger.LogInformation("Scheduled job for key {Key} (will run in background)", job.Key);
        return new RestoreReplicationFactorStartResult(
            ApiError: false,
            AlreadyInProgress: false,
            Message: $"Restore replication factor process started for collection {collectionName}");
    }

    public async Task<bool> CancelJobAsync(string key, CancellationToken cancellationToken = default)
    {
        var cancelled = await jobRegistry.CancelJobAsync(key, cancellationToken);
        if (cancelled)
            logger.LogInformation("Cancelled job for key {Key}", key);
        return cancelled;
    }
}
