using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Concurrent;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Responses.Shared;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class ClusterManagerTests
{
    private IQdrantNodesProvider _nodesProvider = null!;
    private IOptions<QdrantOptions> _options = null!;
    private ILogger<ClusterManager> _logger = null!;
    private IMeterService _meterService = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private ICollectionService _collectionService = null!;
    private ISnapshotService _snapshotService = null!;
    private IDynamicConfigService _dynamicConfigService = null!;
    private IHostEnvironment _environment = null!;
    private IKubernetesManager _kubernetesManager = null!;
    private IJobRegistry _jobRegistry = null!;
    private TestDataProvider _testDataProvider = null!;
    private ClusterManager _clusterManager = null!;
    private ConcurrentDictionary<string, IQdrantHttpClient> _mockClients = null!;

    [SetUp]
    public void Setup()
    {
        _nodesProvider = Substitute.For<IQdrantNodesProvider>();
        _options = Substitute.For<IOptions<QdrantOptions>>();
        _logger = Substitute.For<ILogger<ClusterManager>>();
        _meterService = Substitute.For<IMeterService>();
        _clientFactory = Substitute.For<IQdrantClientFactory>();
        _collectionService = Substitute.For<ICollectionService>();
        _snapshotService = Substitute.For<ISnapshotService>();
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig()));
        _mockClients = new ConcurrentDictionary<string, IQdrantHttpClient>();

        _options.Value.Returns(new QdrantOptions { HttpTimeoutSeconds = 5 });

        // Create mock environment (Development by default for tests)
        _environment = Substitute.For<IHostEnvironment>();
        _environment.EnvironmentName.Returns(Environments.Development);

        // Create TestDataProvider with the same options and environment
        _testDataProvider = new TestDataProvider(_options, _environment);

        // Setup kubernetes manager
        _kubernetesManager = Substitute.For<IKubernetesManager>();
        _jobRegistry = Substitute.For<IJobRegistry>();

        // Setup collection service to always return healthy
        _collectionService
            .CheckCollectionsHealthAsync(Arg.Any<IQdrantHttpClient>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((true, (string?)null)));

        // Setup GetCollectionsFromQdrantAsync to return healthy status by default
        _collectionService
            .GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string, string, string?, string?)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(([], true, (string?)null));

        // Setup client factory to return mocked clients
        _clientFactory
            .CreateClient(
                Arg.Any<Uri>(),
                Arg.Any<string?>())
            .Returns(info =>
            {
                var uri = info.ArgAt<Uri>(0);
                var key = uri.Host + ":" + uri.Port;
                return _mockClients.GetOrAdd(key, _ => Substitute.For<IQdrantHttpClient>());
            });

        // Setup GetBasicNodeInfoAsync to return basic NodeInfo without calling GetClusterInfo
        // GetNodeInfoAsync will call GetClusterInfo separately to enrich the data
        _nodesProvider.GetBasicNodeInfoAsync(Arg.Any<QdrantNodeConfig>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var config = callInfo.ArgAt<QdrantNodeConfig>(0);
                var nodeUrl = $"http://{config.Host}:{config.Port}";

                return Task.FromResult(new NodeInfo
                {
                    Url = nodeUrl,
                    PeerId = string.Empty, // Will be populated by GetNodeInfoAsync
                    Namespace = config.Namespace,
                    PodName = config.PodName,
                    StatefulSetName = config.StatefulSetName,
                    LastSeen = DateTime.UtcNow
                });
            });

        _clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _snapshotService,
            _dynamicConfigService,
            _testDataProvider,
            _options,
            _jobRegistry,
            _logger,
            _meterService,
            _kubernetesManager);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenAllNodesHealthy_DetectsNoSplit()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;

        // Setup responses for each node - all nodes see all peers
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(3);
        result.Nodes.Count(n => n.IsHealthy).Should().Be(3);
        result.Nodes.All(n => n.Issues.Count == 0).Should().BeTrue();
        result.Nodes.Count(n => n.IsLeader).Should().Be(1);
        result.Nodes.Single(n => n.IsLeader).PodName.Should().Be("pod1");
    }

    [Test]
    public async Task GetClusterStateAsync_WhenClusterSplit_DetectsSplit()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;

        // First call: All nodes are healthy and see each other
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());

        // Initially all nodes see each other
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // First call establishes the baseline
        var firstResult = await _clusterManager.GetClusterStateAsync();
        firstResult.Nodes.Count(n => n.IsHealthy).Should().Be(3);

        // Now simulate a split: Group 1 (pod1, pod2) vs Group 2 (pod3 isolated)
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                        // pod3 is missing
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 2, Commit = 10 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                        // pod3 is missing
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 2, Commit = 10 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod3Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act - Second call should detect the split
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(3);
        // pod1 and pod2 form majority (2 nodes with same peer set), pod3 is isolated
        result.Nodes.Count(n => n.IsHealthy).Should().Be(2, "Expected 2 healthy nodes (majority group)");

        var unhealthyNode = result.Nodes.Single(n => !n.IsHealthy);
        unhealthyNode.PodName.Should().Be("pod3");
        unhealthyNode.ErrorType.Should().Be(NodeErrorType.ClusterSplit);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodeHasMessageSendFailures_DetectsSplit()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        var node1Key = nodes[0].Host + ":" + nodes[0].Port;
        var node1Client = _mockClients.GetOrAdd(node1Key, _ => Substitute.For<IQdrantHttpClient>());

        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());

        // First call: establish healthy baseline
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // First call establishes the baseline
        var firstResult = await _clusterManager.GetClusterStateAsync();
        firstResult.Nodes.Count(n => n.IsHealthy).Should().Be(2);

        // Second call: Node 1 sees node 2 but has message send failures, node 2 doesn't see node 1
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 2,
                        Commit = 10
                    },
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.MessageSendFailureUnit { LatestError = "Connection refused" } }
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod2Id, // Considers itself the leader
                        Term = 2,
                        Commit = 5
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(2);
        // Both nodes are inconsistent with the previous majority state, but one will be majority (the one that matches most)
        var unhealthyNodes = result.Nodes.Where(n => !n.IsHealthy).ToList();
        unhealthyNodes.Should().HaveCountGreaterThanOrEqualTo(1, "At least one node should be unhealthy");
        unhealthyNodes.Any(n => n.ErrorType == NodeErrorType.ClusterSplit).Should().BeTrue();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodeTimesOut_MarksNodeUnhealthy()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        // Node 1 responds normally
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Node 2 times out (never responds in time, gets fallback PeerId "node2:6333")
        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return new GetClusterInfoResponse
                {
                    Result = new GetClusterInfoResponse.ClusterInfo(),
                    Status = new QdrantStatus(QdrantOperationStatusType.Ok)
                };
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert: timing-out node is excluded from cluster state (not in majority), only the responding node is shown
        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].IsHealthy.Should().BeTrue();
        result.Nodes[0].PodName.Should().Be("pod1");
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodeReturnsError_MarksNodeUnhealthy()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Node throws exception
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].IsHealthy.Should().BeFalse();
        result.Nodes[0].ErrorType.Should().Be(NodeErrorType.ConnectionError);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodesHaveDifferentLeaders_DetectsSplit()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());

        // First call: establish baseline
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var firstResult = await _clusterManager.GetClusterStateAsync();
        firstResult.Nodes.Count(n => n.IsHealthy).Should().Be(3);

        // Now pod1 and pod2 have leader=pod1, pod3 has leader=pod3
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 2, Commit = 10 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 2, Commit = 10 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod3Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert - all nodes see same peers but pod3 has different leader
        // All nodes should be healthy as they agree on peer set
        result.Nodes.Should().HaveCount(3);
        result.Nodes.Count(n => n.IsHealthy).Should().Be(3);
        // But there should be 2 nodes claiming to be leaders (split brain with leaders)
        result.Nodes.Count(n => n.IsLeader).Should().BeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GetClusterStateAsync_UpdatesMeterService()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        await _clusterManager.GetClusterStateAsync();

        // Assert
        _meterService.Received(1).UpdateAliveNodes(2);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNoNodes_ReturnsEmptyState()
    {
        // Arrange
        var nodes = Array.Empty<QdrantNodeConfig>();

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().BeEmpty();
        _meterService.Received(1).UpdateAliveNodes(0);
    }

    [Test]
    public async Task GetClusterStateAsync_IncludesStatefulSetName_WhenAvailable()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "qdrant1-0" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        _nodesProvider.GetStatefulSetNameAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("qdrant1"));

        var pod1Id = 1001UL;
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
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

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.StatefulSetName.Should().Be("qdrant1");
    }

    [Test]
    public async Task GetClusterStateAsync_StatefulSetName_IsNull_WhenNotAvailable()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        _nodesProvider.GetStatefulSetNameAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var pod1Id = 1001UL;
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
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

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.StatefulSetName.Should().BeNull();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenAllNodesUnhealthy_ReturnsAllUnhealthy()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(2);
        result.Nodes.Count(n => n.IsHealthy).Should().Be(0);
        result.Nodes.All(n => n.ErrorType == NodeErrorType.ConnectionError).Should().BeTrue();
        _meterService.Received(1).UpdateAliveNodes(0);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodeHasNoPeerInfo_DoesNotIncludeInSplitDetection()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        // Node 1 has no peer info (standalone mode or disabled cluster)
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = null, // No peer information
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Node 2 has peer info
        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                    {
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(2);
        // Node 1 should still be healthy (no split detection without peer info)
        result.Nodes.Count(n => n.IsHealthy).Should().Be(2);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(
                    [new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }]);
            });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Func<Task> act = async () => await _clusterManager.GetClusterStateAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task GetClusterStateAsync_With5NodeCluster_DetectsMajorityAndMinority()
    {
        // Arrange - 5 node cluster with 3-2 split
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" },
            new QdrantNodeConfig { Host = "node4", Port = 6333, Namespace = "ns1", PodName = "pod4" },
            new QdrantNodeConfig { Host = "node5", Port = 6333, Namespace = "ns1", PodName = "pod5" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;
        var pod4Id = 1004UL;
        var pod5Id = 1005UL;

        // Majority group: pod1, pod2, pod3 (all see each other)
        var majorityPeers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        // Minority group: pod4, pod5 (only see each other)
        var minorityPeers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod4Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod5Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        // Setup majority nodes
        for (int i = 1; i <= 3; i++)
        {
            var mockClient = _mockClients.GetOrAdd($"node{i}:6333", _ => Substitute.For<IQdrantHttpClient>());
            var peerId = i == 1 ? pod1Id : i == 2 ? pod2Id : pod3Id;
            mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetClusterInfoResponse
                {
                    Result = new GetClusterInfoResponse.ClusterInfo
                    {
                        PeerId = peerId,
                        Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(majorityPeers.Where(kv => kv.Key != peerId.ToString())),
                        RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                        {
                            Leader = pod1Id,
                            Term = 2,
                            Commit = 100
                        }
                    },
                    Status = new QdrantStatus(QdrantOperationStatusType.Ok)
                }));
        }

        // Setup minority nodes
        for (int i = 4; i <= 5; i++)
        {
            var mockClient = _mockClients.GetOrAdd($"node{i}:6333", _ => Substitute.For<IQdrantHttpClient>());
            var peerId = i == 4 ? pod4Id : pod5Id;
            mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new GetClusterInfoResponse
                {
                    Result = new GetClusterInfoResponse.ClusterInfo
                    {
                        PeerId = peerId,
                        Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(minorityPeers.Where(kv => kv.Key != peerId.ToString())),
                        RaftInfo = new GetClusterInfoResponse.RaftInfoUnit
                        {
                            Leader = pod4Id,
                            Term = 2,
                            Commit = 50
                        }
                    },
                    Status = new QdrantStatus(QdrantOperationStatusType.Ok)
                }));
        }

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(5);
        // Majority group (3 nodes) should be healthy, minority (2 nodes) should be unhealthy
        result.Nodes.Count(n => n.IsHealthy).Should().Be(3, "Expected 3 healthy nodes (majority)");
        result.Nodes.Count(n => !n.IsHealthy).Should().Be(2, "Expected 2 unhealthy nodes (minority)");

        var unhealthyNodes = result.Nodes.Where(n => !n.IsHealthy).ToList();
        unhealthyNodes.All(n => n.ErrorType == NodeErrorType.ClusterSplit).Should().BeTrue();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNodeReturnsNullResult_MarksNodeUnhealthy()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = null, // Invalid - no result
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].IsHealthy.Should().BeFalse();
        result.Nodes[0].ErrorType.Should().Be(NodeErrorType.InvalidResponse);
    }

    #region Collection Issues Tests

    [Test]
    public async Task GetCollectionsInfoAsync_AlwaysFetchesFromApiFirst()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "test_collection",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", MetricConstants.NotAvailableValue },
                    { "sizeBytes", 0L }
                }
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        await _collectionService.Received(1).GetEnrichedCollectionsInfoAsync(
            Arg.Any<IReadOnlyList<NodeInfo>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>(),
                Arg.Any<bool>());

        result.Should().HaveCount(1);
        result[0].CollectionName.Should().Be("test_collection");
    }

    [Test]
    public async Task GetCollectionsInfoAsync_WhenCollectionExistsInApiButNotInStorage_AddsIssue()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        // API returns a collection
        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "missing_from_storage",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", MetricConstants.NotAvailableValue },
                    { "sizeBytes", 0L }
                },
                Issues = ["Collection exists in API but not found in storage"]
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Storage returns empty (collection not found in storage)
        _collectionService.GetCollectionsSizesForPodAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Issues.Should().HaveCount(1);
        result[0].Issues[0].Should().Be("Collection exists in API but not found in storage");
        result[0].Metrics.PrettySize.Should().Be(MetricConstants.NotAvailableValue);
        result[0].Metrics.SizeBytes.Should().Be(0L);
    }

    [Test]
    public async Task GetCollectionsInfoAsync_WhenCollectionExistsInBothApiAndStorage_EnrichesWithStorageData()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        // API returns a collection
        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "test_collection",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", "1 GB" },
                    { "sizeBytes", 1073741824L }
                }
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Storage returns the same collection with size info
        var storageCollections = new List<CollectionSize>
        {
            new()
            {
                CollectionName = "test_collection",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                SizeBytes = 1073741824L // 1 GB
            }
        };

        _collectionService.GetCollectionsSizesForPodAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(storageCollections);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Issues.Should().HaveCount(0, "Should have no issues when collection exists in both API and storage");
        result[0].Metrics.PrettySize.Should().Be("1 GB");
        result[0].Metrics.SizeBytes.Should().Be(1073741824L);
    }

    [Test]
    public async Task GetCollectionsInfoAsync_WithMultipleCollections_CorrectlyIdentifiesIssues()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        // API returns 3 collections
        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "collection_in_both",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", "476.84 MB" },
                    { "sizeBytes", 500000000L }
                }
            },
            new()
            {
                CollectionName = "collection_missing_from_storage",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", MetricConstants.NotAvailableValue },
                    { "sizeBytes", 0L }
                },
                Issues = ["Collection exists in API but not found in storage"]
            },
            new()
            {
                CollectionName = "another_in_both",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", "715.26 MB" },
                    { "sizeBytes", 750000000L }
                }
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Storage returns only 2 collections (one is missing)
        var storageCollections = new List<CollectionSize>
        {
            new()
            {
                CollectionName = "collection_in_both",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                SizeBytes = 500000000L
            },
            new()
            {
                CollectionName = "another_in_both",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                SizeBytes = 750000000L
            }
        };

        _collectionService.GetCollectionsSizesForPodAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(storageCollections);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        result.Should().HaveCount(3);

        var collectionInBoth = result.First(c => c.CollectionName == "collection_in_both");
        collectionInBoth.Issues.Should().HaveCount(0);
        collectionInBoth.Metrics.SizeBytes.Should().Be(500000000L);

        var collectionMissing = result.First(c => c.CollectionName == "collection_missing_from_storage");
        collectionMissing.Issues.Should().HaveCount(1);
        collectionMissing.Issues[0].Should().Be("Collection exists in API but not found in storage");
        collectionMissing.Metrics.PrettySize.Should().Be(MetricConstants.NotAvailableValue);

        var anotherInBoth = result.First(c => c.CollectionName == "another_in_both");
        anotherInBoth.Issues.Should().HaveCount(0);
        anotherInBoth.Metrics.SizeBytes.Should().Be(750000000L);
    }

    [Test]
    public async Task GetCollectionsInfoAsync_WhenNoCollectionsFromApi_ReturnsTestData()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        // API returns no collections
        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns([]);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert - should return test data
        result.Should().HaveCountGreaterThan(0, "Should return test data when no collections from API");
        result.Any(c => c.CollectionName.Contains("test") || c.CollectionName.Contains("product")).Should().BeTrue("Test data should contain standard test collections");
    }

    [Test]
    public async Task GetCollectionsInfoAsync_WithMultipleNodes_HandlesStorageCorrectly()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // API returns collections from both nodes
        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "collection1",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", "953.67 MB" },
                    { "sizeBytes", 1000000000L }
                }
            },
            new()
            {
                CollectionName = "collection1",
                NodeUrl = "http://node2:6333",
                PodName = "pod2",
                PeerId = "1002",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", MetricConstants.NotAvailableValue },
                    { "sizeBytes", 0L }
                },
                Issues = ["Collection exists in API but not found in storage"]
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Storage returns size for node1 but not node2
        _collectionService.GetCollectionsSizesForPodAsync(
                "pod1",
                Arg.Any<string>(),
                "http://node1:6333",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new()
                {
                    CollectionName = "collection1",
                    NodeUrl = "http://node1:6333",
                    PodName = "pod1",
                    PeerId = "1001",
                    SizeBytes = 1000000000L
                }
            ]);

        _collectionService.GetCollectionsSizesForPodAsync(
                "pod2",
                Arg.Any<string>(),
                "http://node2:6333",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        result.Should().HaveCount(2);

        var node1Collection = result.First(c => c.NodeUrl == "http://node1:6333");
        node1Collection.Issues.Should().HaveCount(0);
        node1Collection.Metrics.SizeBytes.Should().Be(1000000000L);

        var node2Collection = result.First(c => c.NodeUrl == "http://node2:6333");
        node2Collection.Issues.Should().HaveCount(1);
        node2Collection.Issues[0].Should().Be("Collection exists in API but not found in storage");
    }

    [Test]
    public async Task GetCollectionsInfoAsync_UsesCacheOnSecondCall()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        var apiCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "test_collection",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics
                {
                    { "prettySize", "1 MB" },
                    { "sizeBytes", 1048576L }
                }
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(apiCollections);

        // Act - First call
        var result1 = await _clusterManager.GetCollectionsInfoAsync(clearCache: false);

        // Act - Second call (ClusterManager will call CollectionService again, but CollectionService handles caching internally)
        var result2 = await _clusterManager.GetCollectionsInfoAsync(clearCache: false);

        // Assert - ClusterManager calls CollectionService twice (caching is now handled inside CollectionService)
        await _collectionService.Received(2).GetEnrichedCollectionsInfoAsync(
            Arg.Any<IReadOnlyList<NodeInfo>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>(),
            false); // Both calls should have clearCache=false

        result1.Should().HaveCount(1);
        result2.Should().HaveCount(1);
        result1[0].CollectionName.Should().Be("test_collection");
        result2[0].CollectionName.Should().Be("test_collection");
    }

    [Test]
    public async Task GetCollectionsInfoAsync_ClearCacheRefreshesData()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        var firstCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "collection1",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics { { "sizeBytes", 1000L } }
            }
        };

        var secondCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "collection2",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics { { "sizeBytes", 2000L } }
            }
        };

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(firstCollections, secondCollections);

        // Act - First call
        var result1 = await _clusterManager.GetCollectionsInfoAsync(clearCache: false);

        // Act - Second call with clearCache=true
        var result2 = await _clusterManager.GetCollectionsInfoAsync(clearCache: true);

        // Assert - First call should have clearCache=false
        await _collectionService.Received(1).GetEnrichedCollectionsInfoAsync(
            Arg.Any<IReadOnlyList<NodeInfo>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>(),
            false);

        // Assert - Second call should have clearCache=true
        await _collectionService.Received(1).GetEnrichedCollectionsInfoAsync(
            Arg.Any<IReadOnlyList<NodeInfo>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>(),
            true);

        result1[0].CollectionName.Should().Be("collection1");
        result2[0].CollectionName.Should().Be("collection2");
    }

    [Test]
    public async Task GetCollectionsInfoAsync_ClearsCacheWhenEmptyResultWithClearCache()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
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

        var initialCollections = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "collection1",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new CollectionMetrics { { "sizeBytes", 1000L } }
            }
        };

        var emptyCollections = new List<CollectionInfo>();

        _collectionService.GetEnrichedCollectionsInfoAsync(
                Arg.Any<IReadOnlyList<NodeInfo>>(),
                Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(initialCollections, emptyCollections, emptyCollections);

        // Act
        // First call - gets real collections and caches them
        var result1 = await _clusterManager.GetCollectionsInfoAsync(clearCache: false);

        // Second call - clearCache=true but gets empty result, should clear cache and return test data
        var result2 = await _clusterManager.GetCollectionsInfoAsync(clearCache: true);

        // Third call - clearCache=false, should NOT return stale cache (it was cleared), should fetch again
        var result3 = await _clusterManager.GetCollectionsInfoAsync(clearCache: false);

        // Assert
        result1[0].CollectionName.Should().Be("collection1", "First call should return real collection");
        result2[0].CollectionName.Should().StartWith("test_", "Second call should return test data");
        result3[0].CollectionName.Should().StartWith("test_", "Third call should return test data (cache was cleared)");

        // Service should be called 3 times - caching is handled inside CollectionService
        await _collectionService.Received(3).GetEnrichedCollectionsInfoAsync(
            Arg.Any<IReadOnlyList<NodeInfo>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>());
    }

    #endregion

    #region DeleteCollectionViaApiOnAllNodesAsync Tests

    [Test]
    public async Task DeleteCollectionViaApiOnAllNodesAsync_DeletesFromAllNodes()
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

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        _collectionService
            .DeleteCollectionViaApiAsync(Arg.Any<string>(), collectionName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var nodeUrls = new[] { "http://node1:6333", "http://node2:6333" };

        // Act
        var result = await _clusterManager.DeleteCollectionViaApiAsync(
            collectionName,
            nodeUrls,
            cancellationToken: CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Values.All(v => v).Should().BeTrue();
    }

    #endregion

    #region ReplicateShardsAsync Tests

    [Test]
    public async Task ReplicateShardsAsync_WhenSuccessful_ReturnsTrue()
    {
        // Arrange
        var sourcePeerId = 1001UL;
        var targetPeerId = 1002UL;
        var collectionName = "test_collection";
        var shardIds = new uint[] { 0, 1 };

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        _collectionService
            .ReplicateShardsAsync(
                Arg.Any<string>(),
                sourcePeerId,
                targetPeerId,
                collectionName,
                shardIds,
                false,
                Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _clusterManager.ReplicateShardsAsync(
            sourcePeerId, targetPeerId, collectionName, shardIds, false, null, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task ReplicateShardsAsync_WithMoveShards_Works()
    {
        // Arrange
        var sourcePeerId = 1001UL;
        var targetPeerId = 1002UL;
        var collectionName = "test_collection";
        var shardIds = new uint[] { 0 };

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        _collectionService
            .ReplicateShardsAsync(
                Arg.Any<string>(),
                sourcePeerId,
                targetPeerId,
                collectionName,
                shardIds,
                true, // isMove = true
                Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _clusterManager.ReplicateShardsAsync(
            sourcePeerId, targetPeerId, collectionName, shardIds, true, null, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await _collectionService.Received(1).ReplicateShardsAsync(
            Arg.Any<string>(),
            sourcePeerId,
            targetPeerId,
            collectionName,
            shardIds,
            true,
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShardsAsync_WhenNoHealthyNodes_ReturnsFalse()
    {
        // Arrange
        var sourcePeerId = 1001UL;
        var targetPeerId = 1002UL;
        var collectionName = "test_collection";
        var shardIds = new uint[] { 0 };

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        // Node is unhealthy (returns error)
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        // Act
        var result = await _clusterManager.ReplicateShardsAsync(
            sourcePeerId, targetPeerId, collectionName, shardIds, false, null, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region RemovePeerAsync Tests

    [Test]
    public async Task RemovePeerAsync_WhenHealthyNodeExists_ReturnsTrue()
    {
        var peerId = 1001UL;
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient.RemovePeer(
                Arg.Any<ulong>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(new DefaultOperationResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var result = await _clusterManager.RemovePeerAsync(peerId, false, null, CancellationToken.None);

        result.Should().BeTrue();
        await mockClient.Received(1).RemovePeer(
            peerId,
            Arg.Any<CancellationToken>(),
            false,
            null,
            Arg.Any<string?>());
    }

    [Test]
    public async Task RemovePeerAsync_WithForceDropAndTimeout_CallsClientWithCorrectParams()
    {
        var peerId = 2002UL;
        var timeout = TimeSpan.FromSeconds(60);
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient.RemovePeer(
                Arg.Any<ulong>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(new DefaultOperationResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var result = await _clusterManager.RemovePeerAsync(peerId, true, timeout, CancellationToken.None);

        result.Should().BeTrue();
        await mockClient.Received(1).RemovePeer(
            peerId,
            Arg.Any<CancellationToken>(),
            true,
            timeout,
            Arg.Any<string?>());
    }

    [Test]
    public async Task RemovePeerAsync_WhenNoHealthyNode_ReturnsFalse()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        var result = await _clusterManager.RemovePeerAsync(1001UL, false, null, CancellationToken.None);

        result.Should().BeFalse();
        await mockClient.DidNotReceive().RemovePeer(
            Arg.Any<ulong>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task RemovePeerAsync_WhenClientReturnsError_ReturnsFalse()
    {
        var peerId = 1001UL;
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient.RemovePeer(
                Arg.Any<ulong>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(new DefaultOperationResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "Peer has shards" }
            }));

        var result = await _clusterManager.RemovePeerAsync(peerId, false, null, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Test]
    public async Task RemovePeerAsync_WhenClientThrows_ReturnsFalse()
    {
        var peerId = 1001UL;
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = 1001UL,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = 1001UL, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient.RemovePeer(
                Arg.Any<ulong>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<string?>())
            .Returns(Task.FromException<DefaultOperationResponse>(new HttpRequestException("Timeout")));

        var result = await _clusterManager.RemovePeerAsync(peerId, false, null, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Test]
    public async Task RemovePeerAsync_WhenSuccess_ExcludedPeerNotShownInNextGetClusterState()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };
        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var clusterInfoThreePeers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient1.RemovePeer(Arg.Any<ulong>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new DefaultOperationResponse { Status = new QdrantStatus(QdrantOperationStatusType.Ok) }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var removeResult = await _clusterManager.RemovePeerAsync(pod3Id, false, null, CancellationToken.None);
        removeResult.Should().BeTrue();

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Should().HaveCount(2);
        state.Nodes.Select(n => n.PeerId).Should().NotContain(pod3Id.ToString());
        state.Nodes.Select(n => n.PeerId).Should().Contain(pod1Id.ToString());
        state.Nodes.Select(n => n.PeerId).Should().Contain(pod2Id.ToString());
    }

    [Test]
    public async Task RemovePeerAsync_WhenFails_ExcludedPeerStillShownInGetClusterState()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };
        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;
        var clusterInfoThreePeers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        mockClient1.RemovePeer(Arg.Any<ulong>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new DefaultOperationResponse { Status = new QdrantStatus(QdrantOperationStatusType.Error) { Error = "Peer has shards" } }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(clusterInfoThreePeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var removeResult = await _clusterManager.RemovePeerAsync(pod3Id, false, null, CancellationToken.None);
        removeResult.Should().BeFalse();

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Should().HaveCount(3);
        state.Nodes.Select(n => n.PeerId).Should().Contain(pod3Id.ToString());
    }

    #endregion

    #region GetClusterStateAsync Excluded and out-of-cluster filtering

    [Test]
    public async Task GetClusterStateAsync_WhenNodeNotInMajority_FiltersOutNonRespondingNode()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "pod3" }
        };
        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var peersTwo = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = peersTwo,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = peersTwo,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Should().HaveCount(2);
        state.Nodes.Select(n => n.PeerId).Should().Contain(pod1Id.ToString());
        state.Nodes.Select(n => n.PeerId).Should().Contain(pod2Id.ToString());
        state.Nodes.Any(n => n.PeerId == "node3:6333").Should().BeFalse();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenNoMajority_DoesNotFilterByMajority()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new HttpRequestException("Connection refused"));

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Should().HaveCount(1);
        state.Nodes[0].PeerId.Should().Be("node1:6333");
        state.Nodes[0].IsHealthy.Should().BeFalse();
    }

    #endregion

    #region MessageSendFailures with Timestamp Tests

    [Test]
    public async Task GetClusterStateAsync_WithStaleMessageSendFailures_AddsWarningNotError()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var consensusUpdateTime = DateTime.UtcNow;
        var staleErrorTime = consensusUpdateTime.AddMinutes(-5); // 5 minutes before consensus update

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = consensusUpdateTime,
                        Err = null
                    },
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        ["1002"] = new GetClusterInfoResponse.MessageSendFailureUnit
                        {
                            Count = 3,
                            LatestError = "Connection timeout",
                            LatestErrorTimestamp = staleErrorTime
                        }
                    },
                    Peers = []
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];
        node.IsHealthy.Should().BeTrue("Node should be healthy - stale failures don't mark it unhealthy");
        node.ErrorType.Should().Be(NodeErrorType.None, "Node should have no error type");
        node.Warnings.Should().HaveCount(1, "Node should have one warning");
        node.Warnings[0].Should().Contain("Stale message send failures");
        node.Warnings[0].Should().Contain("1002");
        node.Warnings[0].Should().Contain("Connection timeout");
    }

    [Test]
    public async Task GetClusterStateAsync_WithActiveMessageSendFailures_MarksNodeUnhealthy()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var consensusUpdateTime = DateTime.UtcNow.AddMinutes(-5);
        var recentErrorTime = DateTime.UtcNow; // Recent error after consensus update

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = consensusUpdateTime,
                        Err = null
                    },
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        ["1002"] = new GetClusterInfoResponse.MessageSendFailureUnit
                        {
                            Count = 5,
                            LatestError = "Network unreachable",
                            LatestErrorTimestamp = recentErrorTime
                        }
                    },
                    Peers = []
                }
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        result.Nodes.Should().HaveCount(1);
        var node = result.Nodes[0];
        node.IsHealthy.Should().BeFalse("Node should be unhealthy - active failures mark it unhealthy");
        node.ErrorType.Should().Be(NodeErrorType.MessageSendFailures);
        node.Issues.Should().HaveCountGreaterThan(0, "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        issuesText.Should().Contain("Message send failures");
        issuesText.Should().Contain("1002");
        issuesText.Should().Contain("Network unreachable");
    }

    [Test]
    public async Task GetClusterStateAsync_WithMixedMessageSendFailures_SeparatesActiveAndStale()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var consensusUpdateTime = DateTime.UtcNow;
        var staleErrorTime = consensusUpdateTime.AddHours(-1);
        var recentErrorTime = consensusUpdateTime.AddMinutes(2);

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = consensusUpdateTime,
                        Err = null
                    },
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        ["1002"] = new()
                        {
                            Count = 3,
                            LatestError = "Old timeout",
                            LatestErrorTimestamp = staleErrorTime
                        },
                        ["1003"] = new()
                        {
                            Count = 2,
                            LatestError = "Current error",
                            LatestErrorTimestamp = recentErrorTime
                        }
                    },
                    Peers = []
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];

        node.IsHealthy.Should().BeFalse("Node should be unhealthy due to recent failure");
        node.ErrorType.Should().Be(NodeErrorType.MessageSendFailures);
        node.Issues.Should().HaveCountGreaterThan(0, "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        issuesText.Should().Contain("1003", "Issues should mention peer with recent failure");
        issuesText.Should().Contain("Current error");
        issuesText.Should().NotContain("1002", "Issues should not mention peer with stale failure");
    }

    [Test]
    public async Task GetClusterStateAsync_WithNoConsensusTimestamp_TreatsAllFailuresAsActive()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var errorTime = DateTime.UtcNow.AddMinutes(-10);

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    ConsensusThreadStatus = null, // No consensus status
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        ["1002"] = new()
                        {
                            Count = 3,
                            LatestError = "Connection failed",
                            LatestErrorTimestamp = errorTime
                        }
                    },
                    Peers = []
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];

        node.IsHealthy.Should().BeFalse("Without consensus timestamp, all failures are treated as active");
        node.ErrorType.Should().Be(NodeErrorType.MessageSendFailures);
        node.Issues.Should().HaveCountGreaterThan(0, "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        issuesText.Should().Contain("Message send failures");
        issuesText.Should().Contain("1002");
        issuesText.Should().Contain("Connection failed");
    }

    [Test]
    public async Task GetClusterStateAsync_StaleFailuresAppearInClusterHealthIssues()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var consensusUpdateTime = DateTime.UtcNow;
        var staleErrorTime = consensusUpdateTime.AddHours(-1);

        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId },
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = consensusUpdateTime,
                        Err = null
                    },
                    MessageSendFailures = new Dictionary<string, GetClusterInfoResponse.MessageSendFailureUnit>
                    {
                        ["1002"] = new()
                        {
                            Count = 5,
                            LatestError = "Historical timeout",
                            LatestErrorTimestamp = staleErrorTime
                        }
                    },
                    Peers = []
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Health.IsHealthy.Should().BeTrue("Cluster should be healthy");
        state.Health.Issues.Should().HaveCount(0, "Should have no issues (stale failures go to warnings)");
        state.Health.Warnings.Should().HaveCount(1, "Should have 1 warning for stale failures");
        state.Health.Warnings[0].Should().Contain("pod1");
        state.Health.Warnings[0].Should().Contain("Stale message send failures");
    }

    #endregion

    #region Kubernetes Warnings Integration Tests

    [Test]
    public async Task GetClusterStateAsync_WhenClusterDegraded_ShouldFetchKubernetesWarnings()
    {
        // Arrange
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        var clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _snapshotService,
            _dynamicConfigService,
            _testDataProvider,
            _options,
            _jobRegistry,
            _logger,
            _meterService,
            kubernetesManager);

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "qdrant", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        // Setup node1 as healthy (sees full cluster)
        var node1Key = nodes[0].Host + ":" + nodes[0].Port;
        var node1Client = _mockClients.GetOrAdd(node1Key, _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Setup node2 as unhealthy: responds but with different cluster view (split) so it gets marked inconsistent
        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = [], // only self, so CurrentPeerIds = {1002}
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod2Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Setup Kubernetes warnings
        var k8sWarnings = new List<string>
        {
            "[2024-12-05 10:00:00] Pod/qdrant-1: BackOff - Back-off restarting failed container",
            "[2024-12-05 09:58:00] Pod/qdrant-1: Unhealthy - Readiness probe failed"
        };
        kubernetesManager.GetWarningEventsAsync("qdrant", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(k8sWarnings));

        // Act
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Status.Should().Be(ClusterStatus.Degraded, "Cluster should be degraded");

        // Verify Kubernetes warnings were fetched
        await kubernetesManager.Received(1).GetWarningEventsAsync("qdrant", Arg.Any<CancellationToken>());

        // Verify warnings were added to a node
        var nodeWithWarnings = state.Nodes.FirstOrDefault(n => n.Warnings.Count != 0);
        nodeWithWarnings.Should().NotBeNull("At least one node should have warnings");
        nodeWithWarnings!.Warnings.Count.Should().Be(2, "Should have 2 K8s warnings");
        nodeWithWarnings.Warnings[0].Should().Contain("K8s Event:");
        nodeWithWarnings.Warnings[0].Should().Contain("BackOff");

        // Verify warnings appear in ClusterHealth.Warnings
        state.Health.Warnings.Should().HaveCount(2, "ClusterHealth should have 2 warnings");
        state.Health.Warnings[0].Should().Contain("K8s Event:");
        state.Health.Warnings[0].Should().Contain("BackOff");
    }

    [Test]
    public async Task GetClusterStateAsync_WhenClusterHealthy_ShouldNotFetchKubernetesWarnings()
    {
        // Arrange
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        var clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _snapshotService,
            _dynamicConfigService,
            _testDataProvider,
            _options,
            _jobRegistry,
            _logger,
            _meterService,
            kubernetesManager);

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "qdrant", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        // Setup both nodes as healthy
        var node1Key = nodes[0].Host + ":" + nodes[0].Port;
        var node1Client = _mockClients.GetOrAdd(node1Key, _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Status.Should().Be(ClusterStatus.Healthy, "Cluster should be healthy");

        // Verify Kubernetes warnings were NOT fetched
        await kubernetesManager.DidNotReceive().GetWarningEventsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetClusterStateAsync_WhenClusterDegradedButNoK8sManager_ShouldNotThrow()
    {
        // Arrange - No Kubernetes manager (null)
        var clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _snapshotService,
            _dynamicConfigService,
            _testDataProvider,
            _options,
            _jobRegistry,
            _logger,
            _meterService,
            null); // No Kubernetes manager

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "qdrant", PodName = "pod2" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;

        // Setup node1 as healthy (sees full cluster)
        var node1Key = nodes[0].Host + ":" + nodes[0].Port;
        var node1Client = _mockClients.GetOrAdd(node1Key, _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Setup node2 as unhealthy: responds but with different cluster view (split) so it gets marked inconsistent
        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod2Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act & Assert - should not throw
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Status.Should().Be(ClusterStatus.Degraded, "Cluster should be degraded");
        state.Nodes.All(n => n.Warnings.Count == 0).Should().BeTrue("No warnings should be added without K8s manager");
    }

    [Test]
    public async Task GetClusterStateAsync_WhenK8sManagerThrows_ShouldContinueGracefully()
    {
        // Arrange
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        var clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _snapshotService,
            _dynamicConfigService,
            _testDataProvider,
            _options,
            _jobRegistry,
            _logger,
            _meterService,
            kubernetesManager);

        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "qdrant", PodName = "pod2" },
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "qdrant", PodName = "pod3" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var pod2Id = 1002UL;
        var pod3Id = 1003UL;
        var majorityPeers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
        {
            { pod2Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
            { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
        };

        // Setup node1 and node2 as healthy (same view = majority)
        var node1Client = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod1Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(majorityPeers),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));
        var node2Client = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod2Id,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { pod1Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { pod3Id.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Setup node3 as unhealthy: different view (split) so it gets marked inconsistent
        var node3Client = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        node3Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = pod3Id,
                    Peers = [],
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod3Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Setup Kubernetes manager to throw exception
        kubernetesManager.GetWarningEventsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<List<string>>(_ => throw new Exception("K8s API Error"));

        // Act & Assert - should not throw, should handle exception gracefully
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Status.Should().Be(ClusterStatus.Degraded, "Cluster should be degraded");
        // Should still return valid state even if K8s warnings fetch failed
        state.Nodes.Should().HaveCount(3);
    }

    #endregion

    #region Qdrant ReportIssues Tests

    [Test]
    public async Task GetClusterStateAsync_WithQdrantIssues_ShouldAddIssuesToNode()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId },
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = DateTime.UtcNow,
                        Err = null
                    },
                    Peers = []
                }
            }));

        // Mock ReportIssues response
        var reportIssuesResponse = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint
            {
                Issues =
                [
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "disk_usage",
                        Description = "Disk usage is above 80%",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    },
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "memory_usage",
                        Description = "Memory usage is above 90%",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    }
                ]
            }
        };

