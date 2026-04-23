namespace Vigilante.Models;

/// <summary>
/// Aggregated Qdrant storage usage information for a single pod.
/// </summary>
public class QdrantStorageUsageInfo
{
    public string PodName { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public long UsedBytes { get; set; }

    public long PvcCapacityBytes { get; set; }

    public decimal UsagePercent { get; set; }

    public IReadOnlyList<string> PvcNames { get; set; } = [];
}