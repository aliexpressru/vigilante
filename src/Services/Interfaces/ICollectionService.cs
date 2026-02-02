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
        CancellationToken cancellationToken);

    /// <summary>
    /// Recovers a collection from a snapshot URL (e.g., S3 URL) on a specific node
    /// </summary>
    Task<bool> RecoverCollectionFromUrlAsync(
        string nodeUrl,
        string collectionName,
        string snapshotUrl,
        string? snapshotChecksum,
        bool waitForResult,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets clustering information for a collection and enriches collection infos with shard data
    /// </summary>
    Task EnrichWithClusteringInfoAsync(
        string healthyNodeUrl,
        IList<CollectionInfo> collectionInfos,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets collections list from a Qdrant node directly (fallback when Kubernetes is not available)
    /// </summary>
    Task<List<CollectionInfo>> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Gets enriched collections information from healthy nodes with storage and clustering data
    /// </summary>
    Task<IReadOnlyList<CollectionInfo>> GetEnrichedCollectionsInfoAsync(
        IReadOnlyList<NodeInfo> nodes,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken);

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
}

