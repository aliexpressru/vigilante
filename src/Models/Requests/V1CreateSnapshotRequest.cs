namespace Vigilante.Models.Requests;

/// <summary>
/// Request for creating a collection snapshot on specified nodes
/// </summary>
public class V1CreateSnapshotRequest
{
    /// <summary>
    /// Name of the collection to snapshot
    /// </summary>
    public required string CollectionName { get; set; }

    /// <summary>
    /// List of node URLs where snapshots should be created
    /// Each entry should be a node URL like "http://qdrant-0:6333"
    /// If empty or null, snapshot will be created on all nodes
    /// </summary>
    public List<string>? NodeUrls { get; set; }
}
