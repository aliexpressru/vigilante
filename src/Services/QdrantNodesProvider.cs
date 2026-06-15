using Aer.QdrantClient.Http.Abstractions;
using k8s;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Provides Qdrant nodes from Kubernetes, environment variables, or configuration.
/// Implements IDisposable to properly cleanup Kubernetes client.
/// </summary>
public class QdrantNodesProvider(
    IConfiguration configuration,
    IKubernetes? kubernetes,
    IKubernetesManager? kubernetesManager,
    IQdrantClientFactory clientFactory,
    IOptions<QdrantOptions> options,
    ILogger<QdrantNodesProvider> logger)
    : IQdrantNodesProvider
{
    private readonly QdrantOptions _options = options.Value;
    private string? _statefulSetName;
    private readonly Lock _statefulSetNameLock = new();

    public async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesAsync(CancellationToken cancellationToken)
    {
        // If Kubernetes client is available, use K8s discovery exclusively
        // This prevents showing static config nodes when StatefulSet is scaled to 0
        if (kubernetes != null)
        {
            try
            {
                var nodes = await GetNodesFromK8sAsync(cancellationToken);
                // Return K8s nodes even if empty - this is important for scaled-to-0 scenarios
                return nodes;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to get Qdrant nodes from Kubernetes, falling back to other methods");
            }
        }

        // Try to get nodes from environment variables
        var nodesFromEnv = GetNodesFromEnvironment();
        if (nodesFromEnv.Count != 0)
        {
            return nodesFromEnv;
        }

        // Try to get nodes from configuration (only when not in K8s)
        var nodesFromConfig = configuration.GetSection(QdrantConstants.NodesConfigurationPath).Get<List<QdrantNodeConfig>>();
        if (nodesFromConfig != null && nodesFromConfig.Count != 0)
        {
            return nodesFromConfig;
        }

        logger.LogWarning("No Qdrant nodes found through any discovery method");
        return [];
    }

    /// <summary>
    /// Gets the StatefulSet name from memory, or attempts to discover it from nodes
    /// </summary>
    public async Task<string?> GetStatefulSetNameAsync(CancellationToken cancellationToken)
    {
        // First, check if we already have it stored in memory
        lock (_statefulSetNameLock)
        {
            if (!string.IsNullOrEmpty(_statefulSetName))
            {
                return _statefulSetName;
            }
        }

        // Try to discover from running nodes
        var nodes = await GetNodesAsync(cancellationToken);
        var statefulSetName = nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.StatefulSetName))?.StatefulSetName;

        if (!string.IsNullOrEmpty(statefulSetName))
        {
            // Store the discovered name
            SetStatefulSetName(statefulSetName);
            logger.LogInformation("Discovered and stored StatefulSet name: {StatefulSetName}", statefulSetName);
            return statefulSetName;
        }

        return null;
    }

    /// <summary>
    /// Sets the StatefulSet name in memory
    /// </summary>
    public void SetStatefulSetName(string statefulSetName)
    {
        if (string.IsNullOrWhiteSpace(statefulSetName))
        {
            throw new ArgumentException("StatefulSet name cannot be null or empty", nameof(statefulSetName));
        }

        lock (_statefulSetNameLock)
        {
            _statefulSetName = statefulSetName;
            logger.LogInformation("StatefulSet name set to: {StatefulSetName}", statefulSetName);
        }
    }

    /// <summary>
    /// Builds a list of NodeInfo with peer IDs for all discovered nodes.
    /// This is a lightweight method suitable for scenarios where only basic node info and peer IDs are needed.
    /// </summary>
    public async Task<List<NodeInfo>> BuildNodeInfoListAsync(CancellationToken cancellationToken)
    {
        var nodeConfigs = await GetNodesAsync(cancellationToken);
        var tasks = nodeConfigs.Select(config => GetBasicNodeInfoAsync(config, cancellationToken));
        var nodeInfoList = await Task.WhenAll(tasks);
        return [.. nodeInfoList];
    }

    /// <summary>
    /// Gets basic node information including peer ID for a single node configuration.
    /// </summary>
    public async Task<NodeInfo> GetBasicNodeInfoAsync(QdrantNodeConfig nodeConfig, CancellationToken cancellationToken)
    {
        var nodeUrl = $"{QdrantConstants.HttpProtocol}{nodeConfig.Host}:{nodeConfig.Port}";
        var peerId = await GetPeerIdForNodeAsync(nodeUrl, nodeConfig, cancellationToken);

        return new NodeInfo
        {
            Url = nodeUrl,
            PeerId = peerId ?? 0,
            Namespace = nodeConfig.Namespace,
            PodName = nodeConfig.PodName,
            StatefulSetName = nodeConfig.StatefulSetName,
            LastSeen = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets the peer ID for a specific node
    /// </summary>
    private async Task<ulong?> GetPeerIdForNodeAsync(
        string nodeUrl,
        QdrantNodeConfig nodeConfig,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = clientFactory.CreateClient(nodeConfig.Host, nodeConfig.Port, _options.ApiKey);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var clusterInfo = await client.GetClusterInfo(linkedCts.Token).WaitAsync(timeoutCts.Token);

            if (clusterInfo.Status.IsSuccess && clusterInfo.Result?.PeerId != null)
            {
                return clusterInfo.Result.PeerId;
            }

            logger.LogWarning("Failed to get cluster info from node {NodeUrl}: {Error}",
                nodeUrl, clusterInfo.Status?.Error ?? MetricConstants.UnknownErrorMessage);
            return null;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Timeout getting peer ID for node {NodeUrl}", nodeUrl);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get peer ID for node {NodeUrl}", nodeUrl);
            return null;
        }
    }

    private async Task<IReadOnlyList<QdrantNodeConfig>> GetNodesFromK8sAsync(CancellationToken cancellationToken)
    {
        if (kubernetes == null)
        {
            return [];
        }

        var currentNamespace = kubernetesManager?.GetCurrentNamespace() ?? KubernetesConstants.DefaultNamespace;

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
                    if (lastDashIndex > 0 && int.TryParse(pod.Metadata.Name.AsSpan(lastDashIndex + 1), out _))
                    {
                        statefulSetName = pod.Metadata.Name[..lastDashIndex];
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

        return nodes;
    }

    private static List<QdrantNodeConfig> GetNodesFromEnvironment()
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

        return nodes;
    }
}
