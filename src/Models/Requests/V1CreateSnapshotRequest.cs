namespace Vigilante.Models.Requests;

/// <summary>
/// Request for creating a collection snapshot
/// </summary>
public class V1CreateSnapshotRequest
{
    /// <summary>
    /// Name of the collection to snapshot
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// If true, create snapshot only on specific node. Otherwise on all nodes.
    /// </summary>
    public bool SingleNode { get; set; }

    /// <summary>
    /// Node URL (required if SingleNode is true)
    /// </summary>
    public string? NodeUrl { get; set; }
}
