using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request to delete a collection
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
    /// If true, delete only on specified node. If false, delete on all nodes.
    /// </summary>
    public required bool SingleNode { get; set; }
    
    /// <summary>
    /// Node URL for API deletion (required when SingleNode = true and DeletionType = Api)
    /// </summary>
    public string? NodeUrl { get; set; }
    
    /// <summary>
    /// Pod name for disk deletion (required when SingleNode = true and DeletionType = Disk)
    /// </summary>
    public string? PodName { get; set; }
    
    /// <summary>
    /// Pod namespace for disk deletion (required when SingleNode = true and DeletionType = Disk)
    /// </summary>
    public string? PodNamespace { get; set; }
}

