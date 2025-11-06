namespace Vigilante.Models.Responses;

/// <summary>
/// Response for delete snapshot operation
/// </summary>
public class V1DeleteSnapshotResponse: BaseOperationResponse
{
    /// <summary>
    /// Results per node (only for multi-node operations)
    /// Key is node URL
    /// </summary>
    public Dictionary<string, NodeSnapshotDeletionResult>? Results { get; set; }
}

public class NodeSnapshotDeletionResult
{
    /// <summary>
    /// Whether deletion succeeded on this node
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Error message if deletion failed
    /// </summary>
    public string? Error { get; set; }
}

