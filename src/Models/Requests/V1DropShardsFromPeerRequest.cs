namespace Vigilante.Models.Requests;

public class V1DropShardsFromPeerRequest
{
    /// <summary>
    /// Collection name to drop shards from
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Peer ID to identify the peer to drop shards from
    /// </summary>
    public ulong? PeerId { get; set; }
    
    /// <summary>
    /// List of shard IDs to drop from the peer
    /// </summary>
    public uint[] ShardIds { get; set; } = [];
    
    /// <summary>
    /// Dry run mode - if true, calculates and logs operations without executing them
    /// </summary>
    public bool IsDryRun { get; set; }
}
