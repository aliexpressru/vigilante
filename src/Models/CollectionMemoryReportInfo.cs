namespace Vigilante.Models;

public sealed class CollectionMemoryReportInfo
{
    public MemoryUsageInfo Total { get; init; } = new();

    public VectorMemoryReportInfo[] Vectors { get; init; } = [];

    public VectorMemoryReportInfo[] SparseVectors { get; init; } = [];

    public MemoryUsageInfo Payload { get; init; } = new();

    public PayloadIndexMemoryReportInfo[] PayloadIndex { get; init; } = [];

    public OtherComponentsMemoryReportInfo Other { get; init; } = new();
}

public sealed class VectorMemoryReportInfo
{
    public string Name { get; init; } = string.Empty;

    public MemoryUsageInfo Storage { get; init; } = new();

    public MemoryUsageInfo Index { get; init; } = new();
}

public sealed class PayloadIndexMemoryReportInfo
{
    public string Name { get; init; } = string.Empty;

    public MemoryUsageInfo Usage { get; init; } = new();
}

public sealed class OtherComponentsMemoryReportInfo
{
    public MemoryUsageInfo IdTracker { get; init; } = new();
}
