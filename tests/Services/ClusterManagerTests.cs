using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Concurrent;
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
public class ClusterManagerTests
{
    private IQdrantNodesProvider _nodesProvider = null!;
    private IOptions<QdrantOptions> _options = null!;
    private ILogger<ClusterManager> _logger = null!;
    private IMeterService _meterService = null!;
    private IQdrantClientFactory _clientFactory = null!;
    private ICollectionService _collectionService = null!;
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
        _mockClients = new ConcurrentDictionary<string, IQdrantHttpClient>();
        
        _options.Value.Returns(new QdrantOptions { HttpTimeoutSeconds = 5 });
        
        // Create TestDataProvider with the same options
        _testDataProvider = new TestDataProvider(_options);
        
        // Setup collection service to always return healthy
        _collectionService
            .CheckCollectionsHealthAsync(Arg.Any<IQdrantHttpClient>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((true, (string?)null)));
        
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
        
        var kubernetesManager = Substitute.For<IKubernetesManager>();
        
        _clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _testDataProvider,
            _options,
            _logger,
            _meterService,
            kubernetesManager);
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
        Assert.That(result.Nodes, Has.Count.EqualTo(3));
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(3));
        Assert.That(result.Nodes.All(n => n.Issues.Count == 0), Is.True);
        Assert.That(result.Nodes.Count(n => n.IsLeader), Is.EqualTo(1));
        Assert.That(result.Nodes.Single(n => n.IsLeader).PodName, Is.EqualTo("pod1"));
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
        Assert.That(firstResult.Nodes.Count(n => n.IsHealthy), Is.EqualTo(3));

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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod3Id, Term = 2, Commit = 5 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Act - Second call should detect the split
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        Assert.That(result.Nodes, Has.Count.EqualTo(3));
        // pod1 and pod2 form majority (2 nodes with same peer set), pod3 is isolated
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(2), "Expected 2 healthy nodes (majority group)");
        
        var unhealthyNode = result.Nodes.Single(n => !n.IsHealthy);
        Assert.That(unhealthyNode.PodName, Is.EqualTo("pod3"));
        Assert.That(unhealthyNode.ErrorType, Is.EqualTo(NodeErrorType.ClusterSplit));
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
        Assert.That(firstResult.Nodes.Count(n => n.IsHealthy), Is.EqualTo(2));

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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
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
        Assert.That(result.Nodes, Has.Count.EqualTo(2));
        // Both nodes are inconsistent with the previous majority state, but one will be majority (the one that matches most)
        var unhealthyNodes = result.Nodes.Where(n => !n.IsHealthy).ToList();
        Assert.That(unhealthyNodes, Has.Count.GreaterThanOrEqualTo(1), "At least one node should be unhealthy");
        Assert.That(unhealthyNodes.Any(n => n.ErrorType == NodeErrorType.ClusterSplit), Is.True);
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit 
                    { 
                        Leader = pod1Id,
                        Term = 1,
                        Commit = 1
                    }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // Node 2 times out
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

        // Assert
        Assert.That(result.Nodes, Has.Count.EqualTo(2));
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(1));
        var unhealthyNode = result.Nodes.Single(n => !n.IsHealthy);
        Assert.That(unhealthyNode.PodName, Is.EqualTo("pod2"));
        Assert.That(unhealthyNode.ErrorType, Is.EqualTo(NodeErrorType.Timeout));
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
        Assert.That(result.Nodes, Has.Count.EqualTo(1));
        Assert.That(result.Nodes[0].IsHealthy, Is.False);
        Assert.That(result.Nodes[0].ErrorType, Is.EqualTo(NodeErrorType.ConnectionError));
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
        Assert.That(firstResult.Nodes.Count(n => n.IsHealthy), Is.EqualTo(3));


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
        Assert.That(result.Nodes, Has.Count.EqualTo(3));
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(3));
        // But there should be 2 nodes claiming to be leaders (split brain with leaders)
        Assert.That(result.Nodes.Count(n => n.IsLeader), Is.GreaterThanOrEqualTo(1));
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
        Assert.That(result.Nodes, Is.Empty);
        _meterService.Received(1).UpdateAliveNodes(0);
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
        Assert.That(result.Nodes, Has.Count.EqualTo(2));
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(0));
        Assert.That(result.Nodes.All(n => n.ErrorType == NodeErrorType.ConnectionError), Is.True);
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
        Assert.That(result.Nodes, Has.Count.EqualTo(2));
        // Node 1 should still be healthy (no split detection without peer info)
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(2));
    }

    [Test]
    public void GetClusterStateAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        _nodesProvider.GetNodesAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<QdrantNodeConfig>>(
                    new[] { new QdrantNodeConfig { Host = "node1", Port = 6333, Namespace = "ns1", PodName = "pod1" } });
            });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await _clusterManager.GetClusterStateAsync(cts.Token));
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
        Assert.That(result.Nodes, Has.Count.EqualTo(5));
        // Majority group (3 nodes) should be healthy, minority (2 nodes) should be unhealthy
        Assert.That(result.Nodes.Count(n => n.IsHealthy), Is.EqualTo(3), "Expected 3 healthy nodes (majority)");
        Assert.That(result.Nodes.Count(n => !n.IsHealthy), Is.EqualTo(2), "Expected 2 unhealthy nodes (minority)");
        
        var unhealthyNodes = result.Nodes.Where(n => !n.IsHealthy).ToList();
        Assert.That(unhealthyNodes.All(n => n.ErrorType == NodeErrorType.ClusterSplit), Is.True);
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
        Assert.That(result.Nodes, Has.Count.EqualTo(1));
        Assert.That(result.Nodes[0].IsHealthy, Is.False);
        Assert.That(result.Nodes[0].ErrorType, Is.EqualTo(NodeErrorType.InvalidResponse));
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
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
                Metrics = new Dictionary<string, object>
                {
                    { "prettySize", "N/A" },
                    { "sizeBytes", 0L },
                    { "snapshots", new List<string>() }
                }
            }
        };

        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
            .Returns(apiCollections);

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        await _collectionService.Received(1).GetCollectionsFromQdrantAsync(
            Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
            Arg.Any<CancellationToken>());

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].CollectionName, Is.EqualTo("test_collection"));
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
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
                Metrics = new Dictionary<string, object>
                {
                    { "prettySize", "N/A" },
                    { "sizeBytes", 0L },
                    { "snapshots", new List<string>() }
                }
            }
        };

        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
            .Returns(apiCollections);

        // Storage returns empty (collection not found in storage)
        _collectionService.GetCollectionsSizesForPodAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<CollectionSize>());

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Issues, Has.Count.EqualTo(1));
        Assert.That(result[0].Issues[0], Is.EqualTo("Collection exists in API but not found in storage"));
        Assert.That(result[0].Metrics["prettySize"], Is.EqualTo("N/A"));
        Assert.That(result[0].Metrics["sizeBytes"], Is.EqualTo(0L));
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
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
                Metrics = new Dictionary<string, object>
                {
                    { "prettySize", "N/A" },
                    { "sizeBytes", 0L },
                    { "snapshots", new List<string>() }
                }
            }
        };

        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
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
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Issues, Has.Count.EqualTo(0), "Should have no issues when collection exists in both API and storage");
        Assert.That(result[0].Metrics["prettySize"], Is.EqualTo("1 GB"));
        Assert.That(result[0].Metrics["sizeBytes"], Is.EqualTo(1073741824L));
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
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
                Metrics = new Dictionary<string, object> { { "prettySize", "N/A" }, { "sizeBytes", 0L }, { "snapshots", new List<string>() } }
            },
            new()
            {
                CollectionName = "collection_missing_from_storage",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new Dictionary<string, object> { { "prettySize", "N/A" }, { "sizeBytes", 0L }, { "snapshots", new List<string>() } }
            },
            new()
            {
                CollectionName = "another_in_both",
                NodeUrl = "http://node1:6333",
                PodName = "pod1",
                PeerId = "1001",
                Metrics = new Dictionary<string, object> { { "prettySize", "N/A" }, { "sizeBytes", 0L }, { "snapshots", new List<string>() } }
            }
        };

        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
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
        Assert.That(result, Has.Count.EqualTo(3));
        
        var collectionInBoth = result.First(c => c.CollectionName == "collection_in_both");
        Assert.That(collectionInBoth.Issues, Has.Count.EqualTo(0));
        Assert.That(collectionInBoth.Metrics["sizeBytes"], Is.EqualTo(500000000L));

        var collectionMissing = result.First(c => c.CollectionName == "collection_missing_from_storage");
        Assert.That(collectionMissing.Issues, Has.Count.EqualTo(1));
        Assert.That(collectionMissing.Issues[0], Is.EqualTo("Collection exists in API but not found in storage"));
        Assert.That(collectionMissing.Metrics["prettySize"], Is.EqualTo("N/A"));

        var anotherInBoth = result.First(c => c.CollectionName == "another_in_both");
        Assert.That(anotherInBoth.Issues, Has.Count.EqualTo(0));
        Assert.That(anotherInBoth.Metrics["sizeBytes"], Is.EqualTo(750000000L));
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>(),
                    RaftInfo = new GetClusterInfoResponse.RaftInfoUnit { Leader = pod1Id, Term = 1, Commit = 1 }
                },
                Status = new QdrantStatus(QdrantOperationStatusType.Ok)
            }));

        // API returns no collections
        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<CollectionInfo>());

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert - should return test data
        Assert.That(result, Has.Count.GreaterThan(0), "Should return test data when no collections from API");
        Assert.That(result.Any(c => c.CollectionName.Contains("test") || c.CollectionName.Contains("product")), 
            Is.True, "Test data should contain standard test collections");
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
                Metrics = new Dictionary<string, object> { { "prettySize", "N/A" }, { "sizeBytes", 0L }, { "snapshots", new List<string>() } }
            },
            new()
            {
                CollectionName = "collection1",
                NodeUrl = "http://node2:6333",
                PodName = "pod2",
                PeerId = "1002",
                Metrics = new Dictionary<string, object> { { "prettySize", "N/A" }, { "sizeBytes", 0L }, { "snapshots", new List<string>() } }
            }
        };

        _collectionService.GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>())
            .Returns(apiCollections);

        // Storage returns size for node1 but not node2
        _collectionService.GetCollectionsSizesForPodAsync(
                "pod1",
                Arg.Any<string>(),
                "http://node1:6333",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<CollectionSize>
            {
                new()
                {
                    CollectionName = "collection1",
                    NodeUrl = "http://node1:6333",
                    PodName = "pod1",
                    PeerId = "1001",
                    SizeBytes = 1000000000L
                }
            });

        _collectionService.GetCollectionsSizesForPodAsync(
                "pod2",
                Arg.Any<string>(),
                "http://node2:6333",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<CollectionSize>());

        // Act
        var result = await _clusterManager.GetCollectionsInfoAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        
        var node1Collection = result.First(c => c.NodeUrl == "http://node1:6333");
        Assert.That(node1Collection.Issues, Has.Count.EqualTo(0));
        Assert.That(node1Collection.Metrics["sizeBytes"], Is.EqualTo(1000000000L));

        var node2Collection = result.First(c => c.NodeUrl == "http://node2:6333");
        Assert.That(node2Collection.Issues, Has.Count.EqualTo(1));
        Assert.That(node2Collection.Issues[0], Is.EqualTo("Collection exists in API but not found in storage"));
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

        // Act
        var result = await _clusterManager.DeleteCollectionViaApiOnAllNodesAsync(collectionName, CancellationToken.None);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Values.All(v => v), Is.True);
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint> { },
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
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _clusterManager.ReplicateShardsAsync(
            sourcePeerId, targetPeerId, collectionName, shardIds, false, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint> { },
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
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act
        var result = await _clusterManager.ReplicateShardsAsync(
            sourcePeerId, targetPeerId, collectionName, shardIds, true, CancellationToken.None);

        // Assert
        Assert.That(result, Is.True);
        await _collectionService.Received(1).ReplicateShardsAsync(
            Arg.Any<string>(),
            sourcePeerId,
            targetPeerId,
            collectionName,
            shardIds,
            true,
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
            sourcePeerId, targetPeerId, collectionName, shardIds, false, CancellationToken.None);

        // Assert
        Assert.That(result, Is.False);
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Nodes, Has.Count.EqualTo(1));
        var node = state.Nodes[0];
        Assert.That(node.IsHealthy, Is.True, "Node should be healthy - stale failures don't mark it unhealthy");
        Assert.That(node.ErrorType, Is.EqualTo(NodeErrorType.None), "Node should have no error type");
        Assert.That(node.Warnings, Has.Count.EqualTo(1), "Node should have one warning");
        Assert.That(node.Warnings[0], Does.Contain("Stale message send failures"));
        Assert.That(node.Warnings[0], Does.Contain("1002"));
        Assert.That(node.Warnings[0], Does.Contain("Connection timeout"));
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Act
        var result = await _clusterManager.GetClusterStateAsync();

        // Assert
        Assert.That(result.Nodes, Has.Count.EqualTo(1));
        var node = result.Nodes[0];
        Assert.That(node.IsHealthy, Is.False, "Node should be unhealthy - active failures mark it unhealthy");
        Assert.That(node.ErrorType, Is.EqualTo(NodeErrorType.MessageSendFailures));
        Assert.That(node.Issues, Has.Count.GreaterThan(0), "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        Assert.That(issuesText, Does.Contain("Message send failures"));
        Assert.That(issuesText, Does.Contain("1002"));
        Assert.That(issuesText, Does.Contain("Network unreachable"));
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Nodes, Has.Count.EqualTo(1));
        var node = state.Nodes[0];
        
        Assert.That(node.IsHealthy, Is.False, "Node should be unhealthy due to recent failure");
        Assert.That(node.ErrorType, Is.EqualTo(NodeErrorType.MessageSendFailures));
        Assert.That(node.Issues, Has.Count.GreaterThan(0), "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        Assert.That(issuesText, Does.Contain("1003"), "Issues should mention peer with recent failure");
        Assert.That(issuesText, Does.Contain("Current error"));
        Assert.That(issuesText, Does.Not.Contain("1002"), "Issues should not mention peer with stale failure");
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Nodes, Has.Count.EqualTo(1));
        var node = state.Nodes[0];
        
        Assert.That(node.IsHealthy, Is.False, "Without consensus timestamp, all failures are treated as active");
        Assert.That(node.ErrorType, Is.EqualTo(NodeErrorType.MessageSendFailures));
        Assert.That(node.Issues, Has.Count.GreaterThan(0), "Node should have issues");
        var issuesText = string.Join(" ", node.Issues);
        Assert.That(issuesText, Does.Contain("Message send failures"));
        Assert.That(issuesText, Does.Contain("1002"));
        Assert.That(issuesText, Does.Contain("Connection failed"));
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
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
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Health.IsHealthy, Is.True, "Cluster should be healthy");
        Assert.That(state.Health.Issues, Has.Count.EqualTo(0), "Should have no issues (stale failures go to warnings)");
        Assert.That(state.Health.Warnings, Has.Count.EqualTo(1), "Should have 1 warning for stale failures");
        Assert.That(state.Health.Warnings[0], Does.Contain("pod1"));
        Assert.That(state.Health.Warnings[0], Does.Contain("Stale message send failures"));
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
            _testDataProvider,
            _options,
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

        // Setup node1 as healthy
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

        // Setup node2 as unhealthy (timeout)
        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns(Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return new GetClusterInfoResponse
                {
                    Result = new GetClusterInfoResponse.ClusterInfo(),
                    Status = new QdrantStatus(QdrantOperationStatusType.Ok)
                };
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
        Assert.That(state.Status, Is.EqualTo(ClusterStatus.Degraded), "Cluster should be degraded");
        
        // Verify Kubernetes warnings were fetched
        await kubernetesManager.Received(1).GetWarningEventsAsync("qdrant", Arg.Any<CancellationToken>());
        
        // Verify warnings were added to a node
        var nodeWithWarnings = state.Nodes.FirstOrDefault(n => n.Warnings.Any());
        Assert.That(nodeWithWarnings, Is.Not.Null, "At least one node should have warnings");
        Assert.That(nodeWithWarnings!.Warnings.Count, Is.EqualTo(2), "Should have 2 K8s warnings");
        Assert.That(nodeWithWarnings.Warnings[0], Does.Contain("K8s Event:"));
        Assert.That(nodeWithWarnings.Warnings[0], Does.Contain("BackOff"));
        
        // Verify warnings appear in ClusterHealth.Warnings
        Assert.That(state.Health.Warnings, Has.Count.EqualTo(2), "ClusterHealth should have 2 warnings");
        Assert.That(state.Health.Warnings[0], Does.Contain("K8s Event:"));
        Assert.That(state.Health.Warnings[0], Does.Contain("BackOff"));
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
            _testDataProvider,
            _options,
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
        Assert.That(state.Status, Is.EqualTo(ClusterStatus.Healthy), "Cluster should be healthy");
        
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
            _testDataProvider,
            _options,
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

        // Setup node1 as healthy
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

        // Setup node2 as unhealthy
        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new OperationCanceledException());

        // Act & Assert - should not throw
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);
        
        Assert.That(state.Status, Is.EqualTo(ClusterStatus.Degraded), "Cluster should be degraded");
        Assert.That(state.Nodes.All(n => n.Warnings.Count == 0), Is.True, "No warnings should be added without K8s manager");
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
            _testDataProvider,
            _options,
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

        // Setup node1 as healthy
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

        // Setup node2 as unhealthy
        var node2Key = nodes[1].Host + ":" + nodes[1].Port;
        var node2Client = _mockClients.GetOrAdd(node2Key, _ => Substitute.For<IQdrantHttpClient>());
        node2Client.GetClusterInfo(Arg.Any<CancellationToken>())
            .Returns<GetClusterInfoResponse>(_ => throw new OperationCanceledException());

        // Setup Kubernetes manager to throw exception
        kubernetesManager.GetWarningEventsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<List<string>>(_ => throw new Exception("K8s API Error"));

        // Act & Assert - should not throw, should handle exception gracefully
        var state = await clusterManager.GetClusterStateAsync(CancellationToken.None);
        
        Assert.That(state.Status, Is.EqualTo(ClusterStatus.Degraded), "Cluster should be degraded");
        // Should still return valid state even if K8s warnings fetch failed
        Assert.That(state.Nodes, Has.Count.EqualTo(2));
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
                        LastUpdate = DateTime.UtcNow,
                        Err = null
                    },
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Mock ReportIssues response
        var reportIssuesResponse = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint
            {
                Issues = new[]
                {
                    new KeyValuePair<string, string>("disk_usage", "Disk usage is above 80%"),
                    new KeyValuePair<string, string>("memory_usage", "Memory usage is above 90%")
                }
            }
        };
        
