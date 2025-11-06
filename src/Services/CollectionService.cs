using k8s;
using System.Text;
using System.Net.WebSockets;
using Aer.QdrantClient.Http.Abstractions;
using Vigilante.Models;
using Vigilante.Configuration;
using Microsoft.Extensions.Options;
using Vigilante.Services.Interfaces;
using Vigilante.Extensions;

namespace Vigilante.Services;

public class CollectionService : ICollectionService
{
    private readonly ILogger<CollectionService> _logger;
    private readonly IMeterService _meterService;
    private readonly IKubernetes? _kubernetes;
    private readonly QdrantOptions _options;
    private readonly IQdrantClientFactory _clientFactory;

    private const string PrettySizeMetricKey = "prettySize";
    private const string SizeBytesMetricKey = "sizeBytes";
    private const string ShardsMetricKey = "shards";
    private const string OutgoingTransfersKey = "outgoingTransfers";
    private const string ShardStatesKey = "shardStates";

    public CollectionService(
        ILogger<CollectionService> logger,
        IMeterService meterService,
        IQdrantClientFactory clientFactory,
        IOptions<QdrantOptions> options)
    {
        _logger = logger;
        _meterService = meterService;
        _clientFactory = clientFactory;
        _options = options.Value;

        // Try to initialize Kubernetes client only if we're running in a cluster
        try
        {
            _kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
        }
        catch (k8s.Exceptions.KubeConfigException)
        {
            _logger.LogWarning("Not running in Kubernetes cluster, collection size monitoring will be disabled");
            _kubernetes = null;
        }
    }

    public async Task<bool> ReplicateShardsAsync(
        string healthyNodeUrl,
        ulong sourcePeerId,
        ulong targetPeerId,
        string collectionName,
        uint[] shardIds,
        bool isMove,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(healthyNodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            var result = await qdrantClient.ReplicateShards(
                sourcePeerId: sourcePeerId,
                targetPeerId: targetPeerId,
                collectionNamesToReplicate: new[] { collectionName },
                shardIdsToReplicate: shardIds,
                isMoveShards: isMove,
                cancellationToken: cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("✅ Shard replication initiated: {Collection} [{SourcePeer}→{TargetPeer}]", 
                    collectionName, sourcePeerId, targetPeerId);
                return true;
            }

            _logger.LogError("Failed to replicate shards for {Collection}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to replicate shards for collection {Collection}", collectionName);
            return false;
        }
    }

    public async Task<IEnumerable<CollectionSize>> GetCollectionsSizesForPodAsync(
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
            using var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", "cd /qdrant/storage/collections && ls -1d */" },
                "qdrant",
                cancellationToken: cancellationToken);

            var buffer = new byte[4096];
            var segment = new ArraySegment<byte>(buffer);
            var collectionsOutput = new StringBuilder(512);

            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(segment, cancellationToken);
                if (result.Count > 0)
                {
                    collectionsOutput.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

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

            foreach (var collection in collections)
            {
                try
                {
                        
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

                    var sizeOutput = new StringBuilder(64); // Size output is typically small, pre-allocate
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
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse size for collection {Collection}: '{Output}'",
                            collection, output);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get size for collection {Collection} in pod {PodName}",
                        collection, podName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get collection sizes for pod {PodName}", podName);
        }

        return sizes;
    }

