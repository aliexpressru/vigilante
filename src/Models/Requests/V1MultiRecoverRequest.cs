namespace Vigilante.Models.Requests;

/// <summary>
/// Request to recover a collection on multiple target nodes from multiple snapshot sources sequentially.
/// </summary>
public sealed class V1MultiRecoverRequest
{
    /// <summary>
    /// Name of the collection to recover.
    /// </summary>
    public string TargetCollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Original collection name the snapshots were taken from.
    /// Used to locate snapshot files on disk when the target collection name differs.
    /// When null or empty, <see cref="TargetCollectionName"/> is used.
    /// </summary>
    public string? SourceCollectionName { get; set; }

    /// <summary>
    /// Recovery items — each defines one (snapshot source, target node) pair.
    /// Paired positionally: Items[i].SnapshotName is restored on Items[i].TargetNodeUrl.
    /// </summary>
    public List<V1MultiRecoverItem> Items { get; set; } = [];

    /// <summary>
    /// Defines which data should be used as a source of truth during recovery.
    /// </summary>
    public string SnapshotPriority { get; set; } = nameof(Aer.QdrantClient.Http.Models.Shared.SnapshotPriority.Snapshot);
}

/// <summary>
/// One (snapshot source, target node) pair for multi-snapshot recovery.
/// </summary>
public sealed class V1MultiRecoverItem
{
    /// <summary>
    /// URL of the Qdrant node to recover the collection on.
    /// </summary>
    public string TargetNodeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Name of the snapshot to restore from.
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;

    /// <summary>
    /// Storage source of the snapshot: QdrantApi, KubernetesStorage, or S3Storage.
    /// </summary>
    public string SnapshotSource { get; set; } = string.Empty;

    /// <summary>
    /// Optional SHA256 checksum to verify snapshot integrity before recovery.
    /// </summary>
    public string? SnapshotChecksum { get; set; }
}
