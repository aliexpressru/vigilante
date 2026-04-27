using k8s;
using k8s.Models;
using System.Text.Json;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Manages Kubernetes resources (pods, StatefulSets) for Qdrant cluster
/// </summary>
public class KubernetesManager(
    IKubernetes? kubernetes,
    IPodCommandExecutor podCommandExecutor,
    ILogger<KubernetesManager> logger) : IKubernetesManager
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
            logger.LogWarning(KubernetesConstants.NamespaceNotProvidedForPodMessage, podName, KubernetesConstants.DefaultNamespace);
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
        return await UpdateStatefulSetAsync(
            statefulSetName,
            namespaceParameter,
            statefulSet =>
            {
                // Trigger rollout restart by adding/updating annotation
                var now = DateTime.UtcNow.ToString(KubernetesConstants.Iso8601Format);
                statefulSet.Spec.Template.Metadata ??= new V1ObjectMeta();
                statefulSet.Spec.Template.Metadata.Annotations ??= new Dictionary<string, string>();
                statefulSet.Spec.Template.Metadata.Annotations[KubernetesConstants.RestartedAtAnnotationKey] = now;
            },
            KubernetesConstants.RolloutRestartOperation,
            cancellationToken);
    }

    public async Task<bool> ScaleStatefulSetAsync(string statefulSetName, int replicas, string? namespaceParameter = null, CancellationToken cancellationToken = default)
    {
        return await UpdateStatefulSetAsync(
            statefulSetName,
            namespaceParameter,
            statefulSet =>
            {
                // Update replicas
                statefulSet.Spec.Replicas = replicas;
            },
            string.Format(KubernetesConstants.ScaleOperationFormat, replicas),
            cancellationToken);
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
                    fieldSelector: KubernetesConstants.WarningEventFieldSelector,
                    limit: KubernetesConstants.EventsFetchLimit,
                    cancellationToken: cancellationToken);

                if (eventsListV1?.Items != null && eventsListV1.Items.Count > 0)
                {
                    logger.LogInformation("Found {Count} warning events in namespace {Namespace} using Events v1 API", 
                        eventsListV1.Items.Count, ns);

                    var sortedEvents = eventsListV1.Items
                        .OrderByDescending(e => e.EventTime ?? DateTime.MinValue);

                    foreach (var evt in sortedEvents)
                    {
                        warnings.Add(FormatEventWarning(
                            evt.EventTime,
                            evt.Regarding?.Kind,
                            evt.Regarding?.Name,
                            evt.Reason,
                            evt.Note));
                    }

                    logger.LogInformation("Formatted {Count} warning events from namespace {Namespace}", warnings.Count, ns);
                    return warnings;
                }
            }
            catch (Exception exV1)
            {
                logger.LogDebug(exV1, KubernetesConstants.EventsV1NotAvailableMessage);
            }

            // Fallback to CoreV1 Events API (older Kubernetes versions)
            var eventsList = await kubernetes.CoreV1.ListNamespacedEventAsync(
                namespaceParameter: ns,
                fieldSelector: KubernetesConstants.WarningEventFieldSelector,
                limit: KubernetesConstants.EventsFetchLimit,
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
                warnings.Add(FormatEventWarning(
                    evt.LastTimestamp ?? evt.EventTime,
                    evt.InvolvedObject?.Kind,
                    evt.InvolvedObject?.Name,
                    evt.Reason,
                    evt.Message));
            }

            logger.LogInformation("Formatted {Count} warning events from namespace {Namespace}", warnings.Count, ns);
        }
        catch (Exception ex)
        {
            // Check if it's a Forbidden error (RBAC issue)
            if (ex.Message.Contains(KubernetesConstants.ForbiddenError) || 
                ex.Message.Contains(KubernetesConstants.ForbiddenStatusCode))
            {
                logger.LogWarning(KubernetesConstants.RbacPermissionErrorMessage, ns);
            }
            else
            {
                logger.LogError(ex, "Failed to fetch warning events for namespace {Namespace}", ns);
            }
        }

        return warnings;
    }

    public string GetCurrentNamespace()
    {
        try
        {
            if (File.Exists(KubernetesConstants.ServiceAccountNamespacePath))
            {
                return File.ReadAllText(KubernetesConstants.ServiceAccountNamespacePath).Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read service account namespace");
        }

        return KubernetesConstants.DefaultNamespace;
    }

    public async Task UpdateConfigMapDataAsync(
        string configMapName,
        string key,
        string value,
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return;
        }

        var ns = namespaceParameter ?? GetCurrentNamespace();

        logger.LogInformation("Updating ConfigMap {Name} key {Key} in namespace {Namespace}", 
            configMapName, key, ns);

        var patch = new
        {
            data = new Dictionary<string, string>
            {
                [key] = value
            }
        };

        await kubernetes.CoreV1.PatchNamespacedConfigMapAsync(
            new V1Patch(patch, V1Patch.PatchType.MergePatch),
            configMapName,
            ns,
            cancellationToken: cancellationToken);
        
        logger.LogInformation("Successfully updated ConfigMap {Name} key {Key}", configMapName, key);
    }

    public async Task<QdrantStorageUsageInfo?> GetQdrantStorageUsageAsync(
        string? podName,
        string? namespaceParameter,
        string storagePath,
        string? nodeUrl,
        CancellationToken cancellationToken)
    {
        var ns = namespaceParameter ?? GetCurrentNamespace();
        var normalizedStoragePath = storagePath.TrimEnd('/');

        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return null;
        }

        try
        {
            podName = await ResolveQdrantPodNameAsync(podName, nodeUrl, ns, cancellationToken);
            if (string.IsNullOrWhiteSpace(podName))
            {
                return null;
            }

            var baseDirectory = Path.GetDirectoryName(normalizedStoragePath)?.Replace("\\", "/");
            var itemName = Path.GetFileName(normalizedStoragePath);
            if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(itemName))
            {
                logger.LogWarning("Invalid storage path provided: {StoragePath}", storagePath);
                return null;
            }

            var usedBytes = await podCommandExecutor.GetSizeAsync(
                podName,
                ns,
                baseDirectory,
                itemName,
                cancellationToken);

            if (!usedBytes.HasValue)
            {
                logger.LogWarning(
                    "Could not calculate disk usage for path {Path} in pod {PodName}",
                    normalizedStoragePath,
                    podName);
                return null;
            }

            var pod = await kubernetes.CoreV1.ReadNamespacedPodAsync(
                name: podName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);

            var pvcNames = GetStoragePvcNamesFromPod(pod);
            if (pvcNames.Count == 0)
            {
                logger.LogWarning(
                    "No PVCs mounted for Qdrant storage were found in pod {PodName}",
                    podName);
                return null;
            }

            var capacityBytes = 0L;
            foreach (var pvcName in pvcNames)
            {
                var pvc = await kubernetes.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(
                    name: pvcName,
                    namespaceParameter: ns,
                    cancellationToken: cancellationToken);

                var storageCapacity = pvc.Status?.Capacity?.TryGetValue("storage", out var quantity) == true
                    ? ParseKubernetesQuantityToBytes(quantity?.ToString())
                    : null;

                if (!storageCapacity.HasValue)
                {
                    logger.LogWarning(
                        "PVC {PvcName} in namespace {Namespace} has no parseable storage capacity",
                        pvcName,
                        ns);
                    continue;
                }

                capacityBytes += storageCapacity.Value;
            }

            if (capacityBytes <= 0)
            {
                logger.LogWarning(
                    "Total PVC capacity is zero for pod {PodName} in namespace {Namespace}",
                    podName,
                    ns);
                return null;
            }

            var usagePercent = Math.Round((decimal)usedBytes.Value / capacityBytes * 100m, 2, MidpointRounding.AwayFromZero);

            return new QdrantStorageUsageInfo
            {
                PodName = podName,
                Namespace = ns,
                StoragePath = normalizedStoragePath,
                UsedBytes = usedBytes.Value,
                PvcCapacityBytes = capacityBytes,
                UsagePercent = usagePercent,
                PvcNames = pvcNames
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to calculate Qdrant storage usage for pod {PodName} in namespace {Namespace}",
                podName ?? "<auto>",
                ns);
            return null;
        }
    }

    public async Task<QdrantMemoryUsageInfo?> GetQdrantMemoryUsageAsync(
        string? podName,
        string? namespaceParameter,
        string? nodeUrl,
        CancellationToken cancellationToken)
    {
        var ns = namespaceParameter ?? GetCurrentNamespace();

        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return null;
        }

        try
        {
            podName = await ResolveQdrantPodNameAsync(podName, nodeUrl, ns, cancellationToken);
            if (string.IsNullOrWhiteSpace(podName))
            {
                return null;
            }

            var pod = await kubernetes.CoreV1.ReadNamespacedPodAsync(
                name: podName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);

            var qdrantContainer = pod.Spec?.Containers?
                .FirstOrDefault(c => string.Equals(c.Name, QdrantConstants.ContainerName, StringComparison.OrdinalIgnoreCase))
                ?? pod.Spec?.Containers?.FirstOrDefault();

            var requestBytes = qdrantContainer?.Resources?.Requests?.TryGetValue("memory", out var requestQuantity) == true
                ? ParseKubernetesQuantityToBytes(requestQuantity?.ToString())
                : null;
            var limitBytes = qdrantContainer?.Resources?.Limits?.TryGetValue("memory", out var limitQuantity) == true
                ? ParseKubernetesQuantityToBytes(limitQuantity?.ToString())
                : null;

            var metricsPayload = await kubernetes.CustomObjects.ListNamespacedCustomObjectAsync(
                group: "metrics.k8s.io",
                version: "v1beta1",
                namespaceParameter: ns,
                plural: "pods",
                cancellationToken: cancellationToken);

            var usedBytes = TryExtractPodMemoryUsageBytes(metricsPayload, podName);
            if (!usedBytes.HasValue)
            {
                logger.LogWarning("Could not find memory usage in metrics.k8s.io for pod {PodName}", podName);
                return null;
            }

            var denominator = requestBytes ?? limitBytes;
            var usagePercent = denominator.HasValue && denominator.Value > 0
                ? (decimal?)Math.Round((decimal)usedBytes.Value / denominator.Value * 100m, 2, MidpointRounding.AwayFromZero)
                : null;

            return new QdrantMemoryUsageInfo
            {
                PodName = podName,
                Namespace = ns,
                UsedBytes = usedBytes.Value,
                RequestBytes = requestBytes,
                LimitBytes = limitBytes,
                UsagePercent = usagePercent
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to calculate Qdrant memory usage for pod {PodName} in namespace {Namespace}",
                podName ?? "<auto>", ns);
            return null;
        }
    }

    private async Task<bool> UpdateStatefulSetAsync(
        string statefulSetName,
        string? namespaceParameter,
        Action<V1StatefulSet> modifyAction,
        string operationDescription,
        CancellationToken cancellationToken)
    {
        if (kubernetes == null)
        {
            logger.LogWarning(KubernetesConstants.KubernetesClientNotAvailableMessage);
            return false;
        }
        
        if (string.IsNullOrEmpty(namespaceParameter))
        {
            logger.LogWarning(KubernetesConstants.NamespaceNotProvidedForStatefulSetMessage, 
                statefulSetName, KubernetesConstants.DefaultNamespace);
        }
        
        var ns = namespaceParameter ?? KubernetesConstants.DefaultNamespace;
        
        try
        {
            logger.LogInformation("Performing {Operation} for StatefulSet {StatefulSetName} in namespace {Namespace}", 
                operationDescription, statefulSetName, ns);
            
            // Get current StatefulSet
            var statefulSet = await kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            // Apply modification
            modifyAction(statefulSet);
            
            // Update StatefulSet
            await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(
                body: statefulSet,
                name: statefulSetName,
                namespaceParameter: ns,
                cancellationToken: cancellationToken);
            
            logger.LogInformation("Successfully performed {Operation} for StatefulSet {StatefulSetName}", 
                operationDescription, statefulSetName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to {Operation} for StatefulSet {StatefulSetName} in namespace {Namespace}", 
                operationDescription, statefulSetName, ns);
            return false;
        }
    }

    private static string FormatEventWarning(
        DateTime? timestamp,
        string? kind,
        string? name,
        string? reason,
        string? message)
    {
        var timestampStr = timestamp.HasValue
            ? timestamp.Value.ToString(KubernetesConstants.EventTimestampFormat)
            : KubernetesConstants.UnknownTime;

        var involvedObject = !string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(name)
            ? $"{kind}/{name}"
            : KubernetesConstants.UnknownObject;

        var eventReason = reason ?? KubernetesConstants.UnknownReason;
        var eventMessage = message ?? KubernetesConstants.NoMessage;

        return string.Format(KubernetesConstants.EventWarningFormat,
            timestampStr, involvedObject, eventReason, eventMessage);
    }

    private static List<string> GetStoragePvcNamesFromPod(V1Pod pod)
    {
        var qdrantContainer = pod.Spec?.Containers?
            .FirstOrDefault(c => string.Equals(c.Name, QdrantConstants.ContainerName, StringComparison.OrdinalIgnoreCase))
            ?? pod.Spec?.Containers?.FirstOrDefault();

        var storageVolumeNames = qdrantContainer?.VolumeMounts?
            .Where(m =>
                !string.IsNullOrWhiteSpace(m.Name) &&
                !string.IsNullOrWhiteSpace(m.MountPath) &&
                m.MountPath.StartsWith("/qdrant", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        var volumes = pod.Spec?.Volumes ?? [];

        var matchingClaims = volumes
            .Where(v =>
                v.PersistentVolumeClaim != null &&
                !string.IsNullOrWhiteSpace(v.PersistentVolumeClaim.ClaimName) &&
                (storageVolumeNames.Count == 0 || storageVolumeNames.Contains(v.Name)))
            .Select(v => v.PersistentVolumeClaim!.ClaimName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return matchingClaims;
    }

    private async Task<string?> ResolveQdrantPodNameAsync(
        string? podName,
        string? nodeUrl,
        string ns,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(podName))
        {
            return podName;
        }

        var pods = await kubernetes!.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: ns,
            labelSelector: KubernetesConstants.QdrantAppLabelSelector,
            cancellationToken: cancellationToken);

        var runningPods = pods.Items
            .Where(p => p.Status?.Phase == KubernetesConstants.PodPhaseRunning)
            .ToList();

        var nodeHost = TryExtractHost(nodeUrl);
        var resolvedPodName = runningPods
            .Where(p => string.IsNullOrWhiteSpace(nodeHost) || string.Equals(p.Status?.PodIP, nodeHost, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Metadata?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? runningPods
                .Select(p => p.Metadata?.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        if (string.IsNullOrWhiteSpace(resolvedPodName))
        {
            logger.LogWarning("No running qdrant pod found in namespace {Namespace}", ns);
        }

        return resolvedPodName;
    }

    private static long? ParseKubernetesQuantityToBytes(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            return null;
        }

        var trimmed = quantity.Trim();
        if (long.TryParse(trimmed, out var plainBytes))
        {
            return plainBytes;
        }

        if (TryParseBinaryQuantity(trimmed, "Ki", 1L << 10, out var kibibytes))
            return kibibytes;
        if (TryParseBinaryQuantity(trimmed, "Mi", 1L << 20, out var mebibytes))
            return mebibytes;
        if (TryParseBinaryQuantity(trimmed, "Gi", 1L << 30, out var gibibytes))
            return gibibytes;
        if (TryParseBinaryQuantity(trimmed, "Ti", 1L << 40, out var tebibytes))
            return tebibytes;
        if (TryParseBinaryQuantity(trimmed, "Pi", 1L << 50, out var pebibytes))
            return pebibytes;

        return null;
    }

    private static bool TryParseBinaryQuantity(string valueWithUnit, string unit, long multiplier, out long? result)
    {
        result = null;
        if (!valueWithUnit.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numberPart = valueWithUnit[..^unit.Length];
        if (!long.TryParse(numberPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return true;
        }

        result = value * multiplier;
        return true;
    }

    private static long? TryExtractPodMemoryUsageBytes(object metricsPayload, string podName)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metricsPayload));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            var currentPodName = item.TryGetProperty("metadata", out var metadata) &&
                                 metadata.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (!string.Equals(currentPodName, podName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!item.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var container in containers.EnumerateArray())
            {
                var containerName = container.TryGetProperty("name", out var containerNameElement)
                    ? containerNameElement.GetString()
                    : null;

                if (!string.Equals(containerName, QdrantConstants.ContainerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (container.TryGetProperty("usage", out var usage) &&
                    usage.TryGetProperty("memory", out var memoryElement))
                {
                    return ParseKubernetesQuantityToBytes(memoryElement.GetString());
                }
            }

            var firstContainer = containers.EnumerateArray().FirstOrDefault();
            if (firstContainer.ValueKind == JsonValueKind.Object &&
                firstContainer.TryGetProperty("usage", out var firstUsage) &&
                firstUsage.TryGetProperty("memory", out var firstMemoryElement))
            {
                return ParseKubernetesQuantityToBytes(firstMemoryElement.GetString());
            }
        }

        return null;
    }

    private static string? TryExtractHost(string? nodeUrl)
    {
        if (string.IsNullOrWhiteSpace(nodeUrl))
        {
            return null;
        }

        if (Uri.TryCreate(nodeUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return nodeUrl;
    }
}

