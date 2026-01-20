using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Configuration;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class SnapshotServiceTests
{
    private IQdrantNodesProvider _nodesProvider = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private ICollectionService _collectionService = null!;
    private IOptions<QdrantOptions> _options = null!;
    private ILogger<SnapshotService> _logger = null!;
    private IS3SnapshotService _s3SnapshotService = null!;
    private SnapshotService _snapshotManager = null!;

    [SetUp]
    public void Setup()
    {
        _nodesProvider = Substitute.For<IQdrantNodesProvider>();
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _collectionService = Substitute.For<ICollectionService>();
        _options = Substitute.For<IOptions<QdrantOptions>>();
        _logger = Substitute.For<ILogger<SnapshotService>>();
        
        _options.Value.Returns(new QdrantOptions { HttpTimeoutSeconds = 5 });
        
        // Mock S3SnapshotService to avoid real S3 dependencies in tests
        _s3SnapshotService = Substitute.For<IS3SnapshotService>();
        
        _snapshotManager = new SnapshotService(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _s3SnapshotService,
            _options,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        // No need to dispose mocked service
    }

    #region DeleteSnapshotAsync Tests

    [Test]
    public async Task DeleteSnapshotAsync_FromKubernetesStorage_Success()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";
        var podName = "test-pod";
        var podNamespace = "test-namespace";

        _collectionService
            .DeleteSnapshotFromDiskAsync(podName, podNamespace, collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.KubernetesStorage,
            nodeUrl: null,
            podName: podName,
            podNamespace: podNamespace,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
        await _collectionService.Received(1).DeleteSnapshotFromDiskAsync(
            podName, podNamespace, collectionName, snapshotName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteSnapshotAsync_FromKubernetesStorage_MissingPodName_ReturnsFalse()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.KubernetesStorage,
            nodeUrl: null,
            podName: null,
            podNamespace: "test-namespace",
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
        await _collectionService.DidNotReceive().DeleteSnapshotFromDiskAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteSnapshotAsync_FromQdrantApi_Success()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";
        var nodeUrl = "http://test-node:6333";

        // Mock Qdrant client
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        var deleteResponse = new DefaultOperationResponse
        {
            Result = true,
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(deleteResponse));

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.QdrantApi,
            nodeUrl: nodeUrl,
            podName: null,
            podNamespace: null,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
        await mockClient.Received(1).DeleteCollectionSnapshot(
            collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
    }

    [Test]
    public async Task DeleteSnapshotAsync_FromQdrantApi_MissingNodeUrl_ReturnsFalse()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.QdrantApi,
            nodeUrl: null,
            podName: null,
            podNamespace: null,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
        // No need to verify collectionService since we're using snapshotService's own method now
    }

    [Test]
    public async Task DeleteSnapshotAsync_FromQdrantApi_Failure()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";
        var nodeUrl = "http://test-node:6333";

        // Mock Qdrant client
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        var deleteResponse = new DefaultOperationResponse
        {
            Result = false,
            Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "Delete failed" }
        };

        mockClient.DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(deleteResponse));

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.QdrantApi,
            nodeUrl: nodeUrl,
            podName: null,
            podNamespace: null,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
        await mockClient.Received(1).DeleteCollectionSnapshot(
            collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
    }

    #endregion

    #region CreateCollectionSnapshotOnAllNodesAsync Tests

    [Test]
    public async Task CreateCollectionSnapshotOnAllNodesAsync_CreatesOnAllNodes()
    {
        // Arrange
        var collectionName = "test_collection";
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Create mock Qdrant clients for each node
        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();

        // Mock the factory to return appropriate clients
        _clientFactory.CreateClient(Arg.Is<Uri>(u => u.Host == "node1" && u.Port == 6333), Arg.Any<string>())
            .Returns(mockClient1);
        _clientFactory.CreateClient(Arg.Is<Uri>(u => u.Host == "node2" && u.Port == 6333), Arg.Any<string>())
            .Returns(mockClient2);

        // Mock CreateCollectionSnapshot responses
        var snapshotResponse1 = new CreateSnapshotResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Shared.SnapshotInfo
            {
                Name = "snapshot1.snapshot"
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        var snapshotResponse2 = new CreateSnapshotResponse
        {
            Result = new Aer.QdrantClient.Http.Models.Shared.SnapshotInfo
            {
                Name = "snapshot2.snapshot"
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient1.CreateCollectionSnapshot(collectionName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(snapshotResponse1));

        mockClient2.CreateCollectionSnapshot(collectionName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(snapshotResponse2));

        // Act
        var result = await _snapshotManager.CreateCollectionSnapshotOnAllNodesAsync(collectionName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["http://node1:6333"], Is.EqualTo("snapshot1.snapshot"));
        Assert.That(result["http://node2:6333"], Is.EqualTo("snapshot2.snapshot"));
    }

    #endregion

    #region DeleteCollectionSnapshotOnAllNodesAsync Tests

    [Test]
    public async Task DeleteCollectionSnapshotOnAllNodesAsync_DeletesFromAllNodes()
    {
        // Arrange
        var collectionName = "test_collection";
        var snapshotName = "test-snapshot.snapshot";
        
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Mock S3 as not available
        _s3SnapshotService.IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Mock collectionService for Kubernetes storage deletion (since nodes have pod names)
        _collectionService
            .DeleteSnapshotFromDiskAsync("pod1", "ns1", collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        
        _collectionService
            .DeleteSnapshotFromDiskAsync("pod2", "ns1", collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _snapshotManager.DeleteCollectionSnapshotOnAllNodesAsync(collectionName, snapshotName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Values.All(v => v), Is.True);
        Assert.That(result["http://node1:6333"], Is.True);
        Assert.That(result["http://node2:6333"], Is.True);
        
        // Verify it used Kubernetes storage (DeleteSnapshotFromDiskAsync)
        await _collectionService.Received(1).DeleteSnapshotFromDiskAsync("pod1", "ns1", collectionName, snapshotName, Arg.Any<CancellationToken>());
        await _collectionService.Received(1).DeleteSnapshotFromDiskAsync("pod2", "ns1", collectionName, snapshotName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteCollectionSnapshotOnAllNodesAsync_UsesS3WhenAvailable()
    {
        // Arrange
        var collectionName = "test_collection";
        var snapshotName = "test-snapshot.snapshot";
        
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Mock S3 as available
        _s3SnapshotService.IsAvailableAsync("ns1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Mock S3 deletion
        _s3SnapshotService.DeleteSnapshotAsync(collectionName, snapshotName, "ns1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _snapshotManager.DeleteCollectionSnapshotOnAllNodesAsync(collectionName, snapshotName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Values.All(v => v), Is.True);
        Assert.That(result["http://node1:6333"], Is.True);
        Assert.That(result["http://node2:6333"], Is.True);
        
        // Verify it used S3 (DeleteSnapshotAsync on S3 service, only once)
        await _s3SnapshotService.Received(1).DeleteSnapshotAsync(collectionName, snapshotName, "ns1", Arg.Any<CancellationToken>());
        
        // Verify it did NOT use Kubernetes storage
        await _collectionService.DidNotReceive().DeleteSnapshotFromDiskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteCollectionSnapshotOnAllNodesAsync_UsesQdrantApiWhenNoPodNames()
    {
        // Arrange
        var collectionName = "test_collection";
        var snapshotName = "test-snapshot.snapshot";
        
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Mock S3 as not available
        _s3SnapshotService.IsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        // Mock Qdrant clients for API deletion
        var mockClient1 = Substitute.For<IQdrantHttpClient>();
        var mockClient2 = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Is<Uri>(u => u.Host == "node1" && u.Port == 6333), Arg.Any<string>())
            .Returns(mockClient1);
        _clientFactory.CreateClient(Arg.Is<Uri>(u => u.Host == "node2" && u.Port == 6333), Arg.Any<string>())
            .Returns(mockClient2);

        var deleteResponse = new DefaultOperationResponse
        {
            Result = true,
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient1.DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(deleteResponse));

        mockClient2.DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false)
            .Returns(Task.FromResult(deleteResponse));

        // Act
        var result = await _snapshotManager.DeleteCollectionSnapshotOnAllNodesAsync(collectionName, snapshotName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Values.All(v => v), Is.True);
        
        // Verify it used Qdrant API
        await mockClient1.Received(1).DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
        await mockClient2.Received(1).DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
        
        // Verify it did NOT use S3 or Kubernetes storage
        await _s3SnapshotService.DidNotReceive().DeleteSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _collectionService.DidNotReceive().DeleteSnapshotFromDiskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region GetSnapshotsInfoAsync Tests

    [Test]
    public async Task GetSnapshotsInfoAsync_WhenHasPodsWithNames_ReturnsSnapshotsFromDisk()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);
            
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var snapshotsFromDisk = new List<SnapshotInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "1001",
                CollectionName = "collection1",
                SnapshotName = "snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "ns1",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _collectionService
            .GetSnapshotsFromDiskForPodAsync("pod1", "ns1", "http://node1:6333", "1001", Arg.Any<CancellationToken>())
            .Returns(snapshotsFromDisk);

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Source, Is.EqualTo(SnapshotSource.KubernetesStorage));
        Assert.That(result.First().SnapshotName, Is.EqualTo("snapshot1.snapshot"));
        Assert.That(result.First().CollectionName, Is.EqualTo("collection1"));
    }

    [Test]
    public async Task GetSnapshotsInfoAsync_WhenNoPods_FallsBackToQdrantApi()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "", PodName = "" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
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
        
        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Source, Is.EqualTo(SnapshotSource.QdrantApi));
        Assert.That(result.First().SnapshotName, Is.EqualTo("collection1-1001-snapshot.snapshot"));
        Assert.That(result.First().SizeBytes, Is.EqualTo(2048));
    }

    [Test]
    public async Task GetSnapshotsInfoAsync_WhenDiskReturnsEmpty_FallsBackToQdrantApi()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Disk returns empty
        _collectionService
            .GetSnapshotsFromDiskForPodAsync("pod1", "ns1", "http://node1:6333", "1001", Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>());

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
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
        
        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().Source, Is.EqualTo(SnapshotSource.QdrantApi));
    }

    [Test]
    public async Task GetSnapshotsInfoAsync_FiltersByPeerId_WhenSnapshotsContainPeerId()
    {
        // Arrange - simulate S3 storage where all nodes return same snapshots
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "", PodName = "" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
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
        
        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response - return snapshots from multiple nodes (simulating S3 storage)
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }, // Matches node1's PeerId
                new() { Name = "collection1-1002-snapshot.snapshot", Size = 2048 }, // Different PeerId - should be filtered out
                new() { Name = "collection1-1003-snapshot.snapshot", Size = 2048 }  // Different PeerId - should be filtered out
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert - should only return the snapshot matching this node's PeerId
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().SnapshotName, Is.EqualTo("collection1-1001-snapshot.snapshot"));
        Assert.That(result.First().PeerId, Is.EqualTo("1001"));
    }

    [Test]
    public async Task GetSnapshotsInfoAsync_HandlesMultipleCollections()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "", PodName = "" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections with multiple collections
        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection2")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        
        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots for collection1
        var listSnapshots1Response = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots1Response));

        // Mock ListCollectionSnapshots for collection2
        var listSnapshots2Response = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection2-1001-snapshot.snapshot", Size = 4096 }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots2Response));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Count(s => s.CollectionName == "collection1"), Is.EqualTo(1));
        Assert.That(result.Count(s => s.CollectionName == "collection2"), Is.EqualTo(1));
    }

    [Test]
    public async Task GetSnapshotsInfoAsync_WhenQdrantApiFailsForOneCollection_ContinuesWithOthers()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "", PodName = "" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = Substitute.For<IQdrantHttpClient>();
        
        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections = new[]
                {
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection2")
                }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };
        
        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // collection1 throws exception
        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns<ListSnapshotsResponse>(_ => throw new Exception("Collection1 failed"));

        // collection2 succeeds
        var listSnapshots2Response = new ListSnapshotsResponse
        {
            Result = new List<Aer.QdrantClient.Http.Models.Shared.SnapshotInfo>
            {
                new() { Name = "collection2-1001-snapshot.snapshot", Size = 4096 }
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots2Response));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert - should still get snapshots from collection2
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().CollectionName, Is.EqualTo("collection2"));
    }

    #endregion
}

