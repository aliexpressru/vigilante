using System.Reflection;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Requests.Public;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Responses.Shared;
using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

/// <summary>
/// Tests for CollectionService snapshot retrieval functionality.
/// Uses mocked IPodCommandExecutor to validate business logic.
/// Low-level command parsing and WebSocket handling is covered by PodCommandExecutorTests.
/// </summary>
[TestFixture]
public class CollectionServiceTests
{
    private ILogger<CollectionService> _logger = null!;
    private ILogger<PodCommandExecutor> _commandExecutorLogger = null!;
    private IMeterService _meterService = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private IOptions<QdrantOptions> _options = null!;
    private IPodCommandExecutor _mockCommandExecutor = null!;

    [SetUp]
    public void Setup()
    {
        _logger = Substitute.For<ILogger<CollectionService>>();
        _commandExecutorLogger = Substitute.For<ILogger<PodCommandExecutor>>();
        _meterService = Substitute.For<IMeterService>();
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _options = Substitute.For<IOptions<QdrantOptions>>();
        _mockCommandExecutor = Substitute.For<IPodCommandExecutor>();
        
        _options.Value.Returns(new QdrantOptions
        {
            HttpTimeoutSeconds = 5,
            ApiKey = "test-key",
            Nodes = new List<QdrantNodeConfig>()
        });
    }
    
