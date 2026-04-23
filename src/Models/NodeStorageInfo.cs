namespace Vigilante.Models;

public class NodeStorageInfo
{
    /// <summary>
    /// Total used bytes for Qdrant storage directory on this node.
    /// </summary>
    public long? UsedBytes { get; set; }

    /// <summary>
    /// Total PVC storage capacity bytes allocated to this node.
    /// </summary>
    public long? CapacityBytes { get; set; }

    /// <summary>
    /// PVC usage percentage (0-100).
    /// </summary>
    public decimal? UsagePercent { get; set; }
}
