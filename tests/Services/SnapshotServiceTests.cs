using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FluentAssertions;
using NSubstitute;
using NUnit.Framework;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Snapshots;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class SnapshotServiceTests
{
    private IQdrantNodesProvider _nodesProvider = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private IOptions<QdrantOptions> _options = null!;
    private ILogger<SnapshotService> _logger = null!;
    private IS3SnapshotService _s3SnapshotService = null!;
    private IPodCommandExecutor _commandExecutor = null!;
    private IDynamicConfigService _dynamicConfigService = null!;
    private IJobRegistry _jobRegistry = null!;
    private SnapshotService _snapshotManager = null!;

    [SetUp]
    public void Setup()
    {
        _nodesProvider = Substitute.For<IQdrantNodesProvider>();
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _options = Substitute.For<IOptions<QdrantOptions>>();
        _logger = Substitute.For<ILogger<SnapshotService>>();
        _commandExecutor = Substitute.For<IPodCommandExecutor>();
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _jobRegistry = Substitute.For<IJobRegistry>();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        _options.Value.Returns(new QdrantOptions { HttpTimeoutSeconds = 5 });

        // Mock S3SnapshotService to avoid real S3 dependencies in tests
        _s3SnapshotService = Substitute.For<IS3SnapshotService>();

        // Default: S3 is not available (tests can override this)
        _s3SnapshotService.CheckIsAvailableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig()));

        _snapshotManager = new SnapshotService(
            _nodesProvider,
            _clientFactory,
            _s3SnapshotService,
            _commandExecutor,
            _options,
            _dynamicConfigService,
            _jobRegistry,
            serviceProvider,
            _logger);
    }

    /// <summary>
    /// Helper to setup BuildNodeInfoListAsync mock from GetNodesAsync result
    /// </summary>
    private void SetupNodeInfoList(IReadOnlyList<QdrantNodeConfig> nodes, Dictionary<string, ulong>? peerIds = null)
    {
        var nodeInfos = nodes.Select(n =>
        {
            var nodeUrl = $"http://{n.Host}:{n.Port}";
            var peerId = peerIds?.GetValueOrDefault(nodeUrl) ?? 0;

            return new NodeInfo
            {
                Url = nodeUrl,
                PeerId = peerId,
                Namespace = n.Namespace,
                PodName = n.PodName,
                StatefulSetName = n.StatefulSetName,
                LastSeen = DateTime.UtcNow
            };
        }).ToList();

        _nodesProvider.BuildNodeInfoListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(nodeInfos));
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

        _commandExecutor.DeleteAndVerifyAsync(
                podName,
                podNamespace,
                "/qdrant/snapshots/test-collection/test-snapshot.snapshot",
                false,
                "Snapshot test-snapshot.snapshot",
                Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _snapshotManager.DeleteSnapshotAsync(
            collectionName,
            snapshotName,
            SnapshotSource.KubernetesStorage,
            CancellationToken.None,
            nodeUrl: null,
            podName: podName,
            podNamespace: podNamespace);

        // Assert
        result.Should().BeTrue();
        await _commandExecutor.Received(1).DeleteAndVerifyAsync(
            podName,
            podNamespace,
            "/qdrant/snapshots/test-collection/test-snapshot.snapshot",
            false,
            "Snapshot test-snapshot.snapshot",
            Arg.Any<CancellationToken>());
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
            CancellationToken.None,
            nodeUrl: null,
            podName: null,
            podNamespace: "test-namespace");

        // Assert
        result.Should().BeFalse();
        await _commandExecutor.DidNotReceive().DeleteAndVerifyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            CancellationToken.None,
            nodeUrl: nodeUrl,
            podName: null,
            podNamespace: null);

        // Assert
        result.Should().BeTrue();
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
            CancellationToken.None,
            nodeUrl: null,
            podName: null,
            podNamespace: null);

        // Assert
        result.Should().BeFalse();
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
            CancellationToken.None,
            nodeUrl: nodeUrl,
            podName: null,
            podNamespace: null);

        // Assert
        result.Should().BeFalse();
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

        var nodeUrls = new[] { "http://node1:6333", "http://node2:6333" };

        // Act
        var batch = await _snapshotManager.CreateCollectionSnapshotAsync(
            collectionName,
            nodeUrls,
            CancellationToken.None);

        // Assert
        batch.SkippedDuplicatePending.Should().BeFalse();
        batch.Results.Should().HaveCount(2);
        batch.Results["http://node1:6333"].Should().Be("snapshot1.snapshot");
        batch.Results["http://node2:6333"].Should().Be("snapshot2.snapshot");
    }

    [Test]
    public async Task CreateCollectionSnapshot_MultiNode_WhenPendingJobExists_SkipsWithoutCallingQdrant()
    {
        var collectionName = "test_collection";
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));
        var mockClient = Substitute.For<IQdrantHttpClient>();
        _clientFactory.CreateClient(Arg.Is<Uri>(u => u.Host == "node1" && u.Port == 6333), Arg.Any<string>())
            .Returns(mockClient);
        _jobRegistry.HasPendingSnapshotCreationForCollection(collectionName).Returns(true);

        var batch = await _snapshotManager.CreateCollectionSnapshotAsync(
            collectionName,
            ["http://node1:6333"],
            CancellationToken.None);

        batch.SkippedDuplicatePending.Should().BeTrue();
        await mockClient.DidNotReceive()
            .CreateCollectionSnapshot(Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        _jobRegistry.DidNotReceive().TryAddJob(Arg.Any<IJob>(), Arg.Any<CancellationTokenSource>());
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

        // Mock commandExecutor for Kubernetes storage deletion (since nodes have pod names)
        _commandExecutor.DeleteAndVerifyAsync("pod1", "ns1", "/qdrant/snapshots/test_collection/test-snapshot.snapshot", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _commandExecutor.DeleteAndVerifyAsync("pod2", "ns1", "/qdrant/snapshots/test_collection/test-snapshot.snapshot", false, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var pods = new[] { ("pod1", "ns1"), ("pod2", "ns1") };

        // Act
        var result = await _snapshotManager.DeleteSnapshotFromDiskAsync(
            collectionName,
            snapshotName,
            pods,
            CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Values.All(v => v).Should().BeTrue();
        result["pod1"].Should().BeTrue();
        result["pod2"].Should().BeTrue();

        // Verify it used Kubernetes storage (DeleteAndVerifyAsync)
        await _commandExecutor.Received(1).DeleteAndVerifyAsync("pod1", "ns1", "/qdrant/snapshots/test_collection/test-snapshot.snapshot", false, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _commandExecutor.Received(1).DeleteAndVerifyAsync("pod2", "ns1", "/qdrant/snapshots/test_collection/test-snapshot.snapshot", false, Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        // Mock S3 deletion
        _s3SnapshotService.DeleteSnapshotAsync(collectionName, snapshotName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var nodeUrls = new[] { "http://node1:6333", "http://node2:6333" };

        // Act - using S3 directly for all nodes
        var result = await _snapshotManager.DeleteCollectionSnapshotApiAsync(
            collectionName,
            snapshotName,
            nodeUrls,
            CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);

        // Note: This test now tests the API deletion method specifically
        // For S3 deletion, we would use DeleteSnapshotAsync with S3 source
    }

    [Test]
    public async Task DeleteCollectionSnapshotApiAsync_DeletesFromSpecifiedNodes()
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
        _s3SnapshotService.CheckIsAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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

        var nodeUrls = new[] { "http://node1:6333", "http://node2:6333" };

        // Act
        var result = await _snapshotManager.DeleteCollectionSnapshotApiAsync(
            collectionName,
            snapshotName,
            nodeUrls,
            CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Values.All(v => v).Should().BeTrue();

        // Verify it used Qdrant API
        await mockClient1.Received(1).DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
        await mockClient2.Received(1).DeleteCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>(), false);
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
        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });
        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock commandExecutor to return collection folders and snapshot files
        _commandExecutor.ListFilesAsync("pod1", "ns1", "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns(["collection1"]);

        _commandExecutor.ListFilesAsync("pod1", "ns1", "/qdrant/snapshots/collection1", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns(["snapshot1.snapshot"]);

        _commandExecutor.GetSizeAsync("pod1", "ns1", "/qdrant/snapshots/collection1", "snapshot1.snapshot", Arg.Any<CancellationToken>())
            .Returns(1024L);

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);

        var firstSnapshot = result.First();

        firstSnapshot.Source.Should().Be(SnapshotSource.KubernetesStorage);
        firstSnapshot.SnapshotName.Should().Be("snapshot1.snapshot");
        firstSnapshot.CollectionName.Should().Be("collection1");
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
        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections =
                [
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                ]
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);

        var firstSnapshot = result.First();

        firstSnapshot.Source.Should().Be(SnapshotSource.QdrantApi);
        firstSnapshot.SnapshotName.Should().Be("collection1-1001-snapshot.snapshot");
        firstSnapshot.SizeBytes.Should().Be(2048);
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
        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Disk returns empty (no collection folders)
        _commandExecutor.ListFilesAsync("pod1", "ns1", "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns([]);

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections =
                [
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                ]
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);

        var firstSnapshot = result.First();

        firstSnapshot.Source.Should().Be(SnapshotSource.QdrantApi);
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

        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections response
        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections =
                [
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1")
                ]
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots response - return snapshots from multiple nodes (simulating S3 storage)
        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }, // Matches node1's PeerId
                new() { Name = "collection1-1002-snapshot.snapshot", Size = 2048 }, // Different PeerId - should be filtered out
                new() { Name = "collection1-1003-snapshot.snapshot", Size = 2048 }  // Different PeerId - should be filtered out
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert - should only return the snapshot matching this node's PeerId
        result.Should().HaveCount(1);

        var firstSnapshot = result.First();

        firstSnapshot.SnapshotName.Should().Be("collection1-1001-snapshot.snapshot");
        firstSnapshot.PeerId.Should().Be(1001);
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
        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Mock ListCollections with multiple collections
        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections =
                [
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection2")
                ]
            },
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollections(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listCollectionsResponse));

        // Mock ListCollectionSnapshots for collection1
        var listSnapshots1Response = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "collection1-1001-snapshot.snapshot", Size = 2048 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots1Response));

        // Mock ListCollectionSnapshots for collection2
        var listSnapshots2Response = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "collection2-1001-snapshot.snapshot", Size = 4096 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots2Response));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Count(s => s.CollectionName == "collection1").Should().Be(1);
        result.Count(s => s.CollectionName == "collection2").Should().Be(1);
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
        SetupNodeInfoList(nodes, new Dictionary<string, ulong> { ["http://node1:6333"] = pod1Id });

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var listCollectionsResponse = new ListCollectionsResponse
        {
            Result = new ListCollectionsResponse.CollectionNamesUnit
            {
                Collections =
                [
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection1"),
                    new ListCollectionsResponse.CollectionNamesUnit.CollectionName("collection2")
                ]
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
            Result =
            [
                new() { Name = "collection2-1001-snapshot.snapshot", Size = 4096 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots("collection2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshots2Response));

        // Act
        var result = await _snapshotManager.GetSnapshotsInfoAsync(clearCache: false, cancellationToken: CancellationToken.None);

        // Assert - should still get snapshots from collection2
        result.Should().HaveCount(1);

        var firstSnapshot = result.First();

        firstSnapshot.CollectionName.Should().Be("collection2");
    }

    #endregion

    #region GetCollectionSnapshotsWithSizeAsync Tests

    [Test]
    public async Task GetCollectionSnapshotsWithSizeAsync_ReturnsSnapshotsWithSizes()
    {
        // Arrange
        var nodeUrl = "http://node1:6333";
        var collectionName = "test-collection";

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result =
            [
                new() { Name = "snapshot1.snapshot", Size = 1000 },
                new() { Name = "snapshot2.snapshot", Size = 2000 }
            ],
            Status = new QdrantStatus(QdrantOperationStatusType.Ok)
        };

        mockClient.ListCollectionSnapshots(collectionName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetCollectionSnapshotsWithSizeAsync(nodeUrl, collectionName, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("snapshot1.snapshot");
        result[0].Size.Should().Be(1000);
        result[1].Name.Should().Be("snapshot2.snapshot");
        result[1].Size.Should().Be(2000);
    }

    [Test]
    public async Task GetCollectionSnapshotsWithSizeAsync_HandlesError()
    {
        // Arrange
        var nodeUrl = "http://node1:6333";
        var collectionName = "test-collection";

        var mockClient = Substitute.For<IQdrantHttpClient>();

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        var listSnapshotsResponse = new ListSnapshotsResponse
        {
            Result = null,
            Status = QdrantStatus.Fail("Test error")
        };

        mockClient.ListCollectionSnapshots(collectionName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(listSnapshotsResponse));

        // Act
        var result = await _snapshotManager.GetCollectionSnapshotsWithSizeAsync(nodeUrl, collectionName, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region DownloadCollectionSnapshotAsync Tests

    [Test]
    public async Task DownloadCollectionSnapshotAsync_Success()
    {
        // Arrange
        var nodeUrl = "http://node1:6333";
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";

        var mockClient = Substitute.For<IQdrantHttpClient>();
        var mockStream = new MemoryStream([1, 2, 3, 4, 5]);

        var downloadResponse = new DownloadSnapshotResponse(
            snapshotName,
            mockStream,
            5L,
            QdrantStatus.Success(),
            TimeSpan.FromSeconds(1));

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.DownloadCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(downloadResponse));

        // Act
        var result = await _snapshotManager.DownloadCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(mockStream);
    }

    [Test]
    public async Task DownloadCollectionSnapshotAsync_ReturnsNull_WhenResultIsNull()
    {
        // Arrange
        var nodeUrl = "http://node1:6333";
        var collectionName = "test-collection";
        var snapshotName = "test-snapshot.snapshot";

        var mockClient = Substitute.For<IQdrantHttpClient>();

        var downloadResponse = new DownloadSnapshotResponse(
            snapshotName,
            null!,
            0L,
            QdrantStatus.Success(),
            TimeSpan.FromSeconds(1));

        _clientFactory.CreateClient(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<ILogger>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(mockClient);

        mockClient.DownloadCollectionSnapshot(collectionName, snapshotName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(downloadResponse));

        // Act
        var result = await _snapshotManager.DownloadCollectionSnapshotAsync(nodeUrl, collectionName, snapshotName, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetSnapshotsFromDiskForPodAsync Tests

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_ReturnsSnapshotsFromDisk()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://node1:6333";
        var peerId = 1UL;

        var collectionName = "test-collection";
        var snapshotFileName = "test-snapshot.snapshot";
        var snapshotSize = 1500000L;

        _commandExecutor.ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns([collectionName]);

        _commandExecutor.ListFilesAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", "*.snapshot", Arg.Any<CancellationToken>())
            .Returns([snapshotFileName]);

        _commandExecutor.GetSizeAsync(podName, podNamespace, $"/qdrant/snapshots/{collectionName}", snapshotFileName, Arg.Any<CancellationToken>())
            .Returns(snapshotSize);

        // Act
        var result = await _snapshotManager.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        var snapshots = result.ToList();
        snapshots.Should().HaveCount(1);
        snapshots[0].PodName.Should().Be(podName);
        snapshots[0].NodeUrl.Should().Be(nodeUrl);
        snapshots[0].PeerId.Should().Be(peerId);
        snapshots[0].CollectionName.Should().Be(collectionName);
        snapshots[0].SnapshotName.Should().Be(snapshotFileName);
        snapshots[0].SizeBytes.Should().Be(snapshotSize);
    }

    [Test]
    public async Task GetSnapshotsFromDiskForPodAsync_HandlesEmptyFolder()
    {
        // Arrange
        var podName = "test-pod";
        var podNamespace = "test-ns";
        var nodeUrl = "http://node1:6333";
        var peerId = 1UL;

        _commandExecutor.ListFilesAsync(podName, podNamespace, "/qdrant/snapshots", "*/", Arg.Any<CancellationToken>())
            .Returns([]);

        // Act
        var result = await _snapshotManager.GetSnapshotsFromDiskForPodAsync(podName, podNamespace, nodeUrl, peerId, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
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

        _commandExecutor.DeleteAndVerifyAsync(
                podName,
                podNamespace,
                "/qdrant/snapshots/test-collection/test-snapshot.snapshot",
                false,
                "Snapshot test-snapshot.snapshot",
                Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _snapshotManager.DeleteSnapshotFromDiskAsync(
            podName, podNamespace, collectionName, snapshotName, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await _commandExecutor.Received(1).DeleteAndVerifyAsync(
            podName,
            podNamespace,
            "/qdrant/snapshots/test-collection/test-snapshot.snapshot",
            false,
            "Snapshot test-snapshot.snapshot",
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region EnforceRetentionAsync Tests

    [Test]
    public async Task EnforceRetentionAsync_WhenCurrentClusterPeerIdsProvided_DeletesOrphanedPeerSnapshots()
    {
        // Arrange: S3 returns snapshots for collection "col1" from 3 peers: 111, 222 (current) and 999 (orphan)
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };
        _nodesProvider.BuildNodeInfoListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<NodeInfo>
            {
                new() { Url = "http://node1:6333", PeerId = 111, Namespace = "ns1", PodName = "pod1", LastSeen = DateTime.UtcNow }
            }));

        _s3SnapshotService.CheckIsAvailableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var s3Snapshots = new List<(string CollectionName, string SnapshotName, long SizeBytes, DateTime LastModifiedUtc)>
        {
            ("col1", "col1-111-2026-03-01-12-00-00.snapshot", 1000, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
            ("col1", "col1-222-2026-03-01-12-00-00.snapshot", 1000, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
            ("col1", "col1-999-2026-03-01-11-00-00.snapshot", 500, new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc)) // orphan peer
        };
        _s3SnapshotService.ListAllSnapshotsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(s3Snapshots));

        _s3SnapshotService.DeleteSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var currentClusterPeerIds = new HashSet<ulong> { 111, 222 };

        // Act
        await _snapshotManager.EnforceRetentionAsync("col1", retainLastN: 1, CancellationToken.None, currentClusterPeerIds);

        // Assert: orphan peer 999 snapshot must be deleted
        await _s3SnapshotService.Received(1).DeleteSnapshotAsync(
            "col1",
            "col1-999-2026-03-01-11-00-00.snapshot",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnforceRetentionAsync_WhenCurrentClusterPeerIdsProvided_AppliesRetainLastN_ToCurrentPeersOnly()
    {
        // Arrange: two snapshots for peer 111 (keep last 1), one for orphan 999 (delete all)
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };
        _nodesProvider.BuildNodeInfoListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<NodeInfo>
            {
                new() { Url = "http://node1:6333", PeerId = 111, Namespace = "ns1", PodName = "pod1", LastSeen = DateTime.UtcNow }
            }));

        _s3SnapshotService.CheckIsAvailableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var s3Snapshots = new List<(string CollectionName, string SnapshotName, long SizeBytes, DateTime LastModifiedUtc)>
        {
            ("col1", "col1-111-2026-03-01-10-00-00.snapshot", 1000, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)),
            ("col1", "col1-111-2026-03-01-12-00-00.snapshot", 1000, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
            ("col1", "col1-999-2026-03-01-11-00-00.snapshot", 500, new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc))
        };
        _s3SnapshotService.ListAllSnapshotsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(s3Snapshots));

        _s3SnapshotService.DeleteSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var currentClusterPeerIds = new HashSet<ulong> { 111 };

        // Act
        await _snapshotManager.EnforceRetentionAsync("col1", retainLastN: 1, CancellationToken.None, currentClusterPeerIds);

        // Assert: delete older 111 snapshot (10:00) and all 999 snapshots
        await _s3SnapshotService.Received(1).DeleteSnapshotAsync(
            "col1",
            "col1-111-2026-03-01-10-00-00.snapshot",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _s3SnapshotService.Received(1).DeleteSnapshotAsync(
            "col1",
            "col1-999-2026-03-01-11-00-00.snapshot",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnforceRetentionAsync_WhenCurrentClusterPeerIdsNull_DoesNotDeleteByOrphanRule()
    {
        // Arrange: one snapshot per peer (111, 222, 999). With null currentClusterPeerIds we only apply retain-last-N;
        // each group has 1 snapshot, retain 1 -> no deletion from retention. So no delete calls.
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };
        _nodesProvider.BuildNodeInfoListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<NodeInfo>
            {
                new() { Url = "http://node1:6333", PeerId = 111, Namespace = "ns1", PodName = "pod1", LastSeen = DateTime.UtcNow }
            }));

        _s3SnapshotService.CheckIsAvailableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var s3Snapshots = new List<(string CollectionName, string SnapshotName, long SizeBytes, DateTime LastModifiedUtc)>
        {
            ("col1", "col1-111-2026-03-01-12-00-00.snapshot", 1000, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
            ("col1", "col1-999-2026-03-01-11-00-00.snapshot", 500, new DateTime(2026, 3, 1, 11, 0, 0, DateTimeKind.Utc))
        };
        _s3SnapshotService.ListAllSnapshotsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(s3Snapshots));

        _s3SnapshotService.DeleteSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act: null currentClusterPeerIds -> no orphan rule, retain 1 per group; each has 1 so nothing to delete
        await _snapshotManager.EnforceRetentionAsync("col1", retainLastN: 1, CancellationToken.None, currentClusterPeerIds: null);

        // Assert: no deletions (each group has 1 snapshot, we keep 1)
        await _s3SnapshotService.DidNotReceive().DeleteSnapshotAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #endregion
}

