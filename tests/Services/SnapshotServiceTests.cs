using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Configuration;
using Vigilante.Models;
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
        
        _snapshotManager = new SnapshotService(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _options,
            _logger);
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

        _collectionService
            .DeleteCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(true);

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
        await _collectionService.Received(1).DeleteCollectionSnapshotAsync(
            nodeUrl, collectionName, snapshotName, Arg.Any<CancellationToken>());
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
        await _collectionService.DidNotReceive().DeleteCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteSnapshotAsync_FromQdrantApi_Failure()
    {
        // Arrange
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";
        var nodeUrl = "http://test-node:6333";

        _collectionService
            .DeleteCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(false);

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
        await _collectionService.Received(1).DeleteCollectionSnapshotAsync(
            nodeUrl, collectionName, snapshotName, Arg.Any<CancellationToken>());
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

        _collectionService
            .CreateCollectionSnapshotAsync("http://node1:6333", collectionName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("snapshot1.snapshot"));

        _collectionService
            .CreateCollectionSnapshotAsync("http://node2:6333", collectionName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("snapshot2.snapshot"));

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

        _collectionService
            .DeleteCollectionSnapshotAsync(Arg.Any<string>(), collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _snapshotManager.DeleteCollectionSnapshotOnAllNodesAsync(collectionName, snapshotName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Values.All(v => v), Is.True);
        await _collectionService.Received(2).DeleteCollectionSnapshotAsync(
            Arg.Any<string>(), collectionName, snapshotName, Arg.Any<CancellationToken>());
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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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

        // Mock GetCollectionSnapshotsWithSizeAsync
        var snapshotsWithSize = new List<(string Name, long Size)>
        {
            ("collection1-1001-snapshot.snapshot", 2048)
        };

        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection1", Arg.Any<CancellationToken>())
            .Returns(snapshotsWithSize);

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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

        // Mock GetCollectionSnapshotsWithSizeAsync
        var snapshotsWithSize = new List<(string Name, long Size)>
        {
            ("collection1-1001-snapshot.snapshot", 2048)
        };

        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection1", Arg.Any<CancellationToken>())
            .Returns(snapshotsWithSize);

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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

        // Return snapshots from multiple nodes (simulating S3 storage)
        var snapshotsWithSize = new List<(string Name, long Size)>
        {
            ("collection1-1001-snapshot.snapshot", 2048), // Matches node1's PeerId
            ("collection1-1002-snapshot.snapshot", 2048), // Different PeerId - should be filtered out
            ("collection1-1003-snapshot.snapshot", 2048)  // Different PeerId - should be filtered out
        };

        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection1", Arg.Any<CancellationToken>())
            .Returns(snapshotsWithSize);

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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

        // Mock snapshots for collection1
        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection1", Arg.Any<CancellationToken>())
            .Returns(new List<(string Name, long Size)>
            {
                ("collection1-1001-snapshot.snapshot", 2048)
            });

        // Mock snapshots for collection2
        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection2", Arg.Any<CancellationToken>())
            .Returns(new List<(string Name, long Size)>
            {
                ("collection2-1001-snapshot.snapshot", 4096)
            });

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

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
        
        _clientFactory.CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
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
        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection1", Arg.Any<CancellationToken>())
            .Returns<List<(string, long)>>(_ => throw new Exception("Collection1 failed"));

        // collection2 succeeds
        _collectionService
            .GetCollectionSnapshotsWithSizeAsync("http://node1:6333", "collection2", Arg.Any<CancellationToken>())
            .Returns(new List<(string Name, long Size)>
            {
                ("collection2-1001-snapshot.snapshot", 4096)
            });

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(CancellationToken.None);

        // Assert - should still get snapshots from collection2
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().CollectionName, Is.EqualTo("collection2"));
    }

    #endregion
}

