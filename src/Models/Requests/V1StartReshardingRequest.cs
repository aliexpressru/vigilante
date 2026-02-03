namespace Vigilante.Models.Requests;

public class V1StartReshardingRequest
{
    /// <summary>
    /// Collection name to start resharding for
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Resharding direction: "Up" to scale up (add shard) or "Down" to scale down (remove shard).
    /// Must be a valid ReshardingOperationDirection enum value.
    /// </summary>
    public string? Direction { get; set; }
    
    /// <summary>
    /// Optional peer ID to issue resharding operation on
    /// </summary>
    public ulong? PeerId { get; set; }
}
