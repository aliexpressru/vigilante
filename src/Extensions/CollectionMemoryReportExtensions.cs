using Aer.QdrantClient.Http.Models.Responses;
using Vigilante.Models;

namespace Vigilante.Extensions;

internal static class CollectionMemoryReportExtensions
{
    public static CollectionMemoryReportInfo ToInfo(
        this CollectionMemoryReportResponse.CollectionMemoryReport report)
    {
        return new CollectionMemoryReportInfo
        {
            Total = report.Total.ToInfo(),
            Vectors = report.Vectors?.Select(v => v.ToInfo()).ToArray() ?? [],
            SparseVectors = report.SparseVectors?.Select(v => v.ToInfo()).ToArray() ?? [],
            Payload = report.Payload.ToInfo(),
            PayloadIndex = report.PayloadIndex?.Select(p => p.ToInfo()).ToArray() ?? [],
            Other = new OtherComponentsMemoryReportInfo
            {
                IdTracker = report.Other.IdTracker.ToInfo()
            }
        };
    }

    private static MemoryUsageInfo ToInfo(this CollectionMemoryReportResponse.MemoryUsage usage) =>
        new()
        {
            DiskBytes = usage.DiskBytes,
            RamBytes = usage.RamBytes,
            CachedBytes = usage.CachedBytes,
            ExpectedCacheBytes = usage.ExpectedCacheBytes
        };

    private static VectorMemoryReportInfo ToInfo(this CollectionMemoryReportResponse.VectorMemoryReport vector) =>
        new()
        {
            Name = vector.Name,
            Storage = vector.Storage.ToInfo(),
            Index = vector.Index.ToInfo()
        };

    private static PayloadIndexMemoryReportInfo ToInfo(
        this CollectionMemoryReportResponse.PayloadIndexMemoryReport payloadIndex) =>
        new()
        {
            Name = payloadIndex.Name,
            Usage = payloadIndex.Usage.ToInfo()
        };
}
