using Aer.QdrantClient.Http.Models.Shared;

namespace Vigilante.Models;

public class CollectionInfo
{
    public string PodName { get; set; } = string.Empty;

    public string NodeUrl { get; set; } = string.Empty;

    public string PeerId { get; set; } = string.Empty;

    public string CollectionName { get; set; } = string.Empty;
    
    public string PodNamespace { get; set; } = string.Empty;

    public CollectionMetrics Metrics { get; set; } = [];
    
    public List<string> Issues { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<CollectionOptimizationInfo> RunningOptimizations { get; set; } = [];
    
    public List<string> Aliases { get; set; } = [];
    
    /// <summary>
    /// Collection status from Qdrant API.
    /// Green - all good, Yellow - optimization is running, Red - some operations failed.
    /// </summary>
    public QdrantCollectionStatus? Status { get; set; }

    /// <summary>
    /// HNSW index m parameter (edges per node). 0 or null = global index disabled (multi-tenant mode).
    /// </summary>
    public ulong? HnswM { get; set; }
}
