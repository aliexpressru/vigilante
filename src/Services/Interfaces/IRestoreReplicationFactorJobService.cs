using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Handles starting restore replication factor jobs. Uses cluster state (via IClusterManager) and IJobRegistry.
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

}
