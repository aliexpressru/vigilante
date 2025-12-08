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
    /// Gets pod name by its IP address in the specified namespace
    /// </summary>
    Task<string?> GetPodNameByIpAsync(string podIp, string? namespaceParameter = null, CancellationToken cancellationToken = default);
}
