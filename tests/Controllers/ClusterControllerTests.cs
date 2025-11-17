using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class ClusterControllerTests
{
    private IClusterManager _clusterManager = null!;
    private ILogger<ClusterController> _logger = null!;
    private ClusterController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _logger = Substitute.For<ILogger<ClusterController>>();
        
        _controller = new ClusterController(_clusterManager, _logger);
    }

    #region GetClusterStatus Tests

    [Test]
    public async Task GetClusterStatus_WhenSuccessful_ReturnsOkWithClusterState()
    {
        // Arrange
        var clusterState = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                new()
                {
                    Url = "http://node1:6333",
                    PeerId = "peer1",
                    IsHealthy = true,
                    IsLeader = true,
                    PodName = "pod1",
                    Namespace = "default"
                }
            }
        };

        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(clusterState);

        // Act
        var result = await _controller.GetClusterStatusAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as ClusterState;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Nodes, Has.Count.EqualTo(1));
        Assert.That(response.Health.IsHealthy, Is.True);
    }

    [Test]
    public async Task GetClusterStatus_WhenClusterUnhealthy_ReturnsStateWithIssues()
    {
        // Arrange
        var clusterState = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                new()
                {
                    Url = "http://node1:6333",
                    PeerId = "peer1",
                    IsHealthy = false,
                    IsLeader = false,
                    Error = "Connection timeout",
                    PodName = "pod1",
                    Namespace = "default"
                },
                new()
                {
                    Url = "http://node2:6333",
                    PeerId = "peer2",
                    IsHealthy = false,
                    IsLeader = false,
                    Error = "Network error",
                    PodName = "pod2",
                    Namespace = "default"
                }
            }
        };

        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(clusterState);

        // Act
        var result = await _controller.GetClusterStatusAsync(CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as ClusterState;
        Assert.That(response!.Health.IsHealthy, Is.False);
        Assert.That(response.Health.Issues, Is.Not.Empty);
        Assert.That(response.Nodes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetClusterStatus_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ClusterState>(new Exception("Test error")));

        // Act
        var result = await _controller.GetClusterStatusAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion

    #region ReplicateShards Tests

    [Test]
    public async Task ReplicateShards_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 0, 1 },
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.Not.Null);
        
        // Verify the method was called with correct parameters
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 0 },
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task ReplicateShards_WithMoveShards_CallsWithCorrectFlag()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 0 },
            IsMoveShards = true
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            true,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            true,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WithMultipleShards_HandlesCorrectly()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 0, 1, 2, 3 },
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Is<uint[]>(arr => arr.Length == 4),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task ReplicateShards_WhenExceptionThrown_Returns500()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = new uint[] { 0 },
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion
}

