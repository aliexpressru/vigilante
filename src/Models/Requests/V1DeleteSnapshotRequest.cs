using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request for deleting a collection snapshot on specified nodes
/// </summary>
public class V1DeleteSnapshotRequest
{
    /// <summary>
    /// Name of the collection
    /// </summary>
    public required string CollectionName { get; set; }

    /// <summary>
    /// Name of the snapshot to delete
    /// </summary>
    public required string SnapshotName { get; set; }

    /// <summary>
    /// Source where snapshot should be deleted from
    /// </summary>
    public required SnapshotSource Source { get; set; }

    /// <summary>
    /// List of node URLs for API deletion (required when Source = QdrantApi)
    /// Each entry should be a node URL like "http://qdrant-0:6333"
    /// </summary>
    public List<string>? NodeUrls { get; set; }
    
    /// <summary>
    /// List of pod specifications for disk deletion (required when Source = KubernetesStorage)
    /// Each entry contains pod name and namespace
    /// </summary>
    public List<PodSpecification>? Pods { get; set; }

    /// <summary>
    /// Specification of a pod for disk deletion
    /// </summary>
    public class PodSpecification
    {
        /// <summary>
        /// Pod name
        /// </summary>
        public required string PodName { get; set; }
        
        /// <summary>
        /// Pod namespace
        /// </summary>
        public required string PodNamespace { get; set; }
    }
}

