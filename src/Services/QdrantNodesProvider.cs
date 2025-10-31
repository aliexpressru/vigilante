using k8s;
using Vigilante.Configuration;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Provides Qdrant nodes from Kubernetes, environment variables, or configuration.
/// Implements IDisposable to properly cleanup Kubernetes client.
/// </summary>
public class QdrantNodesProvider(
    IConfiguration configuration, 
    ILogger<QdrantNodesProvider> logger)
    : IQdrantNodesProvider, IDisposable
{
    private readonly Lazy<IKubernetes> _kubernetes = new(() => new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
    private const string NamespaceFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";
    private bool _disposed;

    private string GetCurrentNamespace()
    {
        if (File.Exists(NamespaceFilePath))
        {
            return File.ReadAllText(NamespaceFilePath).Trim();
        }
        
        return "qdrant";
    }

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
        var nodesFromConfig = configuration.GetSection("Qdrant:Nodes").Get<List<QdrantNodeConfig>>();
        if (nodesFromConfig != null && nodesFromConfig.Any())
        {
            return nodesFromConfig;
        }
        
        logger.LogWarning("No Qdrant nodes found through any discovery method");
        return [];
    }

    private async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesFromK8sAsync(CancellationToken cancellationToken)
    {
        var currentNamespace = GetCurrentNamespace();
        
        var pods = await _kubernetes.Value.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: currentNamespace,
            labelSelector: "app=qdrant",
            cancellationToken: cancellationToken);

        var nodes = pods.Items
            .Where(pod => pod.Status.Phase == "Running")
            .Select(pod => new QdrantNodeConfig
            {
                Host = pod.Status.PodIP,
                Port = 6333, // Default Qdrant port
                Namespace = pod.Metadata.NamespaceProperty,
                PodName = pod.Metadata.Name
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
        var nodesEnv = Environment.GetEnvironmentVariable("QDRANT_NODES");
        
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
                    Port = parts.Length > 1 ? int.Parse(parts[1]) : 6333
                };
            })
            .ToList();

        if (nodes.Count > 0)
        {
            logger.LogInformation("Discovered {Count} Qdrant nodes from environment variables", nodes.Count);
        }

        return nodes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        if (_kubernetes.IsValueCreated)
        {
            try
            {
                _kubernetes.Value.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error disposing Kubernetes client");
            }
        }
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
