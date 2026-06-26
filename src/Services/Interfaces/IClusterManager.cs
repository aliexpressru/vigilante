using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Models;
using Vigilante.Models.Requests;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Interface for cluster management operations
/// </summary>
public interface IClusterManager
{
    /// <summary>
    /// Gets the current state of the cluster including all nodes and their health status
    /// </summary>
    Task<ClusterState> GetClusterStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about all collections across the cluster
    /// </summary>
    Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(bool clearCache = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replicates shards from source peer to target peer
    /// </summary>
    Task<bool> ReplicateShardsAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool moveShards,
        ShardTransferMethod? shardTransferMethod = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts an ongoing shard transfer
    /// </summary>
    Task<bool> AbortShardTransferAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint shardId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection via Qdrant API on specified nodes.
    /// deleteSnapshots=null → use Snapshot.DeleteWithCollection from dynamic config.
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionViaApiAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        bool? deleteSnapshots = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection from disk on specified pods.
    /// deleteSnapshots=null → use Snapshot.DeleteWithCollection from dynamic config.
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionFromDiskAsync(
        string collectionName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        bool? deleteSnapshots = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops specified shards from a peer node
    /// </summary>
    Task<bool> DropShardsFromPeerAsync(
        string collectionName,
        ulong peerId,
        uint[] shardIds,
        bool isDryRun,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts resharding operation for a collection
    /// </summary>
    Task<bool> StartReshardingAsync(
        string collectionName,
        ReshardingOperationDirection direction,
        ulong? peerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the specified peer from the cluster.
    /// </summary>
    /// <param name="peerId">The identifier of the peer to remove.</param>
    /// <param name="isForceDropOperation">If true, removes peer even if it has shards/replicas on it.</param>
    /// <param name="timeout">Optional operation timeout. If not set, default of 30 seconds is used.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<bool> RemovePeerAsync(
        ulong peerId,
        bool isForceDropOperation = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (adds) an alias for a collection. Uses one healthy node; in cluster mode alias change is replicated.
    /// </summary>
    Task<bool> SetCollectionAliasAsync(string collectionName, string aliasName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a collection alias. Uses one healthy node; in cluster mode change is replicated.
    /// </summary>
    Task<bool> RenameCollectionAliasAsync(string oldAliasName, string newAliasName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection alias. Uses one healthy node; in cluster mode change is replicated.
    /// </summary>
    Task<bool> DeleteCollectionAliasAsync(string aliasName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers optimizers for a collection. Uses one healthy node.
    /// </summary>
    Task<bool> TriggerCollectionOptimizersAsync(string collectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports an external issue to surface in cluster health, identified by key.
    /// The issue expires after a TTL if not explicitly cleared.
    /// </summary>
    void ReportIssue(string key, string message);

    /// <summary>
    /// Clears a previously reported issue by key
    /// </summary>
    void ClearIssue(string key);
}
