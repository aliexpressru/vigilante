namespace Vigilante.Services.Interfaces;

/// <summary>
/// Interface for managing Kubernetes resources (pods, StatefulSets)
/// </summary>
public interface IKubernetesManager
{
    /// <summary>
    /// Deletes a pod in the specified namespace
    /// </summary>
    Task<bool> DeletePodAsync(string podName, string? namespaceParameter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a rollout restart of a StatefulSet
    /// </summary>
    Task<bool> RolloutRestartStatefulSetAsync(string statefulSetName, string? namespaceParameter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scales a StatefulSet to the specified number of replicas
    /// </summary>
    Task<bool> ScaleStatefulSetAsync(string statefulSetName, int replicas, string? namespaceParameter = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets warning events from Kubernetes for the specified namespace
    /// </summary>
    Task<List<string>> GetWarningEventsAsync(string? namespaceParameter = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current namespace from the service account namespace file, or returns default namespace
    /// </summary>
    string GetCurrentNamespace();
    
    /// <summary>
    /// Reads an Endpoints resource, or creates it if it doesn't exist
    /// </summary>
    Task<k8s.Models.V1Endpoints> GetOrCreateEndpointsAsync(
        string endpointsName,
        Dictionary<string, string>? labels = null,
        Dictionary<string, string>? annotations = null,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Updates annotations on an Endpoints resource
    /// </summary>
    Task UpdateEndpointsAnnotationsAsync(
        string endpointsName,
        Dictionary<string, string> annotations,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Watches for changes to a specific Endpoints resource
    /// </summary>
    Task WatchEndpointsAsync(
        string endpointsName,
        Action<k8s.WatchEventType, k8s.Models.V1Endpoints> onEvent,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);
}
