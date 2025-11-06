using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Concurrent;
using Aer.QdrantClient.Http.Abstractions;
using Aer.QdrantClient.Http.Models.Responses;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Configuration;
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
            .CreateClient(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>())
            .Returns(info =>
            {
                var host = info.ArgAt<string>(0);
                var port = info.ArgAt<int>(1);
                var key = host + ":" + port;
                return _mockClients.GetOrAdd(key, _ => Substitute.For<IQdrantHttpClient>());
            });
        
        _clusterManager = new ClusterManager(
            _nodesProvider,
            _clientFactory,
            _collectionService,
            _testDataProvider,
            _options,
            _logger,
            _meterService);
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
        Assert.That(result.Nodes.All(n => string.IsNullOrEmpty(n.Error)), Is.True);
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

    [Test]
    public void RecoverClusterAsync_ExecutesWithoutError()
    {
        // Act & Assert - Currently just a placeholder, but should not throw
        Assert.DoesNotThrowAsync(async () => await _clusterManager.RecoverClusterAsync());
    }
}
