namespace Vigilante.Models.Responses;

/// <summary>
/// Response for delete collection operation
/// </summary>
public class V1DeleteCollectionResponse
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
    /// Key is node identifier (URL for API, PodName for Disk)
    /// </summary>
    public Dictionary<string, NodeDeletionResult>? Results { get; set; }
}

public class NodeDeletionResult
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

