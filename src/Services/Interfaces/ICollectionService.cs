using Aer.QdrantClient.Http.Abstractions;
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
    /// Creates a snapshot of a collection on a specific node
    /// </summary>
    Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists all snapshots for a collection on a specific node
    /// </summary>
    Task<List<string>> ListCollectionSnapshotsAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a snapshot for a collection on a specific node
    /// </summary>
    Task<bool> DeleteCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a snapshot for a collection from a specific node
    /// </summary>
    Task<Stream?> DownloadCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a snapshot directly from disk on a specific pod (bypasses Qdrant API)
    /// </summary>
    Task<Stream?> DownloadSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
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
    Task<IReadOnlyList<CollectionInfo>> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets snapshot files and their sizes from disk for a specific pod
    /// </summary>
    Task<IEnumerable<SnapshotInfo>> GetSnapshotsFromDiskForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken);


    /// <summary>
    /// Deletes a snapshot file directly from disk on a specific pod
    /// </summary>
    Task<bool> DeleteSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken);
}