namespace Vigilante.Models.Requests;

public class V1DeletePodRequest
{
    /// <summary>
    /// Name of the pod to delete
    /// </summary>
    public string PodName { get; set; } = string.Empty;
    
    /// <summary>
    /// Namespace of the pod (optional, will use current namespace if not specified)
    /// </summary>
    public string? Namespace { get; set; }
}

