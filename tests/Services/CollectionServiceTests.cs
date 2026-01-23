using Aer.QdrantClient.Http.Abstractions;
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
        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger, null);
        
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

        var collectionsResponse = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new Aer.QdrantClient.Http.Models.Shared.QdrantStatus(
                Aer.QdrantClient.Http.Models.Shared.QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger, null);
        
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

        var collectionsResponse = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new Aer.QdrantClient.Http.Models.Shared.QdrantStatus(
                Aer.QdrantClient.Http.Models.Shared.QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);

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

        var collectionsResponse = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new Aer.QdrantClient.Http.Models.Shared.QdrantStatus(
                Aer.QdrantClient.Http.Models.Shared.QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);

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

        var collectionsResponse = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new Aer.QdrantClient.Http.Models.Responses.ListCollectionsResponse.CollectionNamesUnit.CollectionName("test_collection")
                }
            },
            Status = new Aer.QdrantClient.Http.Models.Shared.QdrantStatus(
                Aer.QdrantClient.Http.Models.Shared.QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(collectionsResponse);

        var service = new CollectionService(_logger, _meterService, _clientFactory, _options, _commandExecutorLogger, null);
        
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
            _commandExecutorLogger,
            null);

        // Use reflection to set the private readonly _commandExecutor field
        var fieldInfo = typeof(CollectionService).GetField("_commandExecutor", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (fieldInfo == null)
        {
            // Try to find it by all fields
            var allFields = typeof(CollectionService).GetFields(
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Instance | 
                System.Reflection.BindingFlags.Public);
            
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
