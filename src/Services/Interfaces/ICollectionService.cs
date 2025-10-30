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
    /// Replicates shards between nodes (deprecated - use ClusterManager.ReplicateShardsAsync instead)
    /// </summary>
    Task<bool> ReplicateShardsAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        CancellationToken cancellationToken);
    
    /// <summary>
    /// Internal method to replicate shards (called by ClusterManager)
    /// </summary>
    Task<bool> ReplicateShardsInternalAsync(
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
    Task<(bool IsHealthy, string? ErrorMessage)> CheckCollectionsHealthAsync(IQdrantHttpClient client, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Generates test collection data for local development
    /// </summary>
    IReadOnlyList<CollectionInfo> GenerateTestCollectionData();
}

