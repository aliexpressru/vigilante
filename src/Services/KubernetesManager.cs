using k8s;
using k8s.Models;
using Vigilante.Constants;
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
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for pod {PodName}, using default '{DefaultNamespace}'", podName, KubernetesConstants.DefaultNamespace);
        }
        
        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;
        
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
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for StatefulSet {StatefulSetName}, using default '{DefaultNamespace}'", statefulSetName, KubernetesConstants.DefaultNamespace);
        }
        
        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;
        
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
            statefulSet.Spec.Template.Metadata.Annotations[KubernetesConstants.RestartedAtAnnotationKey] = now;
            
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
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning("Namespace not provided for StatefulSet {StatefulSetName}, using default '{DefaultNamespace}'", statefulSetName, KubernetesConstants.DefaultNamespace);
        }
        
        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;
        
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

    public async Task<List<string>> GetWarningEventsAsync(string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return new List<string>();
        }

        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;
        var warnings = new List<string>();

        try
        {
            logger.LogInformation("Fetching warning events for namespace {Namespace}", ns);

            // Try events.k8s.io/v1 API first (newer Kubernetes versions)
            try
            {
                var eventsListV1 = await kubernetes.EventsV1.ListNamespacedEventAsync(
                    namespaceParameter: ns,
                    fieldSelector: "type=Warning",
                    limit: 20,
                    cancellationToken: cancellationToken);

                if (eventsListV1?.Items != null && eventsListV1.Items.Count > 0)
                {
                    logger.LogInformation("Found {Count} warning events in namespace {Namespace} using Events v1 API", 
                        eventsListV1.Items.Count, ns);

                    var sortedEvents = eventsListV1.Items
                        .OrderByDescending(e => e.EventTime ?? DateTime.MinValue);

                    foreach (var evt in sortedEvents)
                    {
                        var timestamp = evt.EventTime;
                        var timestampStr = timestamp.HasValue ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown time";
                        var involvedObject = evt.Regarding != null 
                            ? $"{evt.Regarding.Kind}/{evt.Regarding.Name}" 
                            : "Unknown object";
                        var reason = evt.Reason ?? "Unknown reason";
                        var message = evt.Note ?? "No message";

                        warnings.Add($"[{timestampStr}] {involvedObject}: {reason} - {message}");
                    }

                    logger.LogInformation("Formatted {Count} warning events from namespace {Namespace}", warnings.Count, ns);
                    return warnings;
                }
            }
            catch (Exception exV1)
            {
                logger.LogDebug(exV1, "Events v1 API not available or failed, falling back to CoreV1 Events");
            }

            // Fallback to CoreV1 Events API (older Kubernetes versions)
            var eventsList = await kubernetes.CoreV1.ListNamespacedEventAsync(
                namespaceParameter: ns,
                fieldSelector: "type=Warning",
                limit: 20,
                cancellationToken: cancellationToken);

            if (eventsList?.Items == null || eventsList.Items.Count == 0)
            {
                logger.LogInformation("No warning events found in namespace {Namespace} (checked both v1 and CoreV1 APIs)", ns);
                return warnings;
            }

            logger.LogInformation("Found {Count} warning events in namespace {Namespace} using CoreV1 API", 
                eventsList.Items.Count, ns);

            // Sort by last timestamp (most recent first) and format
            var warningEvents = eventsList.Items
                .OrderByDescending(e => e.LastTimestamp ?? e.EventTime ?? DateTime.MinValue);

            foreach (var evt in warningEvents)
            {
                var timestamp = evt.LastTimestamp ?? evt.EventTime;
                var timestampStr = timestamp.HasValue ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown time";
                var involvedObject = evt.InvolvedObject != null 
                    ? $"{evt.InvolvedObject.Kind}/{evt.InvolvedObject.Name}" 
                    : "Unknown object";
                var reason = evt.Reason ?? "Unknown reason";
                var message = evt.Message ?? "No message";

                warnings.Add($"[{timestampStr}] {involvedObject}: {reason} - {message}");
            }

            logger.LogInformation("Formatted {Count} warning events from namespace {Namespace}", warnings.Count, ns);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch warning events for namespace {Namespace}", ns);
        }

        return warnings;
    }

    public async Task<string?> GetPodNameByIpAsync(string podIp, string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return null;
        }

        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;

        try
        {
            logger.LogInformation("Getting pod name for IP {PodIp} in namespace {Namespace}", podIp, ns);

            var pods = await kubernetes.CoreV1.ListNamespacedPodAsync(
                namespaceParameter: ns,
                fieldSelector: $"status.podIP=={podIp}",
                cancellationToken: cancellationToken);

            logger.LogInformation("Found {PodsCount} pods matching IP {PodIp}", pods.Items.Count, podIp);

            return pods.Items.FirstOrDefault()?.Metadata.Name;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get pod name for IP {PodIp} in namespace {Namespace}", podIp, ns);
            return null;
        }
    }
}

