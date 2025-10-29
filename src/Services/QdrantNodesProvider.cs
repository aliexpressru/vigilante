using k8s;
using Vigilante.Configuration;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class QdrantNodesProvider(
    IConfiguration configuration, 
    ILogger<QdrantNodesProvider> logger)
    : IQdrantNodesProvider
{
    private readonly Lazy<IKubernetes> _kubernetes = new(() => new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
    private const string NamespaceFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

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
        logger.LogInformation("Starting GetNodesAsync");
        try
        {
            logger.LogInformation("Attempting to get nodes from Kubernetes");
            var nodes = await GetNodesFromK8sAsync(cancellationToken);
            if (nodes.Count > 0)
            {
                logger.LogInformation("Successfully found {Count} nodes in Kubernetes", nodes.Count);
                return nodes;
            }
            logger.LogWarning("No nodes found in Kubernetes");
        }
        catch(Exception e)
        {
            logger.LogError(e, "Failed to get Qdrant nodes from Kubernetes, falling back to other methods.");
        }

        // Try to get nodes from environment variables
        logger.LogInformation("Attempting to get nodes from environment variables");
        var nodesFromEnv = GetNodesFromEnvironment();
        if (nodesFromEnv.Any())
        {
            logger.LogInformation("Found {Count} nodes in environment variables", nodesFromEnv.Count);
            return nodesFromEnv;
        }
        logger.LogInformation("No nodes found in environment variables");

        // Try to get nodes from configuration
        logger.LogInformation("Attempting to get nodes from configuration");
        var nodesFromConfig = configuration.GetSection("Qdrant:Nodes").Get<List<QdrantNodeConfig>>();
        if (nodesFromConfig != null && nodesFromConfig.Any())
        {
            logger.LogInformation("Found {Count} nodes in configuration", nodesFromConfig.Count);
            return nodesFromConfig;
        }
        logger.LogWarning("No nodes found in configuration");
        
        logger.LogWarning("No Qdrant nodes found through any discovery method");
        return [];
    }

    private async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesFromK8sAsync(CancellationToken cancellationToken)
    {
        var currentNamespace = GetCurrentNamespace();
        logger.LogInformation("Looking for Qdrant pods in namespace: {Namespace}", currentNamespace);
        
        logger.LogDebug("Querying K8s API for pods with label app=qdrant");
        var pods = await _kubernetes.Value.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: currentNamespace,
            labelSelector: "app=qdrant",
            cancellationToken: cancellationToken);

        logger.LogInformation("Found {TotalPods} total pods, {RunningPods} in Running state", 
            pods.Items.Count,
            pods.Items.Count(p => p.Status.Phase == "Running"));

        var nodes = pods.Items
            .Where(pod => pod.Status.Phase == "Running")
            .Select(pod =>
            {
                logger.LogDebug("Processing pod: Name={Name}, IP={IP}, Phase={Phase}", 
                    pod.Metadata.Name, pod.Status.PodIP, pod.Status.Phase);
                return new QdrantNodeConfig
                {
                    Host = pod.Status.PodIP,
                    Port = 6333, // Default Qdrant port
                    Namespace = pod.Metadata.NamespaceProperty,
                    PodName = pod.Metadata.Name
                };
            })
            .ToList();

        foreach (var node in nodes)
        {
            logger.LogInformation("Discovered Qdrant node: Host={Host}, Port={Port}, Pod={Pod}, Namespace={Namespace}", 
                node.Host, node.Port, node.PodName, node.Namespace);
        }

        return nodes;
    }

    private IReadOnlyList<QdrantNodeConfig> GetNodesFromEnvironment()
    {
        var nodesEnv = Environment.GetEnvironmentVariable("QDRANT_NODES");
        logger.LogDebug("QDRANT_NODES environment variable value: {Value}", nodesEnv ?? "null");
        
        if (string.IsNullOrEmpty(nodesEnv))
        {
            return [];
        }

        var nodes = nodesEnv.Split(';')
            .Select(node =>
            {
                var parts = node.Split(':');
                var config = new QdrantNodeConfig
                {
                    Host = parts[0],
                    Port = parts.Length > 1 ? int.Parse(parts[1]) : 6333
                };
                logger.LogDebug("Parsed node from environment: Host={Host}, Port={Port}", config.Host, config.Port);
                return config;
            })
            .ToList();

        return nodes;
    }
}
