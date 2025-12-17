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
    /// Supports caching to improve performance
    /// </summary>
    /// <param name="clearCache">Whether to clear the cache and fetch fresh data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all snapshots with their information</returns>
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(
        bool clearCache = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets paginated and filtered snapshots information
    /// </summary>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="filter">Filter by collection name (case-insensitive partial match)</param>
    /// <param name="forceRefresh">Whether to force refresh the cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of paginated snapshots and total count</returns>
    Task<(IReadOnlyList<SnapshotInfo> Snapshots, int TotalCount)> GetSnapshotsInfoPaginatedAsync(
        int page,
        int pageSize,
        string? filter,
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the snapshots cache
    /// </summary>
    void ClearCache();
}

