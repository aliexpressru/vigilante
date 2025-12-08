using k8s;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Provides Qdrant nodes from Kubernetes, environment variables, or configuration.
/// Implements IDisposable to properly cleanup Kubernetes client.
/// </summary>
public class QdrantNodesProvider(
    IConfiguration configuration,
    IKubernetes? kubernetes,
    ILogger<QdrantNodesProvider> logger)
    : IQdrantNodesProvider
{

    public async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nodes = await GetNodesFromK8sAsync(cancellationToken);
            if (nodes.Count > 0)
            {
                return nodes;
            }
        }
        catch(Exception e)
        {
            logger.LogWarning(e, "Failed to get Qdrant nodes from Kubernetes, trying other methods");
        }

        // Try to get nodes from environment variables
        var nodesFromEnv = GetNodesFromEnvironment();
        if (nodesFromEnv.Any())
        {
            return nodesFromEnv;
        }

        // Try to get nodes from configuration
        var nodesFromConfig = configuration.GetSection(QdrantConstants.NodesConfigurationPath).Get<List<QdrantNodeConfig>>();
        if (nodesFromConfig != null && nodesFromConfig.Any())
        {
            return nodesFromConfig;
        }
        
        logger.LogWarning("No Qdrant nodes found through any discovery method");
        return [];
    }

    private string GetCurrentNamespace()
    {
        if (File.Exists(KubernetesConstants.ServiceAccountNamespacePath))
        {
            return File.ReadAllText(KubernetesConstants.ServiceAccountNamespacePath).Trim();
        }
        
        return KubernetesConstants.DefaultNamespace;
    }

    private async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesFromK8sAsync(CancellationToken cancellationToken)
    {
        if (kubernetes == null)
        {
            logger.LogDebug("Kubernetes client is not available, skipping K8s discovery");
            return [];
        }
        
        var currentNamespace = GetCurrentNamespace();
        
        var pods = await kubernetes.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: currentNamespace,
            labelSelector: KubernetesConstants.QdrantAppLabelSelector,
            cancellationToken: cancellationToken);

        var nodes = pods.Items
            .Where(pod => pod.Status.Phase == KubernetesConstants.PodPhaseRunning)
            .Select(pod =>
            {
                // Try to get StatefulSet name from owner references
                var statefulSetOwner = pod.Metadata.OwnerReferences?
                    .FirstOrDefault(o => o.Kind == KubernetesConstants.StatefulSetKind);
                
                string? statefulSetName = statefulSetOwner?.Name;
                
                // If no owner reference found, try to infer from pod name
                // StatefulSet pods are typically named like: <statefulset-name>-<ordinal>
                if (statefulSetName == null && pod.Metadata.Name != null)
                {
                    var lastDashIndex = pod.Metadata.Name.LastIndexOf('-');
                    if (lastDashIndex > 0 && int.TryParse(pod.Metadata.Name.Substring(lastDashIndex + 1), out _))
                    {
                        statefulSetName = pod.Metadata.Name.Substring(0, lastDashIndex);
                    }
                }
                
                return new QdrantNodeConfig
                {
                    Host = pod.Status.PodIP,
                    Port = QdrantConstants.DefaultPort,
                    Namespace = pod.Metadata.NamespaceProperty,
                    PodName = pod.Metadata.Name,
                    StatefulSetName = statefulSetName
                };
            })
            .ToList();

        if (nodes.Count > 0)
        {
            logger.LogInformation("Discovered {Count} Qdrant nodes from Kubernetes in namespace {Namespace}", 
                nodes.Count, currentNamespace);
        }

        return nodes;
    }

    private IReadOnlyList<QdrantNodeConfig> GetNodesFromEnvironment()
    {
        var nodesEnv = Environment.GetEnvironmentVariable(QdrantConstants.NodesEnvironmentVariable);
        
        if (string.IsNullOrEmpty(nodesEnv))
        {
            return [];
        }

        var nodes = nodesEnv.Split(';')
            .Select(node =>
            {
                var parts = node.Split(':');
                return new QdrantNodeConfig
                {
                    Host = parts[0],
                    Port = parts.Length > 1 ? int.Parse(parts[1]) : QdrantConstants.DefaultPort
                };
            })
            .ToList();

        if (nodes.Count > 0)
        {
            logger.LogInformation("Discovered {Count} Qdrant nodes from environment variables", nodes.Count);
        }

        return nodes;
    }
}