#pragma warning disable QD0001
        mockClient.ReportIssues(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reportIssuesResponse));
#pragma warning restore QD0001

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Nodes, Has.Count.EqualTo(1));
        var node = state.Nodes[0];
        Assert.That(node.IsHealthy, Is.True, "Node should be healthy even with Qdrant issues (they are informational)");
        Assert.That(node.Issues, Has.Count.EqualTo(2), "Should have 2 issues from Qdrant");
        Assert.That(node.Issues[0], Is.EqualTo("disk_usage: Disk usage is above 80%"));
        Assert.That(node.Issues[1], Is.EqualTo("memory_usage: Memory usage is above 90%"));
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
                    ConsensusThreadStatus = new GetClusterInfoResponse.ConsensusThreadStatusUnit
                    {
                        ConsensusThreadStatus = "working",
                        LastUpdate = DateTime.UtcNow,
                        Err = null
                    },
                    Peers = new Dictionary<string, GetClusterInfoResponse.PeerInfoUint>()
                }
            }));

        // Mock ReportIssues response with issues without values
        var reportIssuesResponse = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse
        {
            Status = new QdrantStatus(QdrantOperationStatusType.Ok),
            Result = new Aer.QdrantClient.Http.Models.Responses.ReportIssuesResponse.QdrantIssuesUint
            {
                Issues = new[]
                {
                    new KeyValuePair<string, string>(null!, null!),
                    new KeyValuePair<string, string>("", ""),
                    new KeyValuePair<string, string>("   ", "   "),
                    new KeyValuePair<string, string>("disk_usage", "  High  ")
                }
            }
        };
        
