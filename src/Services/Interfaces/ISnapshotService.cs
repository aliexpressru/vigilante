using Vigilante.Models;
using Vigilante.Models.Enums;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Service for managing collection snapshots across the Qdrant cluster
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Creates a snapshot for a collection on all nodes
    /// </summary>
    Task<Dictionary<string, string?>> CreateCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a snapshot from all nodes via Qdrant API
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionSnapshotOnAllNodesAsync(
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Universal method to delete a snapshot based on its source (KubernetesStorage or QdrantApi)
    /// </summary>
    Task<bool> DeleteSnapshotAsync(
        string collectionName,
        string snapshotName,
        SnapshotSource source,
        string? nodeUrl = null,
        string? podName = null,
        string? podNamespace = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a snapshot with fallback (first tries API, then disk)
    /// </summary>
    Task<Stream?> DownloadSnapshotWithFallbackAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        string? podName = null,
        string? podNamespace = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about all snapshots in the cluster (from both Kubernetes storage and Qdrant API)
    /// </summary>
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(
        CancellationToken cancellationToken = default);
}

