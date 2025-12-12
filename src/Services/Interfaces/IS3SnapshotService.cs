namespace Vigilante.Services.Interfaces;

/// <summary>
/// Interface for S3 snapshot service
/// </summary>
public interface IS3SnapshotService
{
    /// <summary>
    /// Lists ALL snapshots from S3 storage
    /// Returns list of tuples: (collectionName, snapshotName, sizeBytes)
    /// This includes snapshots for deleted/old collection versions
    /// </summary>
    Task<List<(string CollectionName, string SnapshotName, long SizeBytes)>> ListAllSnapshotsAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all snapshots for a given collection from S3
    /// Returns list of tuples: (snapshotName, sizeBytes)
    /// </summary>
    Task<List<(string Name, long SizeBytes)>> ListSnapshotsAsync(
        string collectionName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a snapshot from S3 to a stream
    /// </summary>
    Task<Stream?> DownloadSnapshotAsync(
        string collectionName,
        string snapshotName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a snapshot from S3 storage
    /// </summary>
    Task<bool> DeleteSnapshotAsync(
        string collectionName,
        string snapshotName,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if S3 storage is configured and available
    /// </summary>
    Task<bool> IsAvailableAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a presigned URL for downloading a snapshot from S3
    /// </summary>
    Task<string?> GetPresignedDownloadUrlAsync(
        string collectionName,
        string snapshotName,
        TimeSpan expiration,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);
}

