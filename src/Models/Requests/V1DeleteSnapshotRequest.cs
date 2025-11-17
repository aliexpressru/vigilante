namespace Vigilante.Models.Requests;

/// <summary>
/// Request for deleting a collection snapshot
/// </summary>
public class V1DeleteSnapshotRequest
{
    /// <summary>
    /// Name of the collection
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the snapshot to delete
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;

    /// <summary>
    /// If true, delete snapshot only on specific node. Otherwise on all nodes.
    /// </summary>
    public bool SingleNode { get; set; }

    /// <summary>
    /// Node URL (required if SingleNode is true for API deletion)
    /// </summary>
    public string? NodeUrl { get; set; }
    
    /// <summary>
    /// Source where snapshot was retrieved from: "KubernetesStorage" or "QdrantApi"
    /// Used to determine the deletion method
    /// </summary>
    public string? Source { get; set; }
    
    /// <summary>
    /// Pod name (required if Source is KubernetesStorage)
    /// </summary>
    public string? PodName { get; set; }
    
    /// <summary>
    /// Pod namespace (required if Source is KubernetesStorage)
    /// </summary>
    public string? PodNamespace { get; set; }
}

