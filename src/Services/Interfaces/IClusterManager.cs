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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection via Qdrant API on specified nodes
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionViaApiAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection from disk on specified pods
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionFromDiskAsync(
        string collectionName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        CancellationToken cancellationToken = default);
}
