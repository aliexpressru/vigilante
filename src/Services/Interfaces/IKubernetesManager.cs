namespace Vigilante.Services.Interfaces;

using Vigilante.Models;

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
    /// Updates a specific key in ConfigMap data
    /// </summary>
    Task UpdateConfigMapDataAsync(
        string configMapName,
        string key,
        string value,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets Qdrant storage usage from pod disk and allocated PVC capacity.
    /// </summary>
    Task<QdrantStorageUsageInfo?> GetQdrantStorageUsageAsync(
        string? podName,
        string? namespaceParameter,
        string storagePath,
        string? nodeUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets Qdrant memory usage from metrics.k8s.io and pod resources.
    /// </summary>
    Task<QdrantMemoryUsageInfo?> GetQdrantMemoryUsageAsync(
        string? podName,
        string? namespaceParameter,
        string? nodeUrl,
        CancellationToken cancellationToken);
}
