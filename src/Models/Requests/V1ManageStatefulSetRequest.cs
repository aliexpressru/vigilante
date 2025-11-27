using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

public class V1ManageStatefulSetRequest
{
    /// <summary>
    /// Name of the StatefulSet to manage
    /// </summary>
    public string StatefulSetName { get; set; } = string.Empty;
    
    /// <summary>
    /// Namespace of the StatefulSet (optional, will use current namespace if not specified)
    /// </summary>
    public string? Namespace { get; set; }
    
    /// <summary>
    /// Type of operation to perform: Rollout or Scale
    /// </summary>
    public StatefulSetOperationType OperationType { get; set; }
    
    /// <summary>
    /// Number of replicas (required only for Scale operation)
    /// </summary>
    public int? Replicas { get; set; }
}

