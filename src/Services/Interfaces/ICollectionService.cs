using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

public interface ICollectionService
{
    /// <summary>
    /// Gets collection sizes for a specific pod
    /// </summary>
    Task<IEnumerable<CollectionSize>> GetCollectionsSizesForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Internal method to replicate shards (called by ClusterManager)
    /// </summary>
    Task<bool> ReplicateShardsAsync(
        string healthyNodeUrl,
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        ShardTransferMethod? shardTransferMethod,
        CancellationToken cancellationToken);

    /// <summary>
    /// Internal method to abort shard transfer (called by ClusterManager)
    /// </summary>
    Task<bool> AbortShardTransferAsync(
        string healthyNodeUrl,
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint shardId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks if collections can be successfully retrieved from the node
    /// </summary>
    /// <param name="client">Qdrant HTTP client</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple with success status and error message if failed</returns>
    Task<(bool IsHealthy, string? ErrorMessage)> CheckCollectionsHealthAsync(IQdrantHttpClient client,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Deletes a collection via Qdrant API
    /// </summary>
    Task<bool> DeleteCollectionViaApiAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a collection directly from disk on a specific pod
    /// </summary>
    Task<bool> DeleteCollectionFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Checks if a collection exists on a specific node
    /// </summary>

    /// <summary>
    /// Recovers a collection from a snapshot on a specific node
    /// </summary>
    Task<bool> RecoverCollectionFromSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken,
        SnapshotPriority snapshotPriority = SnapshotPriority.Snapshot,
        bool waitForResult = true);

    /// <summary>
    /// Recovers a collection from a snapshot URL (e.g., S3 URL) on a specific node
    /// </summary>
    Task<bool> RecoverCollectionFromUrlAsync(
        string nodeUrl,
        string collectionName,
        string snapshotUrl,
        string? snapshotChecksum,
        bool waitForResult,
        SnapshotPriority snapshotPriority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets collections list from a Qdrant node directly (fallback when Kubernetes is not available)
    /// </summary>
    /// <param name="clearCache">If true, clears the cache and fetches fresh data</param>
    /// <returns>Tuple containing list of collections, health status, and error message if any</returns>
    Task<(List<CollectionInfo> Collections, bool IsHealthy, string? ErrorMessage)> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken,
        bool clearCache = false);
    
    /// <summary>
    /// Gets enriched collections information from healthy nodes with storage and clustering data
    /// </summary>
    /// <param name="clearCache">If true, clears the cache and fetches fresh data</param>
    Task<IReadOnlyList<CollectionInfo>> GetEnrichedCollectionsInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken,
        bool clearCache = false);

    /// <summary>
    /// Drops specified shards from a peer node in the cluster
    /// </summary>
    Task<bool> DropShardsFromPeerAsync(
        string healthyNodeUrl,
        string collectionName,
        ulong peerId,
        uint[] shardIds,
        bool isDryRun,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts resharding operation for a collection
    /// </summary>
    Task<bool> StartReshardingAsync(
        string healthyNodeUrl,
        string collectionName,
        ReshardingOperationDirection direction,
        ulong? peerId,
        CancellationToken cancellationToken);

}