    public IReadOnlyList<CollectionInfo> GenerateTestCollectionData()
    {
        var testData = new List<CollectionInfo>();
        var testCollections = new[] 
        { 
            "test_collection", 
            "products", 
            "embeddings",
            // Long collection names to test UI overflow handling
            "super_long_collection_name_with_multiple_underscores_and_segments_to_test_horizontal_overflow_behavior_even_longer_for_test_purposes",
            "analytics_data_warehouse_user_behavior_tracking_embeddings_v2_production_quantized_optimized_2024"
        };
        var testPeers = new[]
        {
            ("peer1", "pod-1", "http://localhost:6333"),
            ("peer2", "pod-2", "http://localhost:6334"),
            ("peer3", "pod-3", "http://localhost:6335")
        };

        // Define different sizes for different collections to make it more realistic
        var collectionSizes = new Dictionary<string, (string prettySize, long sizeBytes)>
        {
            { "test_collection", ("1.2 GB", 1288490188L) },
            { "products", ("850.5 MB", 891873484L) },
            { "embeddings", ("3.7 GB", 3971891200L) },
            { "super_long_collection_name_with_multiple_underscores_and_segments_to_test_horizontal_overflow_behavior", ("7.3 GB", 7836344320L) },
            { "analytics_data_warehouse_user_behavior_tracking_embeddings_v2_production_quantized_optimized_2024", ("22.1 GB", 23735685734L) }
        };

        foreach (var collection in testCollections)
        {
            var (prettySize, sizeBytes) = collectionSizes.GetValueOrDefault(collection, ("1.0 GB", 1073741824L));
            
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
                    { PrettySizeMetricKey, prettySize },
                    { SizeBytesMetricKey, sizeBytes },
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
            
            // If there are collections, check each one in parallel
            if (collectionsResponse.Result?.Collections != null && collectionsResponse.Result.Collections.Any())
            {
                var collections = collectionsResponse.Result.Collections;
                _logger.LogDebug("Checking health for {CollectionCount} collections in parallel", collections.Length);
                
                // Create tasks for all collection health checks
                var checkTasks = collections.Select(async collection =>
                {
                    var collectionName = collection.Name;
                    
                    _logger.LogDebug("Checking collection info for {CollectionName}", collectionName);
                    var collectionInfo = await client.GetCollectionInfo(collectionName, cancellationToken);
                    
                    if (!collectionInfo.Status.IsSuccess)
                    {
                        var errorDetails = collectionInfo.Status?.Error ?? "Unknown error";
                        _logger.LogWarning("Collections health check failed for {CollectionName}: {Error}", collectionName, errorDetails);
                        return (IsHealthy: false, CollectionName: collectionName, Error: errorDetails);
                    }
                    
                    _logger.LogDebug("Collection {CollectionName} is healthy", collectionName);
                    return (IsHealthy: true, CollectionName: collectionName, Error: (string?)null);
                }).ToArray();
                
                // Wait for all checks to complete
                var results = await Task.WhenAll(checkTasks);
                
                // Check if any collection failed
                var failedCollection = results.FirstOrDefault(r => !r.IsHealthy);
                if (failedCollection.CollectionName != null)
                {
                    return (false, $"Failed to get info for collection '{failedCollection.CollectionName}': {failedCollection.Error}");
                }
                
                _logger.LogDebug("Collections health check passed for all {CollectionCount} collections", collections.Length);
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

    public async Task<bool> DeleteCollectionViaApiAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Deleting collection {CollectionName} via API on node {NodeUrl}", 
                collectionName, nodeUrl);

            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            var result = await qdrantClient.DeleteCollection(collectionName, cancellationToken);

            if (result?.Status?.IsSuccess == true)
            {
                _logger.LogInformation("✅ Collection {CollectionName} deleted successfully via API on node {NodeUrl}", 
                    collectionName, nodeUrl);
                return true;
            }

            _logger.LogError("Failed to delete collection {CollectionName} via API: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete collection {CollectionName} via API on node {NodeUrl}", 
                collectionName, nodeUrl);
            return false;
        }
    }

    public async Task<bool> DeleteCollectionFromDiskAsync(
        string podName,
        string podNamespace,
        string collectionName,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Deleting collection {CollectionName} from disk on pod {PodName} in namespace {Namespace}",
            collectionName, podName, podNamespace);

        if (_kubernetes == null)
        {
            _logger.LogError("Kubernetes client not available, cannot delete collection from disk");
            return false;
        }

        try
        {
            // Execute rm -rf command to delete the collection directory
            // Command: rm -rf /qdrant/storage/collections/{collectionName}
            using var webSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", $"rm -rf /qdrant/storage/collections/{collectionName}" },
                "qdrant",
                cancellationToken: cancellationToken);

            var buffer = new byte[4096];
            var segment = new ArraySegment<byte>(buffer);
            var output = new StringBuilder();

            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(segment, cancellationToken);
                if (result.Count > 0)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

            var outputStr = output.ToString();
            
            // Check if there were any errors in the output
            if (!string.IsNullOrEmpty(outputStr) && 
                (outputStr.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                 outputStr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Failed to delete collection {CollectionName} from disk: {Output}", 
                    collectionName, outputStr);
                return false;
            }

            // Verify deletion by checking if the directory still exists
            using var verifyWebSocket = await _kubernetes.WebSocketNamespacedPodExecAsync(
                podName,
                podNamespace,
                new[] { "sh", "-c", $"test -d /qdrant/storage/collections/{collectionName} && echo 'exists' || echo 'deleted'" },
                "qdrant",
                cancellationToken: cancellationToken);

