namespace Vigilante.Constants;

/// <summary>
/// Metric keys used across multiple services for collection metrics
/// </summary>
public static class MetricConstants
{
    /// <summary>
    /// Metric key for pretty-formatted size (used in ClusterManager, CollectionService, TestDataProvider)
    /// </summary>
    public const string PrettySizeKey = "prettySize";
    
    /// <summary>
    /// Metric key for size in bytes (used in ClusterManager, CollectionService, TestDataProvider)
    /// </summary>
    public const string SizeBytesKey = "sizeBytes";
    
    /// <summary>
    /// Metric key for shards count (used in TestDataProvider)
    /// </summary>
    public const string ShardsKey = "shards";
    
    /// <summary>
    /// Metric key for outgoing transfers (used in TestDataProvider)
    /// </summary>
    public const string OutgoingTransfersKey = "outgoingTransfers";
    
    /// <summary>
    /// Metric key for shard states (used in TestDataProvider)
    /// </summary>
    public const string ShardStatesKey = "shardStates";
}

