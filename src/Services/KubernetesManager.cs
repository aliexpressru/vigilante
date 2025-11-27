using k8s;
using k8s.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Manages Kubernetes resources (pods, StatefulSets) for Qdrant cluster
/// </summary>
public class KubernetesManager(IKubernetes? kubernetes, ILogger<KubernetesManager> logger) : IKubernetesManager
{
    public async Task<bool> DeletePodAsync(string podName, string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning("Kubernetes client is not available. Running outside Kubernetes cluster?");
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for pod {PodName}, using default 'qdrant'", podName);
        }
        
        var ns = namespaceParameter ?? "qdrant";
        
        try
        {
            logger.LogInformation("Deleting pod {PodName} in namespace {Namespace}", podName, ns);
            
            await kubernetes.CoreV1.DeleteNamespacedPodAsync(
                name: podName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            logger.LogInformation("Successfully deleted pod {PodName}", podName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete pod {PodName} in namespace {Namespace}", podName, ns);
            return false;
        }
    }

    public async Task<bool> RolloutRestartStatefulSetAsync(string statefulSetName, string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning("Kubernetes client is not available. Running outside Kubernetes cluster?");
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for StatefulSet {StatefulSetName}, using default 'qdrant'", statefulSetName);
        }
        
        var ns = namespaceParameter ?? "qdrant";
        
        try
        {
            logger.LogInformation("Rolling out restart for StatefulSet {StatefulSetName} in namespace {Namespace}", 
                statefulSetName, ns);
            
            // Get current StatefulSet
            var statefulSet = await kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            // Trigger rollout restart by adding/updating annotation
            var now = DateTime.UtcNow.ToString("o");
            statefulSet.Spec.Template.Metadata ??= new V1ObjectMeta();
            statefulSet.Spec.Template.Metadata.Annotations ??= new Dictionary<string, string>();
            statefulSet.Spec.Template.Metadata.Annotations["vigilante.aer.io/restartedAt"] = now;
            
            // Update StatefulSet
            await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                body: statefulSet,
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            logger.LogInformation("Successfully triggered rollout restart for StatefulSet {StatefulSetName}", statefulSetName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rollout restart StatefulSet {StatefulSetName} in namespace {Namespace}", 
                statefulSetName, ns);
            return false;
        }
    }

    public async Task<bool> ScaleStatefulSetAsync(string statefulSetName, int replicas, string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning("Kubernetes client is not available. Running outside Kubernetes cluster?");
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for StatefulSet {StatefulSetName}, using default 'qdrant'", statefulSetName);
        }
        
        var ns = namespaceParameter ?? "qdrant";
        
        try
        {
            logger.LogInformation("Scaling StatefulSet {StatefulSetName} to {Replicas} replicas in namespace {Namespace}", 
                statefulSetName, replicas, ns);
            
            // Get current StatefulSet
            var statefulSet = await kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            // Update replicas
            statefulSet.Spec.Replicas = replicas;
            
            // Update StatefulSet
            await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                body: statefulSet,
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            logger.LogInformation("Successfully scaled StatefulSet {StatefulSetName} to {Replicas} replicas", 
                statefulSetName, replicas);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scale StatefulSet {StatefulSetName} to {Replicas} replicas in namespace {Namespace}", 
                statefulSetName, replicas, ns);
            return false;
        }
    }
}

