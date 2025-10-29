using k8s;
using System.Text;
using System.Net.WebSockets;
using Aer.QdrantClient.Http;
using Aer.QdrantClient.Http.Abstractions;
using Vigilante.Models;
using Vigilante.Configuration;
using Microsoft.Extensions.Options;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

public class CollectionService : ICollectionService
{
    private readonly Lazy<ClusterManager> _clusterManager;
    private readonly ILogger<CollectionService> _logger;
    private readonly IMeterService _meterService;
    private readonly IKubernetes? _kubernetes;
    private readonly QdrantOptions _options;

    private const string PrettySizeMetricKey = "prettySize";
    private const string SizeBytesMetricKey = "sizeBytes";
    private const string ShardsMetricKey = "shards";
    private const string OutgoingTransfersKey = "outgoingTransfers";
    private const string ShardStatesKey = "shardStates";

    public CollectionService(
        Lazy<ClusterManager> clusterManager,
        ILogger<CollectionService> logger,
        IMeterService meterService,
        IOptions<QdrantOptions> options)
    {
        _clusterManager = clusterManager;
        _logger = logger;
        _meterService = meterService;
        _options = options.Value;

        // Try to initialize Kubernetes client only if we're running in a cluster
        try
        {
            _kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
            _logger.LogInformation("Kubernetes client initialized successfully");
        }
        catch (k8s.Exceptions.KubeConfigException)
        {
            _logger.LogInformation("Not running in Kubernetes cluster, collection size monitoring will be disabled");
            _kubernetes = null;
        }
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsInfoAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting GetCollectionsSizesAsync");

        if (_kubernetes == null)
        {
            _logger.LogDebug("Running in local mode, returning test data");
            return GenerateTestCollectionData();
        }

        var result = new List<CollectionInfo>();
        _logger.LogInformation("Requesting cluster state from ClusterManager...");
        var state = await _clusterManager.Value.GetClusterStateAsync(cancellationToken);
        
        // Create mapping of PeerId to podName
        var peerToPodMap = state.Nodes
            .Where(n => !string.IsNullOrEmpty(n.PeerId) && !string.IsNullOrEmpty(n.PodName))
            .ToDictionary(n => n.PeerId, n => n.PodName);
            
        var nodes = state.Nodes;
        _logger.LogInformation("Found {NodesCount} nodes to process. Healthy nodes: {HealthyCount}", 
            nodes.Count, nodes.Count(n => n.IsHealthy));
            
        // First, get all collections and their sizes from all nodes
        foreach (var node in nodes)
        {
            _logger.LogInformation(
                "Processing node: URL={NodeUrl}, PeerId={PeerId}, IsHealthy={IsHealthy}, Namespace={Namespace}", 
                node.Url, node.PeerId, node.IsHealthy, node.Namespace);
            try
            {
                var podName = await GetPodNameFromIpAsync(node.Url, node.Namespace ?? "", cancellationToken);
                if (string.IsNullOrEmpty(podName))
                {
                    _logger.LogWarning("Could not find pod for IP {NodeUrl} in namespace {Namespace}", node.Url,
                        node.Namespace);
                    continue;
                }

                _logger.LogInformation("Found pod {PodName} for IP {NodeUrl}", podName, node.Url);
                
                var collectionSizes =
                    await GetCollectionsSizesForPodAsync(podName, node.Namespace ?? "", node.Url, node.PeerId, cancellationToken);
                var sizesList = collectionSizes.ToList();
                _logger.LogInformation("Retrieved {SizesCount} collection sizes from pod {PodName}", sizesList.Count,
                    podName);

                foreach (var size in sizesList)
                {
                    var metrics = new Dictionary<string, object>
                    {
                        { PrettySizeMetricKey, size.PrettySize },
                        { SizeBytesMetricKey, size.SizeBytes }
                    };

                    result.Add(new CollectionInfo
                    {
                        CollectionName = size.CollectionName,
                        NodeUrl = size.NodeUrl,
                        PodName = size.PodName,
                        PeerId = size.PeerId,
                        Metrics = metrics
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collection sizes for node {nodeUrl}", node.Url);
            }
        }
        
        // Then, get sharding information from a healthy node
        var healthyNode = nodes.FirstOrDefault(n => n.IsHealthy);
        if (healthyNode != null)
        {
            try
            {
                var uri = new Uri(healthyNode.Url);
                var qdrantClient = string.IsNullOrEmpty(_options.ApiKey)
                    ? new QdrantHttpClient(uri.Host, uri.Port)
                    : new QdrantHttpClient(uri.Host, uri.Port, apiKey: _options.ApiKey);

                // Group collections by name to avoid duplicates
                var collections = result
                    .Select(r => r.CollectionName)
                    .Distinct();

                foreach (var collectionName in collections)
                {
                    try
                    {
                        var clusteringInfo = await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);
                        if (clusteringInfo?.Status?.IsSuccess == true && clusteringInfo.Result != null)
                        {
                            // Update metrics for each node's collection
                            var collectionInfos = result.Where(r => r.CollectionName == collectionName);
                            foreach (var info in collectionInfos)
                            {
                                var shards = new List<ulong>();
                                var shardStates = new Dictionary<string, string>();

                                // If this is the node we made request from, use local shards
                                if (healthyNode.PeerId == info.PeerId)
                                {
                                    if (clusteringInfo.Result.LocalShards != null)
                                    {
                                        foreach (var shard in clusteringInfo.Result.LocalShards)
                                        {
                                            shards.Add(shard.ShardId);
                                            shardStates[shard.ShardId.ToString()] = shard.State.ToString();
                                        }
                                    }
                                }
                                else if (clusteringInfo.Result.RemoteShards != null)
                                {
                                    // Find shards for this node in remote shards
                                    foreach (var remoteShard in clusteringInfo.Result.RemoteShards)
                                    {
                                        if (remoteShard.PeerId.ToString() == info.PeerId)
                                        {
                                            shards.Add(remoteShard.ShardId);
                                            shardStates[remoteShard.ShardId.ToString()] = remoteShard.State.ToString();
                                        }
                                    }
                                }

                                if (shards.Any())
                                {
                                    info.Metrics[ShardsMetricKey] = shards;
                                    info.Metrics[ShardStatesKey] = shardStates;
                                }

                                // Add outgoing transfers information with pod names instead of PeerIds
                                if (clusteringInfo.Result.ShardTransfers != null)
                                {
                                    var outgoingTransfers = clusteringInfo.Result.ShardTransfers
                                        .Where(t => t.From.ToString() == info.PeerId)
                                        .Select(t => new
                                        {
                                            ShardId = t.ShardId,
                                            To = peerToPodMap.TryGetValue(t.To.ToString(), out var podName) ? podName : t.To.ToString(),
                                            IsSync = t.Sync
                                        })
                                        .ToList();

                                    if (outgoingTransfers.Any())
                                    {
                                        info.Metrics[OutgoingTransfersKey] = outgoingTransfers;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to get clustering info for collection {Collection} from node {NodeUrl}", 
                            collectionName, healthyNode.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to setup QdrantClient for node {NodeUrl}", healthyNode.Url);
            }
        }
        else
        {
            _logger.LogWarning("No healthy nodes found, skipping sharding information collection");
        }

        _logger.LogInformation("Completed GetCollectionsSizesAsync, found {TotalCollections} collections in total",
            result.Count);

        return result;
    }
    
    public async Task<bool> ReplicateShardsAsync(
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting shard replication. Source: {SourcePeerId}, Target: {TargetPeerId}, Collection: {Collection}, " +
            "Shards: {ShardIds}, Move: {IsMove}",
            sourcePeerId, targetPeerId, collectionName, string.Join(", ", shardIds), isMove);

        var state = await _clusterManager.Value.GetClusterStateAsync(cancellationToken);
        var healthyNode = state.Nodes.FirstOrDefault(n => n.IsHealthy);
        
        if (healthyNode == null)
        {
            _logger.LogError("No healthy nodes found to perform replication");
            return false;
        }

        try
        {
            var uri = new Uri(healthyNode.Url);
            var qdrantClient = string.IsNullOrEmpty(_options.ApiKey)
                ? new QdrantHttpClient(uri.Host, uri.Port)
                : new QdrantHttpClient(uri.Host, uri.Port, apiKey: _options.ApiKey);

            var result = await qdrantClient.ReplicateShards(
                sourcePeerId: sourcePeerId,
                targetPeerId: targetPeerId,
                collectionNamesToReplicate: new[] { collectionName },
                shardIdsToReplicate: shardIds,
                isMoveShards: isMove,
                cancellationToken: cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation(
                    "Successfully initiated shard replication for collection {Collection}", collectionName);
                return true;
            }

            _logger.LogError(
                "Failed to replicate shards. Status: {Status}, Error: {Error}",
                result?.Status?.ToString() ?? "Unknown",
                result?.Status?.Error ?? "No error details");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to replicate shards for collection {Collection}", collectionName);
            return false;
        }
    }

    private async Task<string?> GetPodNameFromIpAsync(string podUrl, string podNamespace,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(podUrl);
        var podIp = uri.Host;
        
        _logger.LogInformation("Getting pod name for IP {PodIp} in namespace {Namespace}", podIp, podNamespace);

        if (_kubernetes == null)
        {
            return null;
        }

        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
            namespaceParameter: podNamespace,
            fieldSelector: $"status.podIP=={podIp}", // Fix: changed podIp= to status.podIP==
            cancellationToken: cancellationToken);

        _logger.LogInformation("Found {PodsCount} pods matching IP {PodIp}", pods.Items.Count, podIp);

        return pods.Items.FirstOrDefault()?.Metadata.Name;
    }

    private async Task<IEnumerable<CollectionSize>> GetCollectionsSizesForPodAsync(
        string podName,
        string podNamespace,
        string nodeUrl,
        string peerId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting to get collection sizes for pod {PodName} (Node URL {NodeUrl}) in namespace {Namespace}",
            podName, nodeUrl, podNamespace);

        if (_kubernetes == null)
        {
            return [];
        }

        var sizes = new List<CollectionSize>();

        try
        {
            _logger.LogInformation("Executing WebSocket command to list collections in pod {PodName}", podName);
            
            // Command breakdown: ["sh", "-c", "cd /qdrant/storage/collections && ls -1d */"]
            // - "sh": Use the Bourne shell to execute commands
            // - "-c": Execute the following string as a command
            // - "cd /qdrant/storage/collections": Change directory to Qdrant collections storage
            // - "&&": Execute the next command only if the previous one succeeds
            // - "ls": List directory contents
            // - "-1": List one file per line (single-column output)
            // - "-d": List directories themselves, not their contents
            // - "*/": Match all directories (the trailing slash ensures only directories are matched)
            using var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", "cd /qdrant/storage/collections && ls -1d */" },
                "qdrant",
                cancellationToken: cancellationToken);

            _logger.LogDebug("WebSocket connection established for pod {PodName}", podName);
            var collectionsOutput = new StringBuilder();
            var buffer = new byte[4096];
            var segment = new ArraySegment<byte>(buffer);

            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(segment, cancellationToken);
                if (result.Count > 0)
                {
                    var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogDebug("Received WebSocket data: {ReceivedText}", receivedText);
                    collectionsOutput.Append(receivedText);
                }
            } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

            _logger.LogDebug("Raw collections output: {RawOutput}", collectionsOutput.ToString());

            var collections = collectionsOutput.ToString()
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name
                    .TrimEnd('/')
                    .Trim()
                    .Where(c => !char.IsControl(c))
                    .ToArray())
                .Select(chars => new string(chars))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("."))
                .ToList();

            _logger.LogInformation("Found {CollectionsCount} collections in pod {PodName}: {Collections}",
                collections.Count, podName, string.Join(", ", collections));

            foreach (var collection in collections)
            {
                try
                {
                    _logger.LogInformation("Getting size for collection {Collection} in pod {PodName}", collection,
                        podName);
                        
                    // Command breakdown: ["sh", "-c", "cd /qdrant/storage/collections && du -sb \"collection\" | cut -f1"]
                    // - "sh": Use the Bourne shell to execute commands
                    // - "-c": Execute the following string as a command
                    // - "cd /qdrant/storage/collections": Change directory to Qdrant collections storage
                    // - "&&": Execute the next command only if the previous one succeeds
                    // - "du": Disk usage command
                    // - "-s": Summary (don't recursively show nested directories)
                    // - "-b": Show size in bytes (instead of blocks)
                    // - "\"collection\"": The collection directory name (quoted to handle special characters)
                    // - "|": Pipe the output to the next command
                    // - "cut -f1": Extract only the first field (the size in bytes, excluding the path)
                    using var sizeWebSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                        podName,
                        podNamespace,
                        new[] { "sh", "-c", $"cd /qdrant/storage/collections && du -sb \"{collection}\" | cut -f1" },
                        "qdrant",
                        cancellationToken: cancellationToken);

                    var sizeOutput = new StringBuilder();
                    do
                    {
                        result = await sizeWebSocket.ReceiveAsync(segment, cancellationToken);
                        if (result.Count > 0)
                        {
                            var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            _logger.LogDebug("Received size data for {Collection}: {ReceivedText}", collection,
                                receivedText);
                            sizeOutput.Append(receivedText);
                        }
                    } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

                    var output = sizeOutput.ToString()
                        .Trim()
                        .Replace("\n", "")
                        .Replace("\r", "")
                        .Replace("\0", "");
                    _logger.LogDebug("Raw size output for collection {Collection}: '{Output}'", collection, output);

                    // Remove any non-digit characters
                    var cleanedOutput = new string(output.Where(c => char.IsDigit(c)).ToArray());
                    if (!string.IsNullOrEmpty(cleanedOutput) && long.TryParse(cleanedOutput, out var sizeBytes))
                    {
                        var collectionSize = new CollectionSize
                        {
                            PodName = podName,
                            NodeUrl = nodeUrl,
                            PeerId = peerId,
                            CollectionName = collection,
                            SizeBytes = sizeBytes
                        };

                        sizes.Add(collectionSize);
                        _meterService.UpdateCollectionSize(collectionSize);
                        _logger.LogInformation(
                            "Successfully got size for collection {Collection} in pod {PodName} (Node URL {NodeUrl}): {Size} bytes",
                            collection, podName, nodeUrl, sizeBytes);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Failed to parse size from output for collection {Collection}. Raw output: '{Output}'",
                            collection, output);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to get size for collection {Collection} in pod {PodName} (Node {NodeId})",
                        collection, podName, nodeUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection sizes for pod {PodName} (Node URL {NodeUrl})", podName,
                nodeUrl);
        }

        _logger.LogInformation(
            "Completed getting sizes for pod {PodName} (Node URL {NodeUrl}). Found {SizesCount} collection sizes",
            podName, nodeUrl, sizes.Count);

        return sizes;
    }

    private IReadOnlyList<CollectionInfo> GenerateTestCollectionData()
    {
        var testData = new List<CollectionInfo>();
        var testCollections = new[] { "test_collection", "products", "embeddings" };
        var testPeers = new[]
        {
            ("peer1", "pod-1", "http://localhost:6333"),
            ("peer2", "pod-2", "http://localhost:6334"),
            ("peer3", "pod-3", "http://localhost:6335")
        };

        foreach (var collection in testCollections)
        {
            foreach (var (peerId, podName, url) in testPeers)
            {
                var shards = new List<int>();
                var transfers = new List<object>();
                var shardStates = new Dictionary<string, string>();

                // Distribute shards among peers with different states
                if (peerId == "peer1")
                {
                    shards.AddRange(new[] { 0, 1, 2 });
                    transfers.Add(new { isSync = true, shardId = 2, to = "pod-2", toPeerId = "peer2" });
                    
                    // States for the first peer
                    shardStates["0"] = "Active";          // Active shard
                    shardStates["1"] = "Initializing";    // Being initialized
                    shardStates["2"] = "PartialSnapshot"; // Being transferred
                }
                else if (peerId == "peer2")
                {
                    shards.AddRange(new[] { 3, 4, 5 });
                    
                    // States for the second peer
                    shardStates["3"] = "Listener";        // In listener mode
                    shardStates["4"] = "Dead";           // Inaccessible
                    shardStates["5"] = "Recovery";       // Being recovered
                }
                else if (peerId == "peer3")
                {
                    shards.AddRange(new[] { 6, 7, 8 });
                    transfers.Add(new { isSync = false, shardId = 8, to = "pod-1", toPeerId = "peer1" });
                    
                    // States for the third peer
                    shardStates["6"] = "Resharding";             // Being resharded
                    shardStates["7"] = "ReshardingScaleDown";   // Being scaled down
                    shardStates["8"] = "Partial";               // Partially available
                }

                var metrics = new Dictionary<string, object>
                {
                    { PrettySizeMetricKey, "1.2GB" },
                    { SizeBytesMetricKey, 1288490188L }, // 1.2GB в байтах
                    { ShardsMetricKey, shards },
                    { OutgoingTransfersKey, transfers },
                    { ShardStatesKey, shardStates }
                };

                testData.Add(new CollectionInfo
                {
                    CollectionName = collection,
                    PodName = podName,
                    PeerId = peerId,
                    Metrics = metrics
                });
            }
        }

        return testData;
    }

    public async Task<(bool IsHealthy, string? ErrorMessage)> CheckCollectionsHealthAsync(IQdrantHttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Checking collections health");
            var collectionsResponse = await client.ListCollections(cancellationToken);
            
            if (!collectionsResponse.Status.IsSuccess)
            {
                var errorDetails = collectionsResponse.Status?.Error ?? "Unknown error";
                _logger.LogWarning("Collections health check failed: {Error}", errorDetails);
                return (false, $"Failed to list collections: {errorDetails}");
            }
            
            // If there are collections, also try to get info about the first one
            if (collectionsResponse.Result?.Collections != null && collectionsResponse.Result.Collections.Any())
            {
                var firstCollection = collectionsResponse.Result.Collections.First();
                var collectionName = firstCollection.Name;
                
                _logger.LogDebug("Checking collection info for {CollectionName}", collectionName);
                var collectionInfo = await client.GetCollectionInfo(collectionName, cancellationToken);
                
                if (!collectionInfo.Status.IsSuccess)
                {
                    var errorDetails = collectionInfo.Status?.Error ?? "Unknown error";
                    _logger.LogWarning("Collections health check failed for {CollectionName}: {Error}", collectionName, errorDetails);
                    return (false, $"Failed to get info for collection '{collectionName}': {errorDetails}");
                }
                
                _logger.LogDebug("Collections health check passed including collection info for {CollectionName}", collectionName);
            }
            else
            {
                _logger.LogDebug("Collections health check passed (no collections to verify)");
            }
            
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Collections health check failed with exception");
            return (false, $"Exception during collections check: {ex.Message}");
        }
    }
}
