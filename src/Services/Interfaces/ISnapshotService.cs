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
    Task<Dictionary<string, string?>> CreateCollectionSnapshotAsync(
        string collectionName,
        IEnumerable<string> nodeUrls,
        CancellationToken cancellationToken = default,
        bool waitForResult = false);

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
    /// Gets information about all snapshots in the cluster (from both Kubernetes storage and Qdrant API)
    /// Supports caching to improve performance
    /// </summary>
    Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsInfoAsync(
        bool clearCache = false,
        CancellationToken cancellationToken = default);

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
    /// </summary>
    Task EnforceRetentionAsync(
        string collectionName,
        int retainLastN,
        CancellationToken cancellationToken = default);
}
