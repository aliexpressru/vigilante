using Aer.QdrantClient.Http.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
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

    #region DeleteSnapshotFromDiskAsync Tests

    [Test]
    public async Task DeleteSnapshotFromDiskAsync_ShouldCallDeleteAndVerify()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";

        _mockCommandExecutor.DeleteAndVerifyAsync(
                podName, 
                podNamespace, 
                "/qdrant/snapshots/test-collection/test-snapshot.snapshot", 
                false, 
                "Snapshot test-snapshot.snapshot", 
                Arg.Any<CancellationToken>())
            .Returns(true);

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.DeleteSnapshotFromDiskAsync(
            podName, podNamespace, collectionName, snapshotName, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
        await _mockCommandExecutor.Received(1).DeleteAndVerifyAsync(
            podName, 
            podNamespace, 
            "/qdrant/snapshots/test-collection/test-snapshot.snapshot", 
            false, 
            "Snapshot test-snapshot.snapshot", 
            Arg.Any<CancellationToken>());
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
