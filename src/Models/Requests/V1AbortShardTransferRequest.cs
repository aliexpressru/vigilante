namespace Vigilante.Models.Requests;

public class V1AbortShardTransferRequest
{
    /// <summary>
    /// Collection name to abort shard transfer for
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Source peer ID for the transfer to abort
    /// </summary>
    public ulong? SourcePeerId { get; set; }
    
    /// <summary>
    /// Target peer ID for the transfer to abort
    /// </summary>
    public ulong? TargetPeerId { get; set; }
    
    /// <summary>
    /// Shard ID to abort transfer for
    /// </summary>
    public uint? ShardId { get; set; }
}