#pragma warning disable QD0001
        mockClient.ReportIssues(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reportIssuesResponse));
#pragma warning restore QD0001

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];
        node.IsHealthy.Should().BeTrue("Node should be healthy even with Qdrant issues (they are informational)");
        node.Issues.Should().HaveCount(2, "Should have 2 issues from Qdrant");
        node.Issues[0].Should().Be("[disk_usage] Disk usage is above 80%");
        node.Issues[1].Should().Be("[memory_usage] Memory usage is above 90%");
    }

    [Test]
    public async Task GetClusterStateAsync_WithQdrantIssuesWithoutValues_ShouldAddIssuesWithKeyOnly()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId },
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = DateTime.UtcNow,
                        Err = null
                    },
                    Peers = []
                }
            }));

        // Mock ReportIssues response with issues with empty/null values
        var reportIssuesResponse = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint
            {
                Issues =
                [
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = null,
                        Description = null,
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    },
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "",
                        Description = "",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    },
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "   ",
                        Description = "   ",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    },
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "disk_usage",
                        Description = "  High  ",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = null,
                        Solution = null
                    }
                ]
            }
        };

#pragma warning disable QD0001
        mockClient.ReportIssues(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reportIssuesResponse));
#pragma warning restore QD0001

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];
        node.Issues.Should().HaveCount(1, "Only one valid issue should remain");
        node.Issues[0].Should().Be("[disk_usage] High");
        state.Health.Issues.Should().HaveCount(1);
        state.Health.Issues[0].Should().Be("pod1: [disk_usage] High");
    }

    [Test]
    public async Task GetClusterStateAsync_WithQdrantIssuesWithRelatedCollection_ShouldIncludeCollectionName()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId = 1001UL;
        var mockClient = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());

        mockClient.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Status = new QdrantStatus(QdrantOperationStatusType.Ok),
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId,
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId },
                    ConsensusThreadStatus = new ConsensusThreadState
                    {
                        ConsensusThreadStatus = ConsensusThreadStatus.Working,
                        LastUpdate = DateTime.UtcNow,
                        Err = null
                    },
                    Peers = []
                }
            }));

        // Mock ReportIssues response with related collection
        var reportIssuesResponse = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint
            {
                Issues =
                [
                    new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint.QdrantIssue
                    {
                        Id = "shard_replication",
                        Description = "Shard has no active replicas",
                        Timestamp = DateTime.UtcNow,
                        RelatedCollection = "my_collection",
                        Solution = null
                    }
                ]
            }
        };