            var verifyOutput = new StringBuilder();
            do
            {
                result = await verifyWebSocket.ReceiveAsync(segment, cancellationToken);
                if (result.Count > 0)
                {
                    verifyOutput.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            } while (!result.CloseStatus.HasValue && !cancellationToken.IsCancellationRequested);

            var verifyResult = verifyOutput.ToString().Trim();
            
            if (verifyResult.Contains("deleted"))
            {
                _logger.LogInformation("✅ Collection {CollectionName} deleted successfully from disk on pod {PodName}", 
                    collectionName, podName);
                return true;
            }
            else
            {
                _logger.LogError("Collection {CollectionName} still exists after deletion attempt on pod {PodName}", 
                    collectionName, podName);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete collection {CollectionName} from disk on pod {PodName}", 
                collectionName, podName);
            return false;
        }
    }
    public async Task<string?> CreateCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Creating snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.CreateCollectionSnapshot(
                collectionName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var snapshotName = result.Result?.Name ?? $"{collectionName}-snapshot-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var statusText = result.IsAccepted() ? "accepted" : "created successfully";
                _logger.LogInformation("✅ Snapshot {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    statusText, collectionName, nodeUrl);
                return snapshotName;
            }
            
            _logger.LogError("Failed to create snapshot for collection {CollectionName}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create snapshot for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return null;
        }
    }
    public async Task<List<string>> ListCollectionSnapshotsAsync(
        string nodeUrl,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Listing snapshots for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.ListCollectionSnapshots(collectionName, cancellationToken);
            if (result?.Status?.IsSuccess == true && result.Result != null)
            {
                var snapshots = result.Result.Select(s => s.Name).ToList();
                _logger.LogDebug("Found {Count} snapshots for collection {CollectionName} on node {NodeUrl}", 
                    snapshots.Count, collectionName, nodeUrl);
                return snapshots;
            }
            _logger.LogWarning("Failed to list snapshots for collection {CollectionName}: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list snapshots for collection {CollectionName} on node {NodeUrl}", 
                collectionName, nodeUrl);
            return new List<string>();
        }
    }
    public async Task<bool> DeleteCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Deleting snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.DeleteCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "deletion accepted" : "deleted successfully";
                _logger.LogInformation("✅ Snapshot {SnapshotName} {StatusText} for collection {CollectionName} on node {NodeUrl}", 
                    snapshotName, statusText, collectionName, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to delete snapshot {SnapshotName} for collection {CollectionName}: {Error}",
                snapshotName, collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete snapshot {SnapshotName} for collection {CollectionName} on node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            return false;
        }
    }
    public async Task<Stream?> DownloadCollectionSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Downloading snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            
            var result = await qdrantClient.DownloadCollectionSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken);
            
            if (result?.Result?.SnapshotDataStream != null)
            {
                _logger.LogInformation("✅ Snapshot {SnapshotName} downloaded successfully for collection {CollectionName} from node {NodeUrl}", 
                    snapshotName, collectionName, nodeUrl);
                return result.Result.SnapshotDataStream;
            }
            
            _logger.LogError("Failed to download snapshot {SnapshotName} for collection {CollectionName}: empty or null result",
                snapshotName, collectionName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download snapshot {SnapshotName} for collection {CollectionName} from node {NodeUrl}", 
                snapshotName, collectionName, nodeUrl);
            return null;
        }
    }
    public async Task<bool> RecoverCollectionFromSnapshotAsync(
        string nodeUrl,
        string collectionName,
        string snapshotName,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}", 
                collectionName, snapshotName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            var result = await qdrantClient.RecoverCollectionFromSnapshot(
                collectionName, 
                snapshotName, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "recovery accepted" : "recovered successfully";
                _logger.LogInformation("✅ Collection {CollectionName} {StatusText} from snapshot {SnapshotName} on node {NodeUrl}", 
                    collectionName, statusText, snapshotName, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to recover collection {CollectionName} from snapshot {SnapshotName}: {Error}",
                collectionName, snapshotName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover collection {CollectionName} from snapshot {SnapshotName} on node {NodeUrl}", 
                collectionName, snapshotName, nodeUrl);
            return false;
        }
    }
    public async Task<bool> RecoverCollectionFromUploadedSnapshotAsync(
        string nodeUrl,
        string collectionName,
        Stream snapshotData,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Recovering collection {CollectionName} from uploaded snapshot on node {NodeUrl}", 
                collectionName, nodeUrl);
            var uri = new Uri(nodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
            
            var result = await qdrantClient.RecoverCollectionFromUploadedSnapshot(
                collectionName, 
                snapshotData, 
                cancellationToken,
                isWaitForResult: false);
            
            if (result.IsAcceptedOrSuccess())
            {
                var statusText = result.IsAccepted() ? "recovery accepted" : "recovered successfully";
                _logger.LogInformation("✅ Collection {CollectionName} {StatusText} from uploaded snapshot on node {NodeUrl}", 
                    collectionName, statusText, nodeUrl);
                return true;
            }
            
            _logger.LogError("Failed to recover collection {CollectionName} from uploaded snapshot: {Error}",
                collectionName, result?.Status?.Error ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover collection {CollectionName} from uploaded snapshot on node {NodeUrl}",
                collectionName, nodeUrl);
            return false;
        }
    }

    public async Task EnrichWithClusteringInfoAsync(
        string healthyNodeUrl,
        IList<CollectionInfo> collectionInfos,
        Dictionary<string, string> peerToPodMap,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(healthyNodeUrl);
            var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);

            // Find the peer ID of the node we're querying
            var healthyNodePeerId = collectionInfos
                .FirstOrDefault(c => c.NodeUrl == healthyNodeUrl)?.PeerId;
            
            if (string.IsNullOrEmpty(healthyNodePeerId))
            {
                _logger.LogWarning("Could not find peer ID for node {NodeUrl}", healthyNodeUrl);
                return;
            }

            // Get unique collection names for this specific node
            var collectionNames = collectionInfos
                .Where(c => c.NodeUrl == healthyNodeUrl)
                .Select(c => c.CollectionName)
                .Distinct();

            foreach (var collectionName in collectionNames)
            {
                try
                {
                    var clusteringInfo = await qdrantClient.GetCollectionClusteringInfo(collectionName, cancellationToken);
                    if (clusteringInfo?.Status?.IsSuccess == true && clusteringInfo.Result != null)
                    {
                        // Update metrics only for this node's collection
                        var info = collectionInfos.FirstOrDefault(c => 
                            c.CollectionName == collectionName && c.NodeUrl == healthyNodeUrl);
                        
                        if (info == null)
                            continue;

                        var shards = new List<ulong>();
                        var shardStates = new Dictionary<string, string>();

                        // Use local shards for this node
                        if (clusteringInfo.Result.LocalShards != null)
                        {
                            foreach (var shard in clusteringInfo.Result.LocalShards)
                            {
                                shards.Add(shard.ShardId);
                                shardStates[shard.ShardId.ToString()] = shard.State.ToString();
                            }
                        }

                        if (shards.Count != 0)
                        {
                            info.Metrics["shards"] = shards;
                            info.Metrics["shardStates"] = shardStates;
                        }

                        // Add outgoing transfers information with pod names instead of PeerIds
                        if (clusteringInfo.Result.ShardTransfers != null)
                        {
                            var outgoingTransfers = clusteringInfo.Result.ShardTransfers
                                .Where(t => t.From.ToString() == info.PeerId)
                                .Select(t => new
                                {
                                    t.ShardId,
                                    To = peerToPodMap.TryGetValue(t.To.ToString(), out var podName) ? podName : t.To.ToString(),
                                    ToPeerId = t.To.ToString(),
                                    IsSync = t.Sync
                                })
                                .ToList();

                            if (outgoingTransfers.Count != 0)
                            {
                                info.Metrics["outgoingTransfers"] = outgoingTransfers;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get clustering info for collection {Collection} on node {NodeUrl}", 
                        collectionName, healthyNodeUrl);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup QdrantClient for node {NodeUrl}", healthyNodeUrl);
        }
    }

    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsFromQdrantAsync(
        IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)> nodes,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting collections from Qdrant API (Kubernetes storage not available)");
        
        var result = new List<CollectionInfo>();
        
        foreach (var node in nodes)
        {
            try
            {
                _logger.LogDebug("Getting collections from node {NodeUrl}", node.Url);
                
                var uri = new Uri(node.Url);
                var qdrantClient = _clientFactory.CreateClient(uri.Host, uri.Port, _options.ApiKey);
                
                // Get list of collections
                var collectionsResponse = await qdrantClient.ListCollections(cancellationToken);
                if (!collectionsResponse.Status.IsSuccess || collectionsResponse.Result?.Collections == null)
                {
                    _logger.LogWarning("Failed to get collections from node {NodeUrl}: {Error}", 
                        node.Url, collectionsResponse.Status?.Error ?? "Unknown error");
                    continue;
                }
                
                // For each collection, get its info
                foreach (var collection in collectionsResponse.Result.Collections)
                {
                    try
                    {
                        var collectionName = collection.Name;
                        
                        // Get snapshots for this collection
                        var snapshots = await ListCollectionSnapshotsAsync(node.Url, collectionName, cancellationToken);
                        
                        var metrics = new Dictionary<string, object>
                        {
                            { "prettySize", "N/A" },
                            { "sizeBytes", 0L },
                            { "snapshots", snapshots }
                        };

                        result.Add(new CollectionInfo
                        {
                            CollectionName = collectionName,
                            NodeUrl = node.Url,
                            PodName = node.PodName ?? "unknown",
                            PeerId = node.PeerId,
                            PodNamespace = node.Namespace ?? "",
                            Metrics = metrics
                        });
                        
                        _logger.LogDebug("Added collection {CollectionName} from node {NodeUrl}", collectionName, node.Url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get info for collection {CollectionName} from node {NodeUrl}", 
                            collection.Name, node.Url);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get collections from node {NodeUrl}", node.Url);
            }
        }
        
        _logger.LogInformation("Retrieved {Count} collections from Qdrant API", result.Count);
        return result;
    }
}
