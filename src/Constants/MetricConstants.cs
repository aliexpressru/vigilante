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

    public const string RamBytesKey = "ramBytes";

    public const string PrettyRamSizeKey = "prettyRamSize";

    public const string MemoryReportKey = "memoryReport";
    
    /// <summary>
    /// Metric key for shards information including ID, state, and size
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
    
    /// <summary>
    /// Default value for pretty size when size is not available
    /// </summary>
    public const string NotAvailableValue = "N/A";
    
    /// <summary>
    /// Default pod name when pod name is not available
    /// </summary>
    public const string UnknownPodName = "unknown";
    
    /// <summary>
    /// Default error message when error details are not available
    /// </summary>
    public const string UnknownErrorMessage = "Unknown error";
    
    /// <summary>
    /// Status message for accepted recovery operation
    /// </summary>
    public const string RecoveryAcceptedMessage = "recovery accepted";
    
    /// <summary>
    /// Status message for successful recovery operation
    /// </summary>
    public const string RecoverySuccessMessage = "recovered successfully";
}

