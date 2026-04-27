namespace Vigilante.Models;

public class QdrantMemoryUsageInfo
{
    public string PodName { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public long UsedBytes { get; set; }

    public long? RequestBytes { get; set; }

    public long? LimitBytes { get; set; }

    public decimal? UsagePercent { get; set; }
}