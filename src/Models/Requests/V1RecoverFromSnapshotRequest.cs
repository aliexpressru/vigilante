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
    /// Node URL where to recover the collection
    /// </summary>
    public string NodeUrl { get; set; } = string.Empty;
}
