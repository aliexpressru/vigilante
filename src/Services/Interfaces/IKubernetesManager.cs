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
    /// Performs a rollout restart on a StatefulSet
    /// </summary>
    Task<bool> RolloutRestartStatefulSetAsync(string statefulSetName, string? namespaceParameter = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Scales a StatefulSet to the specified number of replicas
    /// </summary>
    Task<bool> ScaleStatefulSetAsync(string statefulSetName, int replicas, string? namespaceParameter = null, CancellationToken cancellationToken = default);
}

