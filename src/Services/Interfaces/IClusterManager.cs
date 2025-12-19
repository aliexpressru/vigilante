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
    /// Deletes a collection via Qdrant API on a specific node
    /// </summary>
    Task<bool> DeleteCollectionViaApiAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection via Qdrant API on all nodes
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionViaApiOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection from disk on a specific pod
    /// </summary>
    Task<bool> DeleteCollectionFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection from disk on all pods
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionFromDiskOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken = default);
}