#pragma warning disable QD0001
        mockClient.ReportIssues(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reportIssuesResponse));
#pragma warning restore QD0001

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        state.Nodes.Should().HaveCount(1);
        var node = state.Nodes[0];
        node.Issues.Should().HaveCount(1);
        node.Issues[0].Should().Be("[shard_replication] Shard has no active replicas (Collection: my_collection)");
    }

    [Test]
    public void CalculateHealth_ShouldSkipEmptyIssuesAndWarnings()
    {
        var state = new ClusterState
        {
            Nodes =
            [
                new()
                {
                    Url = "http://node1:6333",
                    PodName = "pod1",
                    PeerId = "1001",
                    IsHealthy = true,
                    IsLeader = true, // Mark as leader to avoid "No leader elected" issue
                    Issues = ["", "   ", null!, " real issue "],
                    Warnings = [null!, " ", " warn "]
                },
                new()
                {
                    Url = "http://node2:6333",
                    PodName = "pod2",
                    PeerId = "1002",
                    IsHealthy = true,
                    Issues = [],
                    Warnings = ["second warn"]
                }
            ]
        };

        var health = state.Health;

        health.Issues.Should().HaveCount(1);
        health.Issues[0].Should().Be("pod1: real issue");
        health.Warnings.Should().HaveCount(2);
        health.Warnings.Should().Contain("pod1: warn");
        health.Warnings.Should().Contain("pod2: second warn");
    }

    #endregion

    #region Node Sorting Tests

    [Test]
    public async Task GetClusterStateAsync_SortsNodesByPodName()
    {
        // Arrange - nodes in random order
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "qdrant-2" },
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "qdrant-0" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = "qdrant-1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId1 = 1001UL;
        var peerId2 = 1002UL;
        var peerId3 = 1003UL;

        // Setup responses for each node
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId1,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId2,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId3,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert - nodes should be sorted by PodName
        result.Nodes.Should().HaveCount(3);
        result.Nodes[0].PodName.Should().Be("qdrant-0");
        result.Nodes[1].PodName.Should().Be("qdrant-1");
        result.Nodes[2].PodName.Should().Be("qdrant-2");
    }

    [Test]
    public async Task GetClusterStateAsync_SortsNodesByPeerId_WhenPodNameNotAvailable()
    {
        // Arrange - nodes without pod names
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = null },
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = null },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = null }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId1 = 3001UL; // Intentionally out of order
        var peerId2 = 1002UL;
        var peerId3 = 2003UL;

        // Setup responses for each node
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId1,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId2,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId3,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert - nodes should be sorted by PeerId when PodName is not available
        result.Nodes.Should().HaveCount(3);
        result.Nodes[0].PeerId.Should().Be(peerId2.ToString());
        result.Nodes[1].PeerId.Should().Be(peerId3.ToString());
        result.Nodes[2].PeerId.Should().Be(peerId1.ToString());
    }

    [Test]
    public async Task GetClusterStateAsync_SortsNodesByPodName_ThenByPeerId_WhenMixed()
    {
        // Arrange - mix of nodes with and without pod names
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "qdrant-1" },
            new QdrantNodeConfig { Host = "node2", Port = 6333, Namespace = "ns1", PodName = null }, // No pod name
            new QdrantNodeConfig { Host = "node3", Port = 6333, Namespace = "ns1", PodName = "qdrant-0" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var peerId1 = 1001UL;
        var peerId2 = 2002UL;
        var peerId3 = 3003UL;

        // Setup responses for each node
        var mockClient1 = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient1.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId1,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient2 = _mockClients.GetOrAdd("node2:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient2.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId2,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId3.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        var mockClient3 = _mockClients.GetOrAdd("node3:6333", _ => Substitute.For<IQdrantHttpClient>());
        mockClient3.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetClusterInfoResponse
            {
                Result = new GetClusterInfoResponse.ClusterInfo
                {
                    PeerId = peerId3,
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>
                    {
                        { peerId1.ToString(), new GetClusterInfoResponse.PeerInfoUint() },
                        { peerId2.ToString(), new GetClusterInfoResponse.PeerInfoUint() }
                    },
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = peerId1, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert - nodes with PodName should come first (sorted by name), then by PeerId
        result.Nodes.Should().HaveCount(3);
        // First should be peerId2 (no pod name, sorted by peerId which is "2002")
        result.Nodes[0].PeerId.Should().Be(peerId2.ToString());
        result.Nodes[0].PodName.Should().BeNull();
        // Then qdrant-0
        result.Nodes[1].PodName.Should().Be("qdrant-0");
        // Then qdrant-1
        result.Nodes[2].PodName.Should().Be("qdrant-1");
    }

    #endregion

    #region ReportIssue / ClearIssue Tests

    [Test]
    public async Task ReportIssue_WhenCalled_IssueAppearsInHealthIssues()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>([]));

        _clusterManager.ReportIssue("snapshot:my-collection", "snapshot failed");

        // Act
        var state = await _clusterManager.GetClusterStateAsync();

        // Assert
        state.Health.Issues.Should().Contain(i => i.Contains("[snapshot:my-collection] snapshot failed"));
    }

    [Test]
    public async Task ReportIssue_MultipleTimes_SameKey_OverwritesPreviousMessage()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>([]));

        _clusterManager.ReportIssue("snapshot:col", "first error");
        _clusterManager.ReportIssue("snapshot:col", "second error");

        // Act
        var state = await _clusterManager.GetClusterStateAsync();

        // Assert
        var snapshotIssues = state.Health.Issues.Where(i => i.Contains("[snapshot:col]")).ToList();
        snapshotIssues.Should().HaveCount(1);
        snapshotIssues[0].Should().Contain("second error");
    }

    [Test]
    public async Task ClearIssue_AfterReport_IssueNotPresentInHealthIssues()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>([]));

        _clusterManager.ReportIssue("snapshot:my-collection", "snapshot failed");
        _clusterManager.ClearIssue("snapshot:my-collection");

        // Act
        var state = await _clusterManager.GetClusterStateAsync();

        // Assert
        state.Health.Issues.Should().NotContain(i => i.Contains("[snapshot:my-collection]"));
    }

    [Test]
    public async Task ClearIssue_NonExistentKey_DoesNotThrow()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>([]));

        // Act & Assert — no exception
        Action act = () => _clusterManager.ClearIssue("snapshot:does-not-exist");
        act.Should().NotThrow();
    }

    [Test]
    public async Task ReportIssue_MultipleKeys_AllAppearsInHealthIssues()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>([]));

        _clusterManager.ReportIssue("snapshot:col1", "error on col1");
        _clusterManager.ReportIssue("snapshot:col2", "error on col2");

        // Act
        var state = await _clusterManager.GetClusterStateAsync();

        // Assert
        state.Health.Issues.Should().Contain(i => i.Contains("[snapshot:col1] error on col1"));
        state.Health.Issues.Should().Contain(i => i.Contains("[snapshot:col2] error on col2"));
    }

    [Test]
    public async Task GetClusterStateAsync_WhenStorageUsageReturned_PopulatesNodeStorage()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var node1Client = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
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

        _kubernetesManager.GetQdrantStorageUsageAsync(
                "pod1",
                "qdrant",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new QdrantStorageUsageInfo
            {
                PodName = "pod1",
                Namespace = "qdrant",
                StoragePath = "/qdrant/storage",
                UsedBytes = 90,
                PvcCapacityBytes = 180,
                UsagePercent = 50
            });

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        var node = state.Nodes.Single();
        node.Storage.UsedBytes.Should().Be(90);
        node.Storage.CapacityBytes.Should().Be(180);
        node.Storage.UsagePercent.Should().Be(50);
    }

    [Test]
    public async Task GetClusterStateAsync_WhenStorageUsageIsNull_LeavesNodeStorageEmpty()
    {
        // Arrange
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        var pod1Id = 1001UL;
        var node1Client = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
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

        _kubernetesManager.GetQdrantStorageUsageAsync(
                "pod1",
                "qdrant",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns((QdrantStorageUsageInfo?)null);

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        var node = state.Nodes.Single();
        node.Storage.UsedBytes.Should().BeNull();
        node.Storage.CapacityBytes.Should().BeNull();
        node.Storage.UsagePercent.Should().BeNull();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenStorageUsageExceedsThreshold_AddsDiskIssue()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new DynamicConfig
            {
                MonitoringIntervalSeconds = 120,
                DiskUsageAlertThresholdPercent = 80m
            });

        var pod1Id = 1001UL;
        var node1Client = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
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

        _kubernetesManager.GetQdrantStorageUsageAsync(
                "pod1",
                "qdrant",
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new QdrantStorageUsageInfo
            {
                PodName = "pod1",
                Namespace = "qdrant",
                StoragePath = "/qdrant/storage",
                UsedBytes = 170,
                PvcCapacityBytes = 180,
                UsagePercent = 94.44m
            });

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Single().Issues.Any(i => i.Contains("Disk usage is 94.44%")).Should().BeTrue();
    }

    [Test]
    public async Task GetClusterStateAsync_WhenMemoryUsageExceedsThreshold_AddsMemoryIssue()
    {
        var nodes = new[]
        {
            new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "qdrant", PodName = "pod1" }
        };

        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(nodes));

        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new DynamicConfig
            {
                MonitoringIntervalSeconds = 120,
                RamUsageAlertThresholdPercent = 80m
            });

        var pod1Id = 1001UL;
        var node1Client = _mockClients.GetOrAdd("node1:6333", _ => Substitute.For<IQdrantHttpClient>());
        node1Client.GetClusterInfo(Arg.Any<CancellationToken>())
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

        _kubernetesManager.GetQdrantMemoryUsageAsync(
                "pod1",
                "qdrant",
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new QdrantMemoryUsageInfo
            {
                PodName = "pod1",
                Namespace = "qdrant",
                UsedBytes = 94,
                RequestBytes = 100,
                LimitBytes = 120,
                UsagePercent = 94m
            });

        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        state.Nodes.Single().Issues.Any(i => i.Contains("RAM usage is 94.00%")).Should().BeTrue();
    }

    #endregion
}

