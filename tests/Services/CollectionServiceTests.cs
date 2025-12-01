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
            Nodes = new List<QdrantNodeConfig>()
        });
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldParseRealSnapshotStructure()
    {
        // Arrange
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";
        
        var collectionName = "test_collection";
        var snapshotFileName = "test_collection-375902039176772-2025-11-06-16-42-21.snapshot";
        var snapshotSize = 1500000000L; // ~1.4 GB

        // Mock ListFilesAsync for /qdrant/snapshots - returns collection folders and tmp
        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { collectionName, "tmp" }));

        // Mock ListFilesAsync for specific collection - returns .snapshot file
        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { snapshotFileName }));

        // Mock ListFilesAsync for tmp folder - empty result
        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots/tmp", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string>()));

        // Mock GetSizeAsync for snapshot file
        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", snapshotFileName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(snapshotSize));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var snapshots = result.ToList();
        Assert.That(snapshots, Has.Count.EqualTo(1), "Should find exactly one snapshot");
        
        var snapshot = snapshots[0];
        Assert.That(snapshot.PodName, Is.EqualTo(podName));
        Assert.That(snapshot.NodeUrl, Is.EqualTo(nodeUrl));
        Assert.That(snapshot.PeerId, Is.EqualTo(peerId));
        Assert.That(snapshot.CollectionName, Is.EqualTo(collectionName));
        Assert.That(snapshot.SnapshotName, Is.EqualTo(snapshotFileName));
        Assert.That(snapshot.SizeBytes, Is.EqualTo(snapshotSize));
        
        // Verify the snapshot name matches the expected pattern
        Assert.That(snapshot.SnapshotName, Does.StartWith(collectionName));
        Assert.That(snapshot.SnapshotName, Does.Contain("375902039176772"));
        Assert.That(snapshot.SnapshotName, Does.Contain("2025-11-06"));
        Assert.That(snapshot.SnapshotName, Does.EndWith(".snapshot"));
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldIgnoreTmpFolder()
    {
        // Arrange
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";

        // Mock - only tmp folder without snapshot files
        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { "tmp" }));

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots/tmp", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string>()));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty, "Should return empty list when only tmp folder exists without snapshots");
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldHandleEmptySnapshotsFolder()
    {
        // Arrange
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string>()));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Empty, "Should return empty list when no collection folders exist");
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldHandleMultipleSnapshotsInOneCollection()
    {
        // Arrange
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";
        
        var collectionName = "embeddings_collection";
        var snapshot1 = $"{collectionName}-375902039176772-2025-11-06-16-42-21.snapshot";
        var snapshot2 = $"{collectionName}-3372865182647577-2025-11-07-10-30-00.snapshot";
        var size1 = 1500000000L;
        var size2 = 1600000000L;

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { collectionName }));

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { snapshot1, snapshot2 }));

        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", snapshot1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(size1));

        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", snapshot2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(size2));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var snapshots = result.ToList();
        Assert.That(snapshots, Has.Count.EqualTo(2), "Should find two snapshots");
        
        Assert.That(snapshots[0].CollectionName, Is.EqualTo(collectionName));
        Assert.That(snapshots[0].SnapshotName, Is.EqualTo(snapshot1));
        Assert.That(snapshots[0].SizeBytes, Is.EqualTo(size1));
        
        Assert.That(snapshots[1].CollectionName, Is.EqualTo(collectionName));
        Assert.That(snapshots[1].SnapshotName, Is.EqualTo(snapshot2));
        Assert.That(snapshots[1].SizeBytes, Is.EqualTo(size2));
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldHandleMultipleCollectionsWithSnapshots()
    {
        // Arrange
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";
        
        var collection1 = "embeddings";
        var collection2 = "products";
        var snapshot1 = $"{collection1}-111-2025-11-06-16-42-21.snapshot";
        var snapshot2 = $"{collection2}-222-2025-11-07-10-30-00.snapshot";

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { collection1, collection2 }));

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collection1}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { snapshot1 }));

        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collection1}", snapshot1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(1000000L));

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collection2}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { snapshot2 }));

        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collection2}", snapshot2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(2000000L));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var snapshots = result.ToList();
        Assert.That(snapshots, Has.Count.EqualTo(2), "Should find snapshots from both collections");
        
        var col1Snapshots = snapshots.Where(s => s.CollectionName == collection1).ToList();
        var col2Snapshots = snapshots.Where(s => s.CollectionName == collection2).ToList();
        
        Assert.That(col1Snapshots, Has.Count.EqualTo(1));
        Assert.That(col2Snapshots, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ShouldHandleLongCollectionNames()
    {
        // Arrange - test with long collection name similar to production
        var podName = "qdrant1-0";
        var podNamespace = "qdrant";
        var nodeUrl = "http://10.0.0.1:6333";
        var peerId = "peer1";
        
        var collectionName = "long_collection_name_with_special__chars__and__timestamps~~20251104";
        var snapshotFileName = $"{collectionName}-375902039176772-2025-11-06-16-42-21.snapshot";
        var snapshotSize = 5000000000L; // 5 GB

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { collectionName }));

        _mockCommandExecutor
            .ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<string> { snapshotFileName }));

        _mockCommandExecutor
            .GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", snapshotFileName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(snapshotSize));

        var service = CreateCollectionServiceWithMockExecutor(_mockCommandExecutor);

        // Act
        var result = await service.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var snapshots = result.ToList();
        Assert.That(snapshots, Has.Count.EqualTo(1));
        Assert.That(snapshots[0].CollectionName, Is.EqualTo(collectionName));
        Assert.That(snapshots[0].SnapshotName, Does.Contain("long_collection_name"));
        Assert.That(snapshots[0].SizeBytes, Is.EqualTo(snapshotSize));
    }

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

        // Use reflection to set the private _commandExecutor field
        var fieldInfo = typeof(CollectionService).GetField("_commandExecutor", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        fieldInfo?.SetValue(service, mockExecutor);

        return service;
    }
}