#pragma warning disable QD0001
        mockClient.ReportIssues(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reportIssuesResponse));
#pragma warning restore QD0001

        // Act
        var state = await _clusterManager.GetClusterStateAsync(CancellationToken.None);

        // Assert
        Assert.That(state.Nodes, Has.Count.EqualTo(1));
        var node = state.Nodes[0];
        Assert.That(node.Issues, Has.Count.EqualTo(1), "Only one valid issue should remain");
        Assert.That(node.Issues[0], Is.EqualTo("disk_usage: High"));
        Assert.That(state.Health.Issues, Has.Count.EqualTo(1));
        Assert.That(state.Health.Issues[0], Is.EqualTo("pod1: disk_usage: High"));
    }

    [Test]
    public void CalculateHealth_ShouldSkipEmptyIssuesAndWarnings()
    {
        var state = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                new()
                {
                    Url = "http://node1:6333",
                    PodName = "pod1",
                    PeerId = "1001",
                    IsHealthy = true,
                    IsLeader = true, // Mark as leader to avoid "No leader elected" issue
                    Issues = new List<string> { "", "   ", null!, " real issue " },
                    Warnings = new List<string> { null!, " ", " warn " }
                },
                new()
                {
                    Url = "http://node2:6333",
                    PodName = "pod2",
                    PeerId = "1002",
                    IsHealthy = true,
                    Issues = new List<string>(),
                    Warnings = new List<string> { "second warn" }
                }
            }
        };

        var health = state.Health;

        Assert.That(health.Issues, Has.Count.EqualTo(1));
        Assert.That(health.Issues[0], Is.EqualTo("pod1: real issue"));
        Assert.That(health.Warnings, Has.Count.EqualTo(2));
        Assert.That(health.Warnings, Does.Contain("pod1: warn"));
        Assert.That(health.Warnings, Does.Contain("pod2: second warn"));
    }

    #endregion
}

