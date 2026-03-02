using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request to delete a collection on specified nodes
/// </summary>
public class V1DeleteCollectionRequest
{
    /// <summary>
    /// Name of the collection to delete
    /// </summary>
    public required string CollectionName { get; set; }
    
    /// <summary>
    /// Type of deletion operation (API or Disk)
    /// </summary>
    public required CollectionDeletionType DeletionType { get; set; }
    
    /// <summary>
    /// List of node URLs for API deletion (required when DeletionType = Api)
    /// Each entry should be a node URL like "http://qdrant-0:6333"
    /// </summary>
    public List<string>? NodeUrls { get; set; }
    
    /// <summary>
    /// List of pod specifications for disk deletion (required when DeletionType = Disk)
    /// Each entry contains pod name and namespace
    /// </summary>
    public List<PodSpecification>? Pods { get; set; }

    /// <summary>
    /// Delete all snapshots for this collection after successful deletion.
    /// null = use global Snapshot.DeleteWithCollection config value (default: true).
    /// </summary>
    public bool? DeleteSnapshots { get; set; }

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

