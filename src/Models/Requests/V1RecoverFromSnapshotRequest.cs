namespace Vigilante.Models.Requests;

/// <summary>
/// Request for recovering a collection from a snapshot
/// </summary>
public class V1RecoverFromSnapshotRequest
{
    /// <summary>
    /// Name of the collection to recover (target collection name, can be different from source)
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the snapshot to recover from
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;
    
    /// <summary>
    /// Original source collection name (required for S3 snapshots to locate the file)
    /// If not provided, defaults to CollectionName
    /// </summary>
    public string? SourceCollectionName { get; set; }
    
    /// <summary>
    /// Target node URL where to recover the collection
    /// </summary>
    public string TargetNodeUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Source of the snapshot: "KubernetesStorage", "QdrantApi", or "S3Storage"
    /// </summary>
    public string Source { get; set; } = string.Empty;
}
