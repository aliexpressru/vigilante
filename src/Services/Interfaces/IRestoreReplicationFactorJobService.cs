using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Handles starting and cancelling restore replication factor jobs. Uses cluster state (via IClusterManager) and IJobRegistry.
/// All job orchestration lives at the monitor layer; this service is the entry point for API/ClusterManager to request a job.
/// </summary>
public interface IRestoreReplicationFactorJobService
{
    /// <summary>
    /// Requests to start restore replication factor for the collection. Resolves healthy node, creates job, adds to registry.
    /// </summary>
    Task<RestoreReplicationFactorStartResult> RequestRestoreReplicationFactorAsync(
        string collectionName,
        ShardTransferMethod? shardTransferMethod,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the job for the given key (e.g. collection name).
    /// </summary>
    Task<bool> CancelJobAsync(string key, CancellationToken cancellationToken = default);
}
