namespace Vigilante.Models;

public class CollectionOptimizationInfo
{
    public string Optimizer { get; set; } = string.Empty;

    public int SegmentsCount { get; set; }

    public ulong? Done { get; set; }

    public ulong? Total { get; set; }
}