    /// <summary>
    /// Helper method to setup GetCollectionInfo mock for tests - works for any collection name
    /// </summary>
    private void SetupGetCollectionInfoMock(IQdrantHttpClient mockClient)
    {
        mockClient.GetCollectionInfo(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>())
            .Returns(callInfo => new GetCollectionInfoResponse
            {
                Result = new GetCollectionInfoResponse.CollectionInfo
                {
                    Status = QdrantCollectionStatus.Green
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            });
    }

    #region GetCollectionsSizesForPodAsync Tests

    [Test]
    public async Task GetCollectionsSizesForPodAsync_ShouldReturnSizes_WhenCommandExecutorAvailable()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://test-node:6333";
        var peerId = "peer1";

        var collections = new List<string> { "collection1", "collection2" };
        
        _mockCommandExecutor.ListDirectoriesAsync(podName, podNamespace, "/qdrant/storage/collections", Arg.Any<CancellationToken>())
            .Returns(collections);
        
        _mockCommandExecutor.GetSizeAsync(podName, podNamespace, "/qdrant/storage/collections", "collection1", Arg.Any<CancellationToken>())
            .Returns(1000L);
        
        _mockCommandExecutor.GetSizeAsync(podName, podNamespace, "/qdrant/storage/collections", "collection2", Arg.Any<CancellationToken>())
            .Returns(2000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetCollectionsSizesForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var sizes = result.ToList();
        Assert.That(sizes, Has.Count.EqualTo(2));
        Assert.That(sizes[0].CollectionName, Is.EqualTo("collection1"));
        Assert.That(sizes[0].SizeBytes, Is.EqualTo(1000));
        Assert.That(sizes[1].CollectionName, Is.EqualTo("collection2"));
        Assert.That(sizes[1].SizeBytes, Is.EqualTo(2000));
    }

    [Test]
    public async Task GetCollectionsSizesForPodAsync_ShouldHandleNullSize()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://test-node:6333";
        var peerId = "peer1";

        var collections = new List<string> { "collection1" };
        
        _mockCommandExecutor.ListDirectoriesAsync(podName, podNamespace, "/qdrant/storage/collections", Arg.Any<CancellationToken>())
            .Returns(collections);
        
        _mockCommandExecutor.GetSizeAsync(podName, podNamespace, "/qdrant/storage/collections", "collection1", Arg.Any<CancellationToken>())
            .Returns((long?)null);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetCollectionsSizesForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region DeleteCollectionFromDiskAsync Tests

    [Test]
    public async Task DeleteCollectionFromDiskAsync_ShouldCallDeleteAndVerify()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var collectionName = "test-collection";

        _mockCommandExecutor.DeleteAndVerifyAsync(
                podName, 
                podNamespace, 
                "/qdrant/storage/collections/test-collection", 
                true, 
                "Collection test-collection", 
                Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.DeleteCollectionFromDiskAsync(podName, podNamespace, collectionName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
        await _mockCommandExecutor.Received(1).DeleteAndVerifyAsync(
            podName, 
            podNamespace, 
            "/qdrant/storage/collections/test-collection", 
            true, 
            "Collection test-collection", 
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region GetEnrichedCollectionsInfoAsync Tests

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithNoHealthyNodes_ReturnsEmptyList()
    {
        // Arrange
        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = false }
        };
        
        var peerToPodMap = new Dictionary<string, string>();

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithHealthyNodes_FiltersOnlyHealthyOnes()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1" },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = false, PodName = "pod2" }
        };
        
        var peerToPodMap = new Dictionary<string, string>
        {
            { "1001", "pod1" },
            { "1002", "pod2" }
        };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CollectionName, Is.EqualTo("test_collection"));
        Assert.That(result[0].NodeUrl, Is.EqualTo("http://node1:6333"));
        
        // Verify only healthy node was queried
        await mockClient.Received(1).ListCollections(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithStorageData_EnrichesMetrics()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "test_collection" });
        
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "test_collection", Arg.Any<CancellationToken>())
            .Returns(1073741824L); // 1 GB

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1", Namespace = "default" }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Metrics.SizeBytes, Is.EqualTo(1073741824L));
        Assert.That(result[0].Metrics.PrettySize, Is.EqualTo("1 GB"));
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WhenCollectionNotInStorage_AddsIssue()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>()); // Empty - no collections in storage

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1", Namespace = "default" }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Issues, Has.Count.EqualTo(1));
        Assert.That(result[0].Issues[0], Is.EqualTo("Collection exists in API but not found in storage"));
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithoutPodNames_SkipsStorageEnrichment()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = null } // No pod name
        };
        
        var peerToPodMap = new Dictionary<string, string>();

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Metrics.PrettySize, Is.EqualTo("N/A")); // Not enriched
        Assert.That(result[0].Metrics.SizeBytes, Is.EqualTo(0L)); // Not enriched
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithAliases_PopulatesAliasesField()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("products")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = new[]
                {
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "test",
                        CollectionName = "test_collection"
                    },
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "test_alias",
                        CollectionName = "test_collection"
                    },
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "prod",
                        CollectionName = "products"
                    }
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>())
            .Returns(aliasesResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1" }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        
        var testCollection = result.First(c => c.CollectionName == "test_collection");
        Assert.That(testCollection.Aliases, Has.Count.EqualTo(2));
        Assert.That(testCollection.Aliases, Does.Contain("test"));
        Assert.That(testCollection.Aliases, Does.Contain("test_alias"));
        
        var productsCollection = result.First(c => c.CollectionName == "products");
        Assert.That(productsCollection.Aliases, Has.Count.EqualTo(1));
        Assert.That(productsCollection.Aliases, Does.Contain("prod"));
        
        // Verify both methods were called
        await mockClient.Received(1).ListCollections(Arg.Any<CancellationToken>());
        await mockClient.Received(1).ListAllAliases(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithNoAliases_ReturnsEmptyAliasList()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>())
            .Returns(aliasesResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Aliases, Is.Empty);
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WhenAliasesApiFails_ContinuesWithEmptyAliases()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Status = new QdrantStatus(
                QdrantOperationStatusType.Error)
            {
                Error = "Failed to fetch aliases"
            }
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>())
            .Returns(aliasesResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert - Should still return collection but with empty aliases
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CollectionName, Is.EqualTo("test_collection"));
        Assert.That(result[0].Aliases, Is.Empty);
        
        // Verify both methods were called
        await mockClient.Received(1).ListCollections(Arg.Any<CancellationToken>());
        await mockClient.Received(1).ListAllAliases(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WhenAliasesApiThrows_ContinuesWithEmptyAliases()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ListCollectionAliasesResponse>(
                new Exception("Network error")));
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);
        
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert - Should still return collection but with empty aliases
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CollectionName, Is.EqualTo("test_collection"));
        Assert.That(result[0].Aliases, Is.Empty);
    }

    #endregion

    #region Clustering Info and Shards Tests

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithMultipleNodes_RetrievesShardsFromAllNodes()
    {
        // Arrange - This test verifies the bug fix where shards were only retrieved from one node
        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();
        var mockClient3 = Substitute.For<IQdrantHttpClient>();

        // Setup CreateClientFromUrl to return different clients for different URLs
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>())
            .Returns(mockClient1);
        _clientFactory.CreateClientFromUrl("http://node2:6333", Arg.Any<string?>())
            .Returns(mockClient2);
        _clientFactory.CreateClientFromUrl("http://node3:6333", Arg.Any<string?>())
            .Returns(mockClient3);

        // Mock ListCollections for all nodes
        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient1.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient2.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient3.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock for all clients
        SetupGetCollectionInfoMock(mockClient1);
        SetupGetCollectionInfoMock(mockClient2);
        SetupGetCollectionInfoMock(mockClient3);

        // Mock GetCollectionClusteringInfo for each node with different local shards
        var clusteringInfo1 = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1001,
                ShardCount = 3,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 0,
                        State = ShardState.Active
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var clusteringInfo2 = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1002,
                ShardCount = 3,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 1,
                        State = ShardState.Active
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var clusteringInfo3 = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1003,
                ShardCount = 3,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 2,
                        State = ShardState.Active
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient1.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo1);
        mockClient2.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo2);
        mockClient3.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo3);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1" },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = true, PodName = "pod2" },
            new() { Url = "http://node3:6333", PeerId = "1003", IsHealthy = true, PodName = "pod3" }
        };

        var peerToPodMap = new Dictionary<string, string>
        {
            { "1001", "pod1" },
            { "1002", "pod2" },
            { "1003", "pod3" }
        };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3), "Should have 3 collection entries (one per node)");
        
        // Verify each node has its own local shards
        var node1Collection = result.First(r => r.NodeUrl == "http://node1:6333");
        var node2Collection = result.First(r => r.NodeUrl == "http://node2:6333");
        var node3Collection = result.First(r => r.NodeUrl == "http://node3:6333");

        // Node 1 should have shard 0
        Assert.That(node1Collection.Metrics.ContainsKey("shards"), Is.True, "Node 1 should have shards");
        var node1Shards = node1Collection.Metrics.Shards!;
        Assert.That(node1Shards, Has.Count.EqualTo(1), "Node 1 should have 1 shard");
        Assert.That(node1Shards[0].ShardId, Is.EqualTo(0), "Node 1 should have shard 0");

        // Node 2 should have shard 1
        Assert.That(node2Collection.Metrics.ContainsKey("shards"), Is.True, "Node 2 should have shards");
        var node2Shards = node2Collection.Metrics.Shards!;
        Assert.That(node2Shards, Has.Count.EqualTo(1), "Node 2 should have 1 shard");
        Assert.That(node2Shards[0].ShardId, Is.EqualTo(1), "Node 2 should have shard 1");

        // Node 3 should have shard 2
        Assert.That(node3Collection.Metrics.ContainsKey("shards"), Is.True, "Node 3 should have shards");
        var node3Shards = node3Collection.Metrics.Shards!;
        Assert.That(node3Shards, Has.Count.EqualTo(1), "Node 3 should have 1 shard");
        Assert.That(node3Shards[0].ShardId, Is.EqualTo(2), "Node 3 should have shard 2");

        // Verify GetCollectionClusteringInfo was called for each node
        await mockClient1.Received(1).GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>());
        await mockClient2.Received(1).GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>());
        await mockClient3.Received(1).GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithOneUnhealthyNode_OnlyQueriesHealthyNodes()
    {
        // Arrange
        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>())
            .Returns(mockClient1);
        _clientFactory.CreateClientFromUrl("http://node2:6333", Arg.Any<string?>())
            .Returns(mockClient2);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient1.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient2.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock for both clients
        SetupGetCollectionInfoMock(mockClient1);
        SetupGetCollectionInfoMock(mockClient2);

        var clusteringInfo1 = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1001,
                ShardCount = 2,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 0,
                        State = ShardState.Active
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        var clusteringInfo2 = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1002,
                ShardCount = 2,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 1,
                        State = ShardState.Active
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient1.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo1);
        mockClient2.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo2);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1" },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = true, PodName = "pod2" },
            new() { Url = "http://node3:6333", PeerId = "1003", IsHealthy = false, PodName = "pod3" } // Unhealthy
        };

        var peerToPodMap = new Dictionary<string, string>
        {
            { "1001", "pod1" },
            { "1002", "pod2" },
            { "1003", "pod3" }
        };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2), "Should only have collections from 2 healthy nodes");
        
        // Verify only healthy nodes have shards
        var node1Collection = result.FirstOrDefault(r => r.NodeUrl == "http://node1:6333");
        var node2Collection = result.FirstOrDefault(r => r.NodeUrl == "http://node2:6333");

        Assert.That(node1Collection, Is.Not.Null);
        Assert.That(node2Collection, Is.Not.Null);
        
        Assert.That(node1Collection.Metrics.ContainsKey("shards"), Is.True);
        Assert.That(node2Collection.Metrics.ContainsKey("shards"), Is.True);

        // Verify clustering info was only called for healthy nodes
        await mockClient1.Received(1).GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>());
        await mockClient2.Received(1).GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_WithShardStates_EnrichesShardStateMetrics()
    {
        // Arrange
        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        
        // Setup GetCollectionInfo mock
        SetupGetCollectionInfoMock(mockClient);

        // Mock clustering info with multiple shards in different states
        var clusteringInfo = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                PeerId = 1001,
                ShardCount = 3,
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 0,
                        State = ShardState.Active
                    },
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 1,
                        State = ShardState.Partial
                    },
                    new GetCollectionClusteringInfoResponse.LocalShardInfo
                    {
                        ShardId = 2,
                        State = ShardState.Initializing
                    }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(
                QdrantOperationStatusType.Ok)
        };

        mockClient.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringInfo);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "pod1" }
        };

        var peerToPodMap = new Dictionary<string, string> { { "1001", "pod1" } };

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        
        var collection = result[0];
        Assert.That(collection.Metrics.ContainsKey("shards"), Is.True);
        Assert.That(collection.Metrics.ContainsKey("shardStates"), Is.True);

        var shards = collection.Metrics.Shards!;
        var shardStates = collection.Metrics.ShardStates!;

        Assert.That(shards, Has.Count.EqualTo(3));
        Assert.That(shardStates, Has.Count.EqualTo(3));
        
        Assert.That(shards[0].ShardId, Is.EqualTo(0));
        Assert.That(shards[0].State, Is.EqualTo("Active"));
        Assert.That(shards[1].ShardId, Is.EqualTo(1));
        Assert.That(shards[1].State, Is.EqualTo("Partial"));
        Assert.That(shards[2].ShardId, Is.EqualTo(2));
        Assert.That(shards[2].State, Is.EqualTo("Initializing"));
        
        Assert.That(shardStates["0"], Is.EqualTo("Active"));
        Assert.That(shardStates["1"], Is.EqualTo("Partial"));
        Assert.That(shardStates["2"], Is.EqualTo("Initializing"));
    }

    #endregion

    #region Cache Node Matching Tests

    [Test]
    public async Task GetCollectionsFromQdrantAsync_CacheReturned_WhenContainsAllRequestedNodes()
    {
        // Arrange - Setup 3 nodes
        var nodes = new[]
        {
            ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1"),
            ("http://node2:6333", "peer2", (string?)"ns1", (string?)"pod2"),
            ("http://node3:6333", "peer3", (string?)"ns1", (string?)"pod3")
        };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        SetupGetCollectionInfoMock(mockClient);
        
        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        // First call - populate cache with all 3 nodes
        await service.GetCollectionsFromQdrantAsync(nodes, CancellationToken.None, clearCache: true);

        // Act - Second call with same 3 nodes, should use cache
        var (result, isHealthy, error) = await service.GetCollectionsFromQdrantAsync(
            nodes, CancellationToken.None, clearCache: false);

        // Assert - Should return cached data
        Assert.That(result, Has.Count.EqualTo(3)); // 3 nodes
        Assert.That(isHealthy, Is.True);
        Assert.That(error, Is.Null);
        
        // Verify GetCollectionInfo was called only 3 times (from first call, not 6)
        await mockClient.Received(3).GetCollectionInfo(
            "collection1", 
            Arg.Any<CancellationToken>(), 
            Arg.Any<uint>(), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<Action<Exception, TimeSpan, int, uint>>());
    }

    [Test]
    public async Task GetCollectionsFromQdrantAsync_CacheIgnored_WhenContainsOnlyPartialNodes()
    {
        // Arrange
        var singleNode = new[] { ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1") };
        
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        SetupGetCollectionInfoMock(mockClient);
        
        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        // First call - cache from 1 node
        await service.GetCollectionsFromQdrantAsync(singleNode, CancellationToken.None, clearCache: true);

        // Act - Request data from 3 nodes, cache should be ignored
        var allNodes = new[]
        {
            ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1"),
            ("http://node2:6333", "peer2", (string?)"ns1", (string?)"pod2"),
            ("http://node3:6333", "peer3", (string?)"ns1", (string?)"pod3")
        };

        var (result, isHealthy, error) = await service.GetCollectionsFromQdrantAsync(
            allNodes, CancellationToken.None, clearCache: false);

        // Assert - Should fetch fresh data from all 3 nodes
        Assert.That(result, Has.Count.EqualTo(3)); // 3 nodes
        Assert.That(isHealthy, Is.True);
        Assert.That(error, Is.Null);
        
        // Verify GetCollectionInfo was called 4 times: 1 for single node, 3 for all nodes
        await mockClient.Received(4).GetCollectionInfo(
            "collection1", 
            Arg.Any<CancellationToken>(), 
            Arg.Any<uint>(), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<Action<Exception, TimeSpan, int, uint>>());
    }

    [Test]
    public async Task GetCollectionsFromQdrantAsync_CacheReturned_WhenRequestingSubsetOfCachedNodes()
    {
        // Arrange
        var allNodes = new[]
        {
            ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1"),
            ("http://node2:6333", "peer2", (string?)"ns1", (string?)"pod2"),
            ("http://node3:6333", "peer3", (string?)"ns1", (string?)"pod3")
        };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        SetupGetCollectionInfoMock(mockClient);
        
        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        // First call - populate cache with 3 nodes
        await service.GetCollectionsFromQdrantAsync(allNodes, CancellationToken.None, clearCache: true);

        // Act - Request only 2 nodes (subset), should use cache
        var subsetNodes = new[]
        {
            ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1"),
            ("http://node2:6333", "peer2", (string?)"ns1", (string?)"pod2")
        };

        var (result, isHealthy, error) = await service.GetCollectionsFromQdrantAsync(
            subsetNodes, CancellationToken.None, clearCache: false);

        // Assert - Should return cached data (contains all requested nodes)
        Assert.That(result, Has.Count.EqualTo(3)); // Cache returns all 3 nodes
        Assert.That(isHealthy, Is.True);
        Assert.That(error, Is.Null);
        
        // Verify GetCollectionInfo was called only 3 times (from first call)
        await mockClient.Received(3).GetCollectionInfo(
            "collection1", 
            Arg.Any<CancellationToken>(), 
            Arg.Any<uint>(), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<Action<Exception, TimeSpan, int, uint>>());
    }

    [Test]
    public async Task GetCollectionsFromQdrantAsync_CacheCleared_WhenClearCacheIsTrue()
    {
        // Arrange
        var nodes = new[] { ("http://node1:6333", "peer1", (string?)"ns1", (string?)"pod1") };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        SetupGetCollectionInfoMock(mockClient);
        
        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        await service.GetCollectionsFromQdrantAsync(nodes, CancellationToken.None, clearCache: false);

        // Act - Call with clearCache=true
        var (result, isHealthy, _) = await service.GetCollectionsFromQdrantAsync(
            nodes, CancellationToken.None, clearCache: true);

        // Assert - Should fetch fresh data
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(isHealthy, Is.True);
        
        // Verify GetCollectionInfo was called twice (once per call)
        await mockClient.Received(2).GetCollectionInfo(
            "collection1", 
            Arg.Any<CancellationToken>(), 
            Arg.Any<uint>(), 
            Arg.Any<TimeSpan?>(), 
            Arg.Any<Action<Exception, TimeSpan, int, uint>>());
    }

    #endregion

    #region Collection Node Sorting Tests

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_SortsCollectionNodesByPodName()
    {
        // Arrange
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "qdrant-2" },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = true, PodName = "qdrant-0" },
            new() { Url = "http://node3:6333", PeerId = "1003", IsHealthy = true, PodName = "qdrant-1" }
        };

        var peerToPodMap = new Dictionary<string, string>
        {
            { "1001", "qdrant-2" },
            { "1002", "qdrant-0" },
            { "1003", "qdrant-1" }
        };

        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();
        var mockClient3 = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                var uri = callInfo.ArgAt<Uri>(0);
                if (uri.ToString().Contains("node1")) return mockClient1;
                if (uri.ToString().Contains("node2")) return mockClient2;
                return mockClient3;
            });

        // Setup GetCollectionInfo for all clients
        SetupGetCollectionInfoMock(mockClient1);
        SetupGetCollectionInfoMock(mockClient2);
        SetupGetCollectionInfoMock(mockClient3);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[] { new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test-collection") }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        // Setup ListCollections and ListAllAliases for all clients
        mockClient1.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient1.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient2.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient2.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient3.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient3.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(
            _logger,
            _meterService,
            _clientFactory,
            _options,
            _commandExecutorLogger);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert - collection nodes should be sorted by PodName
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].PodName, Is.EqualTo("qdrant-0"));
        Assert.That(result[0].PeerId, Is.EqualTo("1002"));
        Assert.That(result[1].PodName, Is.EqualTo("qdrant-1"));
        Assert.That(result[1].PeerId, Is.EqualTo("1003"));
        Assert.That(result[2].PodName, Is.EqualTo("qdrant-2"));
        Assert.That(result[2].PeerId, Is.EqualTo("1001"));
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_SortsCollectionNodesByPeerId_WhenPodNameNotAvailable()
    {
        // Arrange
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "3001", IsHealthy = true, PodName = null },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = true, PodName = null },
            new() { Url = "http://node3:6333", PeerId = "2003", IsHealthy = true, PodName = null }
        };

        var peerToPodMap = new Dictionary<string, string>();

        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();
        var mockClient3 = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                var uri = callInfo.ArgAt<Uri>(0);
                if (uri.ToString().Contains("node1")) return mockClient1;
                if (uri.ToString().Contains("node2")) return mockClient2;
                return mockClient3;
            });

        // Setup GetCollectionInfo for all clients
        SetupGetCollectionInfoMock(mockClient1);
        SetupGetCollectionInfoMock(mockClient2);
        SetupGetCollectionInfoMock(mockClient3);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[] { new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test-collection") }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        // Setup ListCollections and ListAllAliases for all clients
        mockClient1.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient1.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient2.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient2.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient3.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient3.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(
            _logger,
            _meterService,
            _clientFactory,
            _options,
            _commandExecutorLogger);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert - collection nodes should be sorted by PeerId when PodName is not available
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].PeerId, Is.EqualTo("1002"));
        Assert.That(result[1].PeerId, Is.EqualTo("2003"));
        Assert.That(result[2].PeerId, Is.EqualTo("3001"));
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_SortsMultipleCollections_ByNodeIndependently()
    {
        // Arrange - two collections on nodes in different order
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "1001", IsHealthy = true, PodName = "qdrant-2" },
            new() { Url = "http://node2:6333", PeerId = "1002", IsHealthy = true, PodName = "qdrant-0" },
            new() { Url = "http://node3:6333", PeerId = "1003", IsHealthy = true, PodName = "qdrant-1" }
        };

        var peerToPodMap = new Dictionary<string, string>
        {
            { "1001", "qdrant-2" },
            { "1002", "qdrant-0" },
            { "1003", "qdrant-1" }
        };

        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();
        var mockClient3 = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                var uri = callInfo.ArgAt<Uri>(0);
                if (uri.ToString().Contains("node1")) return mockClient1;
                if (uri.ToString().Contains("node2")) return mockClient2;
                return mockClient3;
            });

        // Setup GetCollectionInfo for all clients
        SetupGetCollectionInfoMock(mockClient1);
        SetupGetCollectionInfoMock(mockClient2);
        SetupGetCollectionInfoMock(mockClient3);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection-a"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection-b")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        // Setup ListCollections and ListAllAliases for all clients
        mockClient1.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient1.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient2.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient2.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        
        mockClient3.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient3.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(
            _logger,
            _meterService,
            _clientFactory,
            _options,
            _commandExecutorLogger);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert - 6 total entries (2 collections x 3 nodes), each collection sorted by node
        Assert.That(result, Has.Count.EqualTo(6));
        
        // First 3 should be collection-a sorted by pod name
        Assert.That(result[0].CollectionName, Is.EqualTo("collection-a"));
        Assert.That(result[0].PodName, Is.EqualTo("qdrant-0"));
        Assert.That(result[1].CollectionName, Is.EqualTo("collection-a"));
        Assert.That(result[1].PodName, Is.EqualTo("qdrant-1"));
        Assert.That(result[2].CollectionName, Is.EqualTo("collection-a"));
        Assert.That(result[2].PodName, Is.EqualTo("qdrant-2"));
        
        // Next 3 should be collection-b sorted by pod name
        Assert.That(result[3].CollectionName, Is.EqualTo("collection-b"));
        Assert.That(result[3].PodName, Is.EqualTo("qdrant-0"));
        Assert.That(result[4].CollectionName, Is.EqualTo("collection-b"));
        Assert.That(result[4].PodName, Is.EqualTo("qdrant-1"));
        Assert.That(result[5].CollectionName, Is.EqualTo("collection-b"));
        Assert.That(result[5].PodName, Is.EqualTo("qdrant-2"));
    }

    #endregion

    #region GetCollectionShardsSizesForPodAsync Tests

    [Test]
    public async Task GetCollectionShardsSizesForPodAsync_ShouldReturnShardSizes_WhenShardsExist()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://test-node:6333";
        var peerId = "peer1";
        var collectionName = "test_collection";

        var shardDirectories = new List<string> { "0", "1", "2" };
        
        _mockCommandExecutor.ListDirectoriesAsync(
            podName, podNamespace, "/qdrant/storage/collections/test_collection", Arg.Any<CancellationToken>())
            .Returns(shardDirectories);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/test_collection", "0", Arg.Any<CancellationToken>())
            .Returns(500000000L);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/test_collection", "1", Arg.Any<CancellationToken>())
            .Returns(600000000L);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/test_collection", "2", Arg.Any<CancellationToken>())
            .Returns(400000000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetCollectionShardsSizesForPodAsync(
            podName, podNamespace, nodeUrl, peerId, collectionName, CancellationToken.None);

        // Assert
        var shardSizes = result.ToList();
        Assert.That(shardSizes, Has.Count.EqualTo(3));
        
        Assert.That(shardSizes[0].ShardId, Is.EqualTo(0));
        Assert.That(shardSizes[0].SizeBytes, Is.EqualTo(500000000));
        Assert.That(shardSizes[0].PrettySize, Does.Contain("MB")); // Format depends on locale
        Assert.That(shardSizes[0].CollectionName, Is.EqualTo(collectionName));
        
        Assert.That(shardSizes[1].ShardId, Is.EqualTo(1));
        Assert.That(shardSizes[1].SizeBytes, Is.EqualTo(600000000));
        
        Assert.That(shardSizes[2].ShardId, Is.EqualTo(2));
        Assert.That(shardSizes[2].SizeBytes, Is.EqualTo(400000000));
    }

    [Test]
    public async Task GetCollectionShardsSizesForPodAsync_ShouldIgnoreNonNumericDirectories()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://test-node:6333";
        var peerId = "peer1";
        var collectionName = "test_collection";

        var shardDirectories = new List<string> { "0", "1", "metadata", "snapshots", "2" };
        
        _mockCommandExecutor.ListDirectoriesAsync(
            podName, podNamespace, "/qdrant/storage/collections/test_collection", Arg.Any<CancellationToken>())
            .Returns(shardDirectories);
        
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), 
            Arg.Is<string>(s => s == "0" || s == "1" || s == "2"), Arg.Any<CancellationToken>())
            .Returns(100000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetCollectionShardsSizesForPodAsync(
            podName, podNamespace, nodeUrl, peerId, collectionName, CancellationToken.None);

        // Assert
        var shardSizes = result.ToList();
        Assert.That(shardSizes, Has.Count.EqualTo(3), "Should only include numeric shard directories");
        Assert.That(shardSizes.Select(s => s.ShardId), Is.EquivalentTo(new uint[] { 0, 1, 2 }));
    }

    [Test]
    public async Task GetCollectionShardsSizesForPodAsync_ShouldReturnEmpty_WhenCommandExecutorIsNull()
    {
        // Arrange
        var service = new CollectionService(
            _logger,
            _meterService,
            _clientFactory,
            _options,
            _commandExecutorLogger); // No Kubernetes client, so no command executor

        // Act
        var result = await service.GetCollectionShardsSizesForPodAsync(
            "pod", "ns", "http://node:6333", "peer1", "collection", CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetCollectionShardsSizesForPodAsync_ShouldHandleException_AndReturnEmpty()
    {
        // Arrange
        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), 
            Arg.Any<CancellationToken>())
            .Returns<List<string>>(x => throw new Exception("Test exception"));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetCollectionShardsSizesForPodAsync(
            "pod", "ns", "http://node:6333", "peer1", "collection", CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetAllShardsSizesForPodAsync Tests

    [Test]
    public async Task GetAllShardsSizesForPodAsync_ShouldReturnAllShardsForAllCollections()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://test-node:6333";
        var peerId = "peer1";

        var collections = new List<string> { "collection1", "collection2" };
        
        _mockCommandExecutor.ListDirectoriesAsync(
            podName, podNamespace, "/qdrant/storage/collections", Arg.Any<CancellationToken>())
            .Returns(collections);
        
        // Collection1 has shards 0, 1
        _mockCommandExecutor.ListDirectoriesAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection1", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "0", "1" });
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection1", "0", Arg.Any<CancellationToken>())
            .Returns(100000000L);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection1", "1", Arg.Any<CancellationToken>())
            .Returns(200000000L);
        
        // Collection2 has shards 0, 1, 2
        _mockCommandExecutor.ListDirectoriesAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection2", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "0", "1", "2" });
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection2", "0", Arg.Any<CancellationToken>())
            .Returns(150000000L);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection2", "1", Arg.Any<CancellationToken>())
            .Returns(250000000L);
        
        _mockCommandExecutor.GetSizeAsync(
            podName, podNamespace, "/qdrant/storage/collections/collection2", "2", Arg.Any<CancellationToken>())
            .Returns(350000000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetAllShardsSizesForPodAsync(
            podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var allShardSizes = result.ToList();
        Assert.That(allShardSizes, Has.Count.EqualTo(5), "Should have 2 shards from collection1 + 3 from collection2");
        
        var collection1Shards = allShardSizes.Where(s => s.CollectionName == "collection1").ToList();
        Assert.That(collection1Shards, Has.Count.EqualTo(2));
        Assert.That(collection1Shards[0].SizeBytes, Is.EqualTo(100000000));
        Assert.That(collection1Shards[1].SizeBytes, Is.EqualTo(200000000));
        
        var collection2Shards = allShardSizes.Where(s => s.CollectionName == "collection2").ToList();
        Assert.That(collection2Shards, Has.Count.EqualTo(3));
        Assert.That(collection2Shards[0].SizeBytes, Is.EqualTo(150000000));
        Assert.That(collection2Shards[1].SizeBytes, Is.EqualTo(250000000));
        Assert.That(collection2Shards[2].SizeBytes, Is.EqualTo(350000000));
    }

    [Test]
    public async Task GetAllShardsSizesForPodAsync_ShouldReturnEmpty_WhenNoCollections()
    {
        // Arrange
        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), 
            "/qdrant/storage/collections", Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetAllShardsSizesForPodAsync(
            "pod", "ns", "http://node:6333", "peer1", CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region EnrichCollectionsWithStorageInfoAsync - Shard Size Enrichment Tests

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_ShouldEnrichShardsWithSizeInformation()
    {
        // Arrange
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "peer1", IsHealthy = true, 
                    PodName = "qdrant-0", Namespace = "default" }
        };
        
        var peerToPodMap = new Dictionary<string, string>
        {
            { "peer1", "qdrant-0" }
        };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>())
            .Returns(mockClient);

        // Setup collections response
        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        SetupGetCollectionInfoMock(mockClient);

        // Setup clustering info with shard states
        var clusteringResponse = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 0, State = ShardState.Active },
                    new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 1, State = ShardState.Partial },
                    new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 2, State = ShardState.Initializing }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringResponse);

        // Setup storage info - collection size
        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default", "/qdrant/storage/collections", 
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { "test_collection" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", "/qdrant/storage/collections", 
            "test_collection", Arg.Any<CancellationToken>())
            .Returns(1500000000L); // 1.5 GB total

        // Setup shard sizes
        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "0", "1", "2" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", "0", Arg.Any<CancellationToken>())
            .Returns(500000000L);

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", "1", Arg.Any<CancellationToken>())
            .Returns(600000000L);

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", "2", Arg.Any<CancellationToken>())
            .Returns(400000000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        
        var collection = result[0];
        Assert.That(collection.CollectionName, Is.EqualTo("test_collection"));
        Assert.That(collection.Metrics.ContainsKey("shards"), Is.True);
        
        var shards = collection.Metrics.Shards!;
        Assert.That(shards, Has.Count.EqualTo(3));
        
        // Verify shard 0
        Assert.That(shards[0].ShardId, Is.EqualTo(0));
        Assert.That(shards[0].State, Is.EqualTo("Active"));
        Assert.That(shards[0].SizeBytes, Is.EqualTo(500000000));
        Assert.That(shards[0].PrettySize, Does.Contain("MB")); // Format depends on locale
        
        // Verify shard 1
        Assert.That(shards[1].ShardId, Is.EqualTo(1));
        Assert.That(shards[1].State, Is.EqualTo("Partial"));
        Assert.That(shards[1].SizeBytes, Is.EqualTo(600000000));
        Assert.That(shards[1].PrettySize, Does.Contain("MB")); // Format depends on locale
        
        // Verify shard 2
        Assert.That(shards[2].ShardId, Is.EqualTo(2));
        Assert.That(shards[2].State, Is.EqualTo("Initializing"));
        Assert.That(shards[2].SizeBytes, Is.EqualTo(400000000));
        Assert.That(shards[2].PrettySize, Does.Contain("MB")); // Format depends on locale
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_ShouldCreateShardsFromStorage_WhenNoClusteringInfo()
    {
        // Arrange - node without clustering info (e.g., standalone mode)
        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", PeerId = "peer1", IsHealthy = true, 
                    PodName = "qdrant-0", Namespace = "default" }
        };
        
        var peerToPodMap = new Dictionary<string, string> { { "peer1", "qdrant-0" } };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>())
            .Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        SetupGetCollectionInfoMock(mockClient);

        // No clustering info available
        mockClient.GetCollectionClusteringInfo(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GetCollectionClusteringInfoResponse
            {
                Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
                {
                    LocalShards = Array.Empty<GetCollectionClusteringInfoResponse.LocalShardInfo>(),
                    ShardTransfers = Array.Empty<ShardTransferInfo>()
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            });

        // Setup storage info
        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default", "/qdrant/storage/collections", 
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { "test_collection" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", "/qdrant/storage/collections", 
            "test_collection", Arg.Any<CancellationToken>())
            .Returns(900000000L);

        // Setup shard sizes
        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "0", "1" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", "0", Arg.Any<CancellationToken>())
            .Returns(400000000L);

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", 
            "/qdrant/storage/collections/test_collection", "1", Arg.Any<CancellationToken>())
            .Returns(500000000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        
        var collection = result[0];
        Assert.That(collection.Metrics.ContainsKey("shards"), Is.True);
        
        var shards = collection.Metrics.Shards!;
        Assert.That(shards, Has.Count.EqualTo(2));
        
        // Shards should have size but no state (since no clustering info)
        Assert.That(shards[0].ShardId, Is.EqualTo(0));
        Assert.That(shards[0].State, Is.Null);
        Assert.That(shards[0].SizeBytes, Is.EqualTo(400000000));
        Assert.That(shards[0].PrettySize, Does.Contain("MB")); // Format depends on locale
        
        Assert.That(shards[1].ShardId, Is.EqualTo(1));
        Assert.That(shards[1].State, Is.Null);
        Assert.That(shards[1].SizeBytes, Is.EqualTo(500000000));
        Assert.That(shards[1].PrettySize, Does.Contain("MB")); // Format depends on locale
    }

    #endregion

    #region EnrichCollectionsWithTelemetryAsync - Vectors/Payload sizes from cluster telemetry

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_ShouldEnrichShardsWithTelemetry_WhenGetClusterTelemetryReturnsData()
    {
        // Arrange: one node with numeric PeerId to match telemetry replica
        const string nodeUrl = "http://node1:6333";
        const ulong peerIdUlong = 100;
        var peerIdStr = peerIdUlong.ToString();

        var nodes = new List<NodeInfo>
        {
            new()
            {
                Url = nodeUrl,
                PeerId = peerIdStr,
                IsHealthy = true,
                PodName = "qdrant-0",
                Namespace = "default"
            }
        };

        var peerToPodMap = new Dictionary<string, string> { { peerIdStr, "qdrant-0" } };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl(nodeUrl, Arg.Any<string?>()).Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = Array.Empty<ListCollectionAliasesResponse.CollectionAlias>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);
        SetupGetCollectionInfoMock(mockClient);

        var clusteringResponse = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 0, State = ShardState.Active }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.GetCollectionClusteringInfo("test_collection", Arg.Any<CancellationToken>())
            .Returns(clusteringResponse);

        const long vectorsSize = 10_000_000L;
        const long payloadsSize = 5_000_000L;

        var telemetryResponse = new GetClusterTelemetryResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new GetClusterTelemetryResponse.ClusterTelemetryInfo
            {
                Collections = new Dictionary<string, GetClusterTelemetryResponse.CollectionTelemetry>
                {
                    ["test_collection"] = new GetClusterTelemetryResponse.CollectionTelemetry
                    {
                        Id = "test_collection",
                        Shards =
                        [
                            new GetClusterTelemetryResponse.CollectionTelemetry.ShardInfo
                            {
                                Id = 0,
                                Replicas =
                                [
                                    new GetClusterTelemetryResponse.CollectionTelemetry.ShardInfo.ReplicaInfo
                                    {
                                        PeerId = peerIdUlong,
                                        VectorsSizeBytes = vectorsSize,
                                        PayloadsSizeBytes = payloadsSize
                                    }
                                ]
                            }
                        ]
                    }
                }
            }
        };

        mockClient.GetClusterTelemetry(Arg.Any<CancellationToken>()).Returns(telemetryResponse);

        // Storage: collection and one shard so enrichment runs
        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default", "/qdrant/storage/collections",
                Arg.Any<CancellationToken>())
            .Returns(new List<string> { "test_collection" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default", "/qdrant/storage/collections",
                "test_collection", Arg.Any<CancellationToken>())
            .Returns(1000L);

        _mockCommandExecutor.ListDirectoriesAsync("qdrant-0", "default",
                "/qdrant/storage/collections/test_collection", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "0" });

        _mockCommandExecutor.GetSizeAsync("qdrant-0", "default",
                "/qdrant/storage/collections/test_collection", "0", Arg.Any<CancellationToken>())
            .Returns(500_000L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        var collection = result[0];
        Assert.That(collection.Metrics.ContainsKey("shards"), Is.True);

        var shards = collection.Metrics.Shards!;
        Assert.That(shards, Has.Count.EqualTo(1));
        Assert.That(shards[0].ShardId, Is.EqualTo(0));
        Assert.That(shards[0].SizeBytes, Is.EqualTo(500_000));
        Assert.That(shards[0].VectorsSizeBytes, Is.EqualTo(vectorsSize));
        Assert.That(shards[0].PayloadsSizeBytes, Is.EqualTo(payloadsSize));
        Assert.That(shards[0].PrettyVectorsSize, Is.Not.Null.And.Contains("MB"));
        Assert.That(shards[0].PrettyPayloadsSize, Is.Not.Null.And.Contains("MB"));
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_ShouldNotFail_WhenGetClusterTelemetryReturnsNull()
    {
        // Arrange: same as a minimal enrichment flow, but GetClusterTelemetry returns null
        var nodes = new List<NodeInfo>
        {
            new()
            {
                Url = "http://node1:6333",
                PeerId = "1",
                IsHealthy = true,
                PodName = "qdrant-0",
                Namespace = "default"
            }
        };

        var peerToPodMap = new Dictionary<string, string> { { "1", "qdrant-0" } };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("c1")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult { Aliases = [] },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        });
        SetupGetCollectionInfoMock(mockClient);

        var clusteringResponse = new GetCollectionClusteringInfoResponse
        {
            Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
            {
                LocalShards = new[]
                {
                    new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 0, State = ShardState.Active }
                },
                ShardTransfers = Array.Empty<ShardTransferInfo>()
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.GetCollectionClusteringInfo("c1", Arg.Any<CancellationToken>()).Returns(clusteringResponse);
        mockClient.GetClusterTelemetry(Arg.Any<CancellationToken>()).Returns((GetClusterTelemetryResponse?)null);

        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), "/qdrant/storage/collections",
                Arg.Any<CancellationToken>())
            .Returns(new List<string> { "c1" });
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(), "/qdrant/storage/collections", "c1",
                Arg.Any<CancellationToken>()).Returns(100L);
        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(),
                "/qdrant/storage/collections/c1", Arg.Any<CancellationToken>()).Returns(new List<string> { "0" });
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(),
                "/qdrant/storage/collections/c1", "0", Arg.Any<CancellationToken>()).Returns(200L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert: flow completes, shards exist but telemetry fields are null
        Assert.That(result, Has.Count.EqualTo(1));
        var shards = result[0].Metrics.Shards!;
        Assert.That(shards, Has.Count.EqualTo(1));
        Assert.That(shards[0].SizeBytes, Is.EqualTo(200));
        Assert.That(shards[0].VectorsSizeBytes, Is.Null);
        Assert.That(shards[0].PayloadsSizeBytes, Is.Null);
    }

    [Test]
    public async Task GetEnrichedCollectionsInfoAsync_ShouldNotFail_WhenGetClusterTelemetryThrows()
    {
        var nodes = new List<NodeInfo>
        {
            new()
            {
                Url = "http://node1:6333",
                PeerId = "1",
                IsHealthy = true,
                PodName = "qdrant-0",
                Namespace = "default"
            }
        };

        var peerToPodMap = new Dictionary<string, string> { { "1", "qdrant-0" } };

        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var collectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[] { new ListCollectionsResponse.CollectionNamesUnit.CollectionName("c1") }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>()).Returns(collectionsResponse);
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult { Aliases = [] },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        });
        SetupGetCollectionInfoMock(mockClient);

        mockClient.GetCollectionClusteringInfo("c1", Arg.Any<CancellationToken>()).Returns(
            new GetCollectionClusteringInfoResponse
            {
                Result = new GetCollectionClusteringInfoResponse.CollectionClusteringInfo
                {
                    LocalShards = new[]
                    {
                        new GetCollectionClusteringInfoResponse.LocalShardInfo { ShardId = 0, State = ShardState.Active }
                    },
                    ShardTransfers = Array.Empty<ShardTransferInfo>()
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            });
        mockClient.GetClusterTelemetry(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<GetClusterTelemetryResponse?>(new InvalidOperationException("Telemetry unavailable")));

        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(), "/qdrant/storage/collections",
                Arg.Any<CancellationToken>())
            .Returns(new List<string> { "c1" });
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(), "/qdrant/storage/collections", "c1",
                Arg.Any<CancellationToken>()).Returns(100L);
        _mockCommandExecutor.ListDirectoriesAsync(Arg.Any<string>(), Arg.Any<string>(),
                "/qdrant/storage/collections/c1", Arg.Any<CancellationToken>()).Returns(new List<string> { "0" });
        _mockCommandExecutor.GetSizeAsync(Arg.Any<string>(), Arg.Any<string>(),
                "/qdrant/storage/collections/c1", "0", Arg.Any<CancellationToken>()).Returns(200L);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetEnrichedCollectionsInfoAsync(nodes, peerToPodMap, CancellationToken.None);

        // Assert: flow completes, shards have no telemetry
        Assert.That(result, Has.Count.EqualTo(1));
        var shards = result[0].Metrics.Shards!;
        Assert.That(shards[0].VectorsSizeBytes, Is.Null);
        Assert.That(shards[0].PayloadsSizeBytes, Is.Null);
    }

    #endregion

    #region SetCollectionAliasAsync Tests

    [Test]
    public async Task SetCollectionAliasAsync_WhenAliasDoesNotExist_CallsUpdateCollectionsAliasesAndReturnsTrue()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = new[]
                {
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "existing",
                        CollectionName = "my_collection"
                    }
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var updateResponse = new DefaultOperationResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>())
            .Returns(updateResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.SetCollectionAliasAsync("http://node1:6333", "my_collection", "new_alias", CancellationToken.None);

        Assert.That(result, Is.True);
        await mockClient.Received(1).ListAllAliases(Arg.Any<CancellationToken>());
        await mockClient.Received(1).UpdateCollectionsAliases(Arg.Is<UpdateCollectionAliasesRequest>(r => r.OperationsCount == 1), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task SetCollectionAliasAsync_WhenAliasAlreadyExistsForCollection_ReturnsTrueWithoutCallingUpdate()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = new[]
                {
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "my_alias",
                        CollectionName = "my_collection"
                    }
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.SetCollectionAliasAsync("http://node1:6333", "my_collection", "my_alias", CancellationToken.None);

        Assert.That(result, Is.True);
        await mockClient.Received(1).ListAllAliases(Arg.Any<CancellationToken>());
        await mockClient.DidNotReceive().UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task SetCollectionAliasAsync_WhenAliasUsedByOtherCollection_ReturnsFalse()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Result = new ListCollectionAliasesResponse.CollectionAliasesResult
            {
                Aliases = new[]
                {
                    new ListCollectionAliasesResponse.CollectionAlias
                    {
                        AliasName = "taken_alias",
                        CollectionName = "other_collection"
                    }
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.SetCollectionAliasAsync("http://node1:6333", "my_collection", "taken_alias", CancellationToken.None);

        Assert.That(result, Is.False);
        await mockClient.Received(1).ListAllAliases(Arg.Any<CancellationToken>());
        await mockClient.DidNotReceive().UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task SetCollectionAliasAsync_WhenListAllAliasesFails_ReturnsFalse()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var aliasesResponse = new ListCollectionAliasesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "API error" }
        };
        mockClient.ListAllAliases(Arg.Any<CancellationToken>()).Returns(aliasesResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.SetCollectionAliasAsync("http://node1:6333", "my_collection", "new_alias", CancellationToken.None);

        Assert.That(result, Is.False);
        await mockClient.DidNotReceive().UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    #endregion

    #region RenameCollectionAliasAsync Tests

    [Test]
    public async Task RenameCollectionAliasAsync_WhenOldAndNewDiffer_CallsUpdateCollectionsAliasesAndReturnsTrue()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var updateResponse = new DefaultOperationResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>())
            .Returns(updateResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.RenameCollectionAliasAsync("http://node1:6333", "old_alias", "new_alias", CancellationToken.None);

        Assert.That(result, Is.True);
        await mockClient.Received(1).UpdateCollectionsAliases(Arg.Is<UpdateCollectionAliasesRequest>(r => r.OperationsCount == 1), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task RenameCollectionAliasAsync_WhenOldEqualsNew_ReturnsTrueWithoutCallingApi()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.RenameCollectionAliasAsync("http://node1:6333", "same_alias", "same_alias", CancellationToken.None);

        Assert.That(result, Is.True);
        await mockClient.DidNotReceive().UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task RenameCollectionAliasAsync_WhenUpdateFails_ReturnsFalse()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var updateResponse = new DefaultOperationResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "Alias not found" }
        };
        mockClient.UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>())
            .Returns(updateResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.RenameCollectionAliasAsync("http://node1:6333", "old_alias", "new_alias", CancellationToken.None);

        Assert.That(result, Is.False);
    }

    #endregion

    #region DeleteCollectionAliasAsync Tests

    [Test]
    public async Task DeleteCollectionAliasAsync_WhenSuccess_CallsUpdateCollectionsAliasesAndReturnsTrue()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var updateResponse = new DefaultOperationResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        mockClient.UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>())
            .Returns(updateResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.DeleteCollectionAliasAsync("http://node1:6333", "my_alias", CancellationToken.None);

        Assert.That(result, Is.True);
        await mockClient.Received(1).UpdateCollectionsAliases(Arg.Is<UpdateCollectionAliasesRequest>(r => r.OperationsCount == 1), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeleteCollectionAliasAsync_WhenUpdateFails_ReturnsFalse()
    {
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClientFromUrl("http://node1:6333", Arg.Any<string?>()).Returns(mockClient);

        var updateResponse = new DefaultOperationResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "Alias not found" }
        };
        mockClient.UpdateCollectionsAliases(Arg.Any<UpdateCollectionAliasesRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>(), Arg.Any<uint>(), Arg.Any<TimeSpan?>(), Arg.Any<Action<Exception, TimeSpan, int, uint>>(), Arg.Any<string>())
            .Returns(updateResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger);

        var result = await service.DeleteCollectionAliasAsync("http://node1:6333", "my_alias", CancellationToken.None);

        Assert.That(result, Is.False);
    }

    #endregion

    /// <summary>
    /// Helper method to create CollectionService with mocked IPodCommandExecutor using reflection
    /// </summary>
    private CollectionService CreateCollectionServiceWithMockExecutor(IPodCommandExecutor mockExecutor)
    {
        var service = new CollectionService(
            _logger,
            _meterService,
            _clientFactory,
            _options,
            _commandExecutorLogger);

        // Use reflection to set the private readonly _commandExecutor field
        var fieldInfo = typeof(CollectionService).GetField("_commandExecutor", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (fieldInfo == null)
        {
            // Try to find it by all fields
            var allFields = typeof(CollectionService).GetFields(
                BindingFlags.NonPublic | 
                BindingFlags.Instance | 
                BindingFlags.Public);
            
            fieldInfo = allFields.FirstOrDefault(f => f.FieldType == typeof(IPodCommandExecutor));
            
            if (fieldInfo == null)
            {
                var fieldNames = string.Join(", ", allFields.Select(f => f.Name));
                throw new InvalidOperationException($"Could not find _commandExecutor field. Available fields: {fieldNames}");
            }
        }
        
        fieldInfo.SetValue(service, mockExecutor);

        return service;
    }
}
