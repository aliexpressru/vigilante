namespace Vigilante.Models.Requests;

public class V1ReplicateShardsRequest
{
    /// <summary>
    /// Source peer ID for shard replication
    /// </summary>
    public ulong? SourcePeerId { get; set; }
    
    /// <summary>
    /// Target peer ID for shard replication
    /// </summary>
    public ulong? TargetPeerId { get; set; }
    
    /// <summary>
    /// Collection name to replicate shards from
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// List of shard IDs to replicate
    /// </summary>
    public uint[] ShardIdsToReplicate { get; set; } = [];
    
    /// <summary>
    /// Whether to move shards instead of copying
    /// </summary>
    public bool IsMoveShards { get; set; }
}
