using System.Reflection;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
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
        Assert.That(result[0].Metrics["sizeBytes"], Is.EqualTo(1073741824L));
        Assert.That(result[0].Metrics["prettySize"], Is.EqualTo("1 GB"));
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
        Assert.That(result[0].Metrics["prettySize"], Is.EqualTo("N/A")); // Not enriched
        Assert.That(result[0].Metrics["sizeBytes"], Is.EqualTo(0L)); // Not enriched
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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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
        var node1Shards = (List<ulong>)node1Collection.Metrics["shards"];
        Assert.That(node1Shards, Has.Count.EqualTo(1), "Node 1 should have 1 shard");
        Assert.That(node1Shards[0], Is.EqualTo(0), "Node 1 should have shard 0");

        // Node 2 should have shard 1
        Assert.That(node2Collection.Metrics.ContainsKey("shards"), Is.True, "Node 2 should have shards");
        var node2Shards = (List<ulong>)node2Collection.Metrics["shards"];
        Assert.That(node2Shards, Has.Count.EqualTo(1), "Node 2 should have 1 shard");
        Assert.That(node2Shards[0], Is.EqualTo(1), "Node 2 should have shard 1");

        // Node 3 should have shard 2
        Assert.That(node3Collection.Metrics.ContainsKey("shards"), Is.True, "Node 3 should have shards");
        var node3Shards = (List<ulong>)node3Collection.Metrics["shards"];
        Assert.That(node3Shards, Has.Count.EqualTo(1), "Node 3 should have 1 shard");
        Assert.That(node3Shards[0], Is.EqualTo(2), "Node 3 should have shard 2");

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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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
                ShardTransfers = Array.Empty<GetCollectionClusteringInfoResponse.ShardTransferInfo>()
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

        var shards = (List<ulong>)collection.Metrics["shards"];
        var shardStates = (Dictionary<string, string>)collection.Metrics["shardStates"];

        Assert.That(shards, Has.Count.EqualTo(3));
        Assert.That(shardStates, Has.Count.EqualTo(3));
        
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
