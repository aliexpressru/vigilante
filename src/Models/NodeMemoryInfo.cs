namespace Vigilante.Models;

public class NodeMemoryInfo
{
    /// <summary>
    /// Current memory usage in bytes from metrics.k8s.io.
    /// </summary>
    public long? UsedBytes { get; set; }

    /// <summary>
    /// Requested memory in bytes from pod resources.
    /// </summary>
    public long? RequestBytes { get; set; }

    /// <summary>
    /// Memory limit in bytes from pod resources.
    /// </summary>
    public long? LimitBytes { get; set; }

    /// <summary>
    /// Usage percentage relative to request (fallback to limit if request is missing).
    /// </summary>
    public decimal? UsagePercent { get; set; }
}
