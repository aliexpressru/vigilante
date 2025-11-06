namespace Vigilante.Models.Responses;

/// <summary>
/// Response for create snapshot operation
/// </summary>
public class V1CreateSnapshotResponse
{
    /// <summary>
    /// Overall operation message
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether the operation was successful overall
    /// </summary>
    public bool Success { get; set; }
    
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
