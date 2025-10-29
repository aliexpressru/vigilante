using Aer.QdrantClient.Http.Abstractions;
using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

public interface ICollectionService
{
    /// <summary>
    /// Gets information about all collections across all nodes
    /// </summary>
    Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Replicates shards between nodes
    /// </summary>
    Task<bool> ReplicateShardsAsync(
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
    /// <returns>True if collections are accessible, false otherwise</returns>
    Task<bool> CheckCollectionsHealthAsync(IQdrantHttpClient client, CancellationToken cancellationToken = default);
}

