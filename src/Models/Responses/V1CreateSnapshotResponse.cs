namespace Vigilante.Models.Responses;

/// <summary>
/// Response for create snapshot operation
/// </summary>
public class V1CreateSnapshotResponse : BaseOperationResponse
{
    /// <summary>
    /// Results per node (only for multi-node operations)
    /// Key is node URL, Value is the snapshot name (null if failed)
    /// </summary>
    public Dictionary<string, string?>? Results { get; set; }
    
    /// <summary>
    /// Snapshot name (for single node operations)
    /// </summary>
    public string? SnapshotName { get; set; }
}
