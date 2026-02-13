using Aer.QdrantClient.Http.Models.Shared;

namespace Vigilante.Models;

public class CollectionInfo
{
    public string PodName { get; set; } = string.Empty;

    public string NodeUrl { get; set; } = string.Empty;

    public string PeerId { get; set; } = string.Empty;

    public string CollectionName { get; set; } = string.Empty;
    
    public string PodNamespace { get; set; } = string.Empty;

    public Dictionary<string, object> Metrics { get; set; } = new();
    
    public List<string> Issues { get; set; } = new();
    
    public List<string> Aliases { get; set; } = new();
    
    /// <summary>
    /// Collection status from Qdrant API.
    /// Green - all good, Yellow - optimization is running, Red - some operations failed.
    /// </summary>
    public QdrantCollectionStatus? Status { get; set; }
}
