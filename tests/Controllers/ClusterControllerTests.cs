using FluentAssertions;
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
            Nodes =
            [
                new()
                {
                    Url = "http://node1:6333",
                    PeerId = "peer1",
                    IsHealthy = true,
                    IsLeader = true,
                    PodName = "pod1",
                    Namespace = "default"
                }
            ]
        };

        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(clusterState);

        // Act
        var result = await _controller.GetClusterStatusAsync(CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as ClusterState;
        response.Should().NotBeNull();
        response!.Nodes.Should().HaveCount(1);
        response.Health.IsHealthy.Should().BeTrue();
    }

    [Test]
    public async Task GetClusterStatus_WhenClusterUnhealthy_ReturnsStateWithIssues()
    {
        // Arrange
        var clusterState = new ClusterState
        {
            Nodes =
            [
                new()
                {
                    Url = "http://node1:6333",
                    PeerId = "peer1",
                    IsHealthy = false,
                    IsLeader = false,
                    Issues = ["Connection timeout"],
                    PodName = "pod1",
                    Namespace = "default"
                },
                new()
                {
                    Url = "http://node2:6333",
                    PeerId = "peer2",
                    IsHealthy = false,
                    IsLeader = false,
                    Issues = ["Network error"],
                    PodName = "pod2",
                    Namespace = "default"
                }
            ]
        };

        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(clusterState);

        // Act
        var result = await _controller.GetClusterStatusAsync(CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as ClusterState;
        response!.Health.IsHealthy.Should().BeFalse();
        response.Health.Issues.Should().NotBeEmpty();
        response.Nodes.Should().HaveCount(2);
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
        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
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
            ShardIdsToReplicate = [0, 1],
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().NotBeNull();

        // Verify the method was called with correct parameters
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
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
            ShardIdsToReplicate = [0],
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
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
            ShardIdsToReplicate = [0],
            IsMoveShards = true
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            true,
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            true,
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
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
            ShardIdsToReplicate = [0, 1, 2, 3],
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Is<uint[]>(arr => arr.Length == 4),
            Arg.Any<bool>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
    }

    [Test]
    public async Task ReplicateShards_WithSnapshotTransferMethod_PassesCorrectValue()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = [0],
            IsMoveShards = false,
            ShardTransferMethod = "Snapshot"
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Is<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(m => m == Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.Snapshot),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.Snapshot,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WithStreamRecordsTransferMethod_PassesCorrectValue()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = [0],
            IsMoveShards = false,
            ShardTransferMethod = "StreamRecords"
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Is<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(m => m == Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.StreamRecords),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.StreamRecords,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WithWalDeltaTransferMethod_PassesCorrectValue()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = [0],
            IsMoveShards = false,
            ShardTransferMethod = "WalDelta"
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Is<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(m => m == Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.WalDelta),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.WalDelta,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WithReshardingStreamRecordsTransferMethod_PassesCorrectValue()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = [0],
            IsMoveShards = false,
            ShardTransferMethod = "ReshardingStreamRecords"
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Is<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(m => m == Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.ReshardingStreamRecords),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod.ReshardingStreamRecords,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplicateShards_WithNullTransferMethod_PassesNull()
    {
        // Arrange
        var request = new V1ReplicateShardsRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardIdsToReplicate = [0],
            IsMoveShards = false,
            ShardTransferMethod = null
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Is<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(m => m == null),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).ReplicateShardsAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardIdsToReplicate,
            request.IsMoveShards,
            null,
            Arg.Any<CancellationToken>());
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
            ShardIdsToReplicate = [0],
            IsMoveShards = false
        };

        _clusterManager.ReplicateShardsAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.ReplicateShards(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region AbortShardTransfer Tests

    [Test]
    public async Task AbortShardTransfer_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1AbortShardTransferRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardId = 0
        };

        _clusterManager.AbortShardTransferAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardId!.Value,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.AbortShardTransfer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().NotBeNull();

        await _clusterManager.Received(1).AbortShardTransferAsync(
            request.SourcePeerId!.Value,
            request.TargetPeerId!.Value,
            request.CollectionName,
            request.ShardId!.Value,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbortShardTransfer_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1AbortShardTransferRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardId = 0
        };

        _clusterManager.AbortShardTransferAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.AbortShardTransfer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task AbortShardTransfer_WhenExceptionThrown_Returns500()
    {
        // Arrange
        var request = new V1AbortShardTransferRequest
        {
            SourcePeerId = 1001,
            TargetPeerId = 1002,
            CollectionName = "test_collection",
            ShardId = 0
        };

        _clusterManager.AbortShardTransferAsync(
            Arg.Any<ulong>(),
            Arg.Any<ulong>(),
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.AbortShardTransfer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region DropShardsFromPeer Tests

    [Test]
    public async Task DropShardsFromPeer_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = [1, 2, 3],
            IsDryRun = false
        };

        _clusterManager.DropShardsFromPeerAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DropShardsFromPeer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().NotBeNull();

        // Verify the method was called with correct parameters
        await _clusterManager.Received(1).DropShardsFromPeerAsync(
            request.CollectionName,
            request.PeerId!.Value,
            request.ShardIds,
            request.IsDryRun,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DropShardsFromPeer_WithDryRun_ReturnsOk()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 987654321,
            ShardIds = [5, 6],
            IsDryRun = true
        };

        _clusterManager.DropShardsFromPeerAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DropShardsFromPeer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();

        // Verify dry run flag was passed correctly
        await _clusterManager.Received(1).DropShardsFromPeerAsync(
            request.CollectionName,
            request.PeerId!.Value,
            request.ShardIds,
            true,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DropShardsFromPeer_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = [1],
            IsDryRun = false
        };

        _clusterManager.DropShardsFromPeerAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.DropShardsFromPeer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task DropShardsFromPeer_WhenExceptionThrown_Returns500WithErrorDetails()
    {
        // Arrange
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "test_collection",
            PeerId = 123456789,
            ShardIds = [1],
            IsDryRun = false
        };

        _clusterManager.DropShardsFromPeerAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.DropShardsFromPeer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task DropShardsFromPeer_WithMultipleShards_CallsManagerCorrectly()
    {
        // Arrange
        var shardIds = new uint[] { 1, 2, 3, 4, 5 };
        var request = new V1DropShardsFromPeerRequest
        {
            CollectionName = "large_collection",
            PeerId = 111222333,
            ShardIds = shardIds,
            IsDryRun = false
        };

        _clusterManager.DropShardsFromPeerAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<uint[]>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DropShardsFromPeer(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();

        // Verify all shards were passed
        await _clusterManager.Received(1).DropShardsFromPeerAsync(
            request.CollectionName,
            request.PeerId!.Value,
            Arg.Is<uint[]>(arr => arr.SequenceEqual(shardIds)),
            false,
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region StartResharding Tests

    [Test]
    public async Task StartResharding_WithValidRequestUp_ReturnsOk()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "Up",
            PeerId = 123456789
        };

        _clusterManager.StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().NotBeNull();

        // Verify the method was called with correct parameters
        await _clusterManager.Received(1).StartReshardingAsync(
            request.CollectionName,
            Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection.Up,
            request.PeerId,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartResharding_WithValidRequestDown_ReturnsOk()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "Down",
            PeerId = null
        };

        _clusterManager.StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();

        // Verify the method was called with correct parameters
        await _clusterManager.Received(1).StartReshardingAsync(
            request.CollectionName,
            Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection.Down,
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartResharding_WithInvalidDirection_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "Invalid",
            PeerId = 123456789
        };

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<BadRequestObjectResult>();
        var badRequestResult = (BadRequestObjectResult)result;
        badRequestResult.StatusCode.Should().Be(400);

        // Verify the service was never called
        await _clusterManager.DidNotReceive().StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartResharding_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "Up",
            PeerId = 123456789
        };

        _clusterManager.StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task StartResharding_WhenExceptionThrown_Returns500WithErrorDetails()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "Up",
            PeerId = 123456789
        };

        _clusterManager.StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task StartResharding_WithCaseInsensitiveDirection_ReturnsOk()
    {
        // Arrange
        var request = new V1StartReshardingRequest
        {
            CollectionName = "test_collection",
            Direction = "up",
            PeerId = null
        };

        _clusterManager.StartReshardingAsync(
            Arg.Any<string>(),
            Arg.Any<Aer.QdrantClient.Http.Models.Shared.ReshardingOperationDirection>(),
            Arg.Any<ulong?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.StartResharding(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
    }

    #endregion

    #region RemovePeer Tests

    [Test]
    public async Task RemovePeer_WithValidRequest_ReturnsOk()
    {
        var request = new V1RemovePeerRequest
        {
            PeerId = 1001UL,
            IsForceDropOperation = false,
            TimeoutSeconds = null
        };

        _clusterManager.RemovePeerAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.RemovePeer(request, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).RemovePeerAsync(
            request.PeerId,
            false,
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemovePeer_WithForceDrop_CallsManagerWithForceTrue()
    {
        var request = new V1RemovePeerRequest
        {
            PeerId = 2002UL,
            IsForceDropOperation = true,
            TimeoutSeconds = null
        };

        _clusterManager.RemovePeerAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.RemovePeer(request, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).RemovePeerAsync(
            request.PeerId,
            true,
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemovePeer_WithTimeoutSeconds_CallsManagerWithTimeout()
    {
        var request = new V1RemovePeerRequest
        {
            PeerId = 3003UL,
            IsForceDropOperation = false,
            TimeoutSeconds = 60
        };

        _clusterManager.RemovePeerAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.RemovePeer(request, CancellationToken.None);

        result.Should().BeAssignableTo<OkObjectResult>();
        await _clusterManager.Received(1).RemovePeerAsync(
            request.PeerId,
            false,
            TimeSpan.FromSeconds(60),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemovePeer_WhenFailed_Returns500()
    {
        var request = new V1RemovePeerRequest { PeerId = 1001UL };

        _clusterManager.RemovePeerAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _controller.RemovePeer(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(500);
    }

    [Test]
    public async Task RemovePeer_WhenExceptionThrown_Returns500WithErrorDetails()
    {
        var request = new V1RemovePeerRequest { PeerId = 1001UL };

        _clusterManager.RemovePeerAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        var result = await _controller.RemovePeer(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(500);
    }

    #endregion
}
