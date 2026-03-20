using Vigilante.Models;
using Vigilante.Models.Enums;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Service for managing collection snapshots across the Qdrant cluster
/// </summary>
public interface ISnapshotService
{
    /// <summary>
    /// Creates a snapshot of a collection on a specific node
    /// </summary>
    Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken,
        bool waitForResult = false);

    /// <summary>
    /// Creates a snapshot for a collection on specified nodes
    /// </summary>
    /// <param name="retainLastNAfterVisible">When set, <see cref="EnforceRetentionAsync"/> runs from <c>snapshot-create-*</c> job after new snapshots show up (use with <c>waitForResult: false</c>).</param>
    /// <remarks>When <see cref="CreateCollectionSnapshotBatchResult.SkippedDuplicatePending"/> is true, no Qdrant create was sent (pending job or concurrent create for the same collection).</remarks>
    Task<CreateCollectionSnapshotBatchResult> CreateCollectionSnapshotAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default,
        bool waitForResult = false,
        int? retainLastNAfterVisible = null,
        IReadOnlySet<string>? retentionClusterPeerIds = null);

    /// <summary>
    /// Gets snapshot information with sizes for a collection on a specific node
    /// </summary>
    Task<List<(string Name, long Size)>> GetCollectionSnapshotsWithSizeAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a snapshot for a collection on a specific node via API
    /// </summary>
    Task<bool> DeleteCollectionSnapshotApiAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a snapshot from specified nodes via Qdrant API
    /// </summary>
    Task<Dictionary<string, bool>> DeleteCollectionSnapshotApiAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a snapshot file from specified pods on disk
    /// </summary>
    Task<Dictionary<string, bool>> DeleteSnapshotFromDiskAsync(
        string collectionName,
        string snapshotName,
        IEnumerable<(string PodName, string PodNamespace)> pods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a snapshot file directly from disk on a specific pod
    /// </summary>
    Task<bool> DeleteSnapshotFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken);

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
    /// Downloads a snapshot for a collection from a specific node via Qdrant API
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
    /// Gets information about all snapshots in the cluster (from both Kubernetes storage and Qdrant API).
    /// Supports caching to improve performance.
    /// </summary>
    /// <param name="clearCache">Whether to clear cache before fetching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="nodesToUse">When provided, only these nodes are queried (e.g. current cluster nodes). When null, all nodes from provider are used.</param>
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(
        bool clearCache = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<NodeInfo>? nodesToUse = null);

    /// <summary>
    /// Deletes a snapshot from the appropriate storage backend based on source.
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
    /// Enforces retention policy: keeps last N snapshots per node for a collection, deletes older ones.
    /// When <paramref name="currentClusterPeerIds"/> is provided, snapshots belonging to peers not in the set (orphaned peers) are deleted entirely.
    /// </summary>
    Task EnforceRetentionAsync(
        string collectionName,
        int retainLastN,
        IReadOnlySet<string>? currentClusterPeerIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-memory snapshot cache.
    /// </summary>
    void InvalidateCache();
}
