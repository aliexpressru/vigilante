namespace Vigilante.Models.Requests;

/// <summary>
/// Request for recovering a collection from a snapshot
/// </summary>
public class V1RecoverFromSnapshotRequest
{
    /// <summary>
    /// Name of the collection to recover
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the snapshot to recover from
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;
    
    /// <summary>
    /// Target node URL where to recover the collection
    /// </summary>
    public string TargetNodeUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Source of the snapshot: "KubernetesStorage", "QdrantApi", or "S3"
    /// </summary>
    public string Source { get; set; } = string.Empty;
}
