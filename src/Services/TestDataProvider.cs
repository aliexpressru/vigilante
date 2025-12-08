using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Models;

namespace Vigilante.Services;

/// <summary>
/// Provides test data for local development when Kubernetes is not available
/// </summary>
public class TestDataProvider
{


    private readonly QdrantOptions _options;

    public TestDataProvider(IOptions<QdrantOptions> options)
    {
        _options = options.Value;
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
        
        // Generate test peers from actual Qdrant configuration
        var testPeers = _options.Nodes.Select((node, index) => 
            (
                peerId: $"peer{index + 1}",
                podName: $"qdrant-{index}",
                url: $"http://{node.Host}:{node.Port}"
            )
        ).ToList();
        
        // If no nodes configured, use defaults
        if (testPeers.Count == 0)
        {
            testPeers = new List<(string peerId, string podName, string url)>
            {
                ("peer1", "qdrant-0", "http://localhost:6333"),
                ("peer2", "qdrant-1", "http://localhost:6334"),
                ("peer3", "qdrant-2", "http://localhost:6335")
            };
        }

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
            
            foreach (var (peerId, podName, _) in testPeers)
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
                    { MetricConstants.PrettySizeKey, prettySize },
                    { MetricConstants.SizeBytesKey, sizeBytes },
                    { MetricConstants.ShardsKey, shards },
                    { MetricConstants.OutgoingTransfersKey, transfers },
                    { MetricConstants.ShardStatesKey, shardStates }
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

    public IReadOnlyList<SnapshotInfo> GenerateTestSnapshotData()
    {
        var testData = new List<SnapshotInfo>();

        // Collections that have snapshots with different snapshot names per node
        var testCollections = new[]
        {
            ("test_collection", 530000000L),  // ~500 MB per node
            ("products", 310000000L),          // ~300 MB per node
            ("embeddings", 1600000000L),       // ~1.5 GB per node
        };

        // Generate test peers from actual Qdrant configuration
        var testPeers = _options.Nodes.Select((node, index) =>
            (
                peerId: $"peer{index + 1}",
                podName: $"qdrant-{index}",
                url: $"http://{node.Host}:{node.Port}",
                index
            )
        ).ToList();

        // If no nodes configured, use defaults
        if (testPeers.Count == 0)
        {
            testPeers = new List<(string peerId, string podName, string url, int index)>
            {
                ("peer1", "qdrant-0", "http://localhost:6333", 0),
                ("peer2", "qdrant-1", "http://localhost:6334", 1),
                ("peer3", "qdrant-2", "http://localhost:6335", 2)
            };
        }

        // Generate snapshots with unique names per node for each collection
        // Real snapshot IDs from production mapped to specific node hosts
        var realSnapshotIdsPerNode = new Dictionary<string, Dictionary<string, string>>();
        // var realSnapshotIdsPerNode = new Dictionary<string, Dictionary<string, string>>
        // {
        //     {
        //         "collection_name",
        //         new Dictionary<string, string>
        //         {
        //             { "ip1", "375902039176772" },
        //             { "ip2", "3372865182647577" },
        //             { "ip3", "3746963919532098" }
        //         }
        //     }
        // };

        foreach (var (collectionName, baseSizeBytes) in testCollections)
        {
            foreach (var (peerId, podName, url, index) in testPeers)
            {
                // Extract host from URL (format: http://host:port)
                var nodeHost = url.Replace("http://", "").Replace("https://", "").Split(':')[0];

                // Use real snapshot IDs mapped to specific nodes if available
                string uniqueId;
                if (realSnapshotIdsPerNode.TryGetValue(collectionName, out var nodeMapping)
                    && nodeMapping.TryGetValue(nodeHost, out var mappedId))
                {
                    uniqueId = mappedId;
                }
                else
                {
                    // Generate synthetic ID for other collections or unknown nodes
                    uniqueId = (375902039176772L + index * 123456789L).ToString();
                }

                var timestamp = "2025-11-06-08-41-36";
                var snapshotName = $"{collectionName}-{uniqueId}-{timestamp}.snapshot";

                // Vary size slightly per node
                var sizeVariation = index * 10000000L; // 10MB variation per node
                var sizeBytes = baseSizeBytes + sizeVariation;

                testData.Add(new SnapshotInfo
                {
                    CollectionName = collectionName,
                    SnapshotName = snapshotName,
                    PodName = podName,
                    PeerId = peerId,
                    NodeUrl = url,
                    SizeBytes = sizeBytes
                });
            }
        }

        return testData;
    }
}

