using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class KubernetesControllerTests
{
    private IKubernetesManager _kubernetesManager = null!;
    private IQdrantNodesProvider _nodesProvider = null!;
    private ILogger<KubernetesController> _logger = null!;
    private KubernetesController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _kubernetesManager = Substitute.For<IKubernetesManager>();
        _nodesProvider = Substitute.For<IQdrantNodesProvider>();
        _logger = Substitute.For<ILogger<KubernetesController>>();
        
        _controller = new KubernetesController(_kubernetesManager, _nodesProvider, _logger);
    }

    #region DeletePodAsync Tests

    [Test]
    public async Task DeletePod_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "qdrant-0",
            Namespace = "qdrant"
        };

        _kubernetesManager.DeletePodAsync(
            request.PodName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DeletePodAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.StatusCode.Should().Be(200);
    }

    [Test]
    public async Task DeletePod_WithEmptyPodName_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "",
            Namespace = "qdrant"
        };

        // Act
        var result = await _controller.DeletePodAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<BadRequestObjectResult>();
    }

    [Test]
    public async Task DeletePod_WhenDeleteFails_ReturnsInternalServerError()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "qdrant-0",
            Namespace = "qdrant"
        };

        _kubernetesManager.DeletePodAsync(
            request.PodName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.DeletePodAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    [Test]
    public async Task DeletePod_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var request = new V1DeletePodRequest
        {
            PodName = "qdrant-0",
            Namespace = "qdrant"
        };

        _kubernetesManager.DeletePodAsync(
            request.PodName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new Exception("Test exception"));

        // Act
        var result = await _controller.DeletePodAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region ManageStatefulSetAsync Tests - Rollout

    [Test]
    public async Task ManageStatefulSet_WithRolloutOperation_ReturnsOk()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        _kubernetesManager.RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _kubernetesManager.Received(1).RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ManageStatefulSet_WithRolloutOperation_WhenFails_ReturnsInternalServerError()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        _kubernetesManager.RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region ManageStatefulSetAsync Tests - Scale

    [Test]
    public async Task ManageStatefulSet_WithScaleOperation_ReturnsOk()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 3
        };

        _kubernetesManager.ScaleStatefulSetAsync(
            request.StatefulSetName,
            request.Replicas.Value,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        await _kubernetesManager.Received(1).ScaleStatefulSetAsync(
            request.StatefulSetName,
            request.Replicas.Value,
            request.Namespace,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ManageStatefulSet_WithScaleOperationToZero_ReturnsOk()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 0
        };

        _kubernetesManager.ScaleStatefulSetAsync(
            request.StatefulSetName,
            0,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
    }

    [Test]
    public async Task ManageStatefulSet_WithScaleOperationWithoutReplicas_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = null
        };

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<BadRequestObjectResult>();
    }

    [Test]
    public async Task ManageStatefulSet_WithScaleOperationNegativeReplicas_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = -1
        };

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<BadRequestObjectResult>();
    }

    [Test]
    public async Task ManageStatefulSet_WithEmptyStatefulSetName_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<BadRequestObjectResult>();
    }

    [Test]
    public async Task ManageStatefulSet_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        _kubernetesManager.RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new Exception("Test exception"));

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region StatefulSet Name Storage Tests

    [Test]
    public async Task ManageStatefulSet_WithSuccessfulRollout_StoresStatefulSetName()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant1",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        _kubernetesManager.RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        _nodesProvider.Received(1).SetStatefulSetName("qdrant1");
    }

    [Test]
    public async Task ManageStatefulSet_WithSuccessfulScale_StoresStatefulSetName()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant2",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 5
        };

        _kubernetesManager.ScaleStatefulSetAsync(
            request.StatefulSetName,
            request.Replicas.Value,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        _nodesProvider.Received(1).SetStatefulSetName("qdrant2");
    }

    [Test]
    public async Task ManageStatefulSet_WithSuccessfulScaleToZero_StoresStatefulSetName()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant-test",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 0
        };

        _kubernetesManager.ScaleStatefulSetAsync(
            request.StatefulSetName,
            0,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<OkObjectResult>();
        _nodesProvider.Received(1).SetStatefulSetName("qdrant-test");
    }

    [Test]
    public async Task ManageStatefulSet_WithFailedOperation_DoesNotStoreStatefulSetName()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant-fail",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Rollout
        };

        _kubernetesManager.RolloutRestartStatefulSetAsync(
            request.StatefulSetName,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(500);
        _nodesProvider.DidNotReceive().SetStatefulSetName(Arg.Any<string>());
    }

    [Test]
    public async Task ManageStatefulSet_WithException_DoesNotStoreStatefulSetName()
    {
        // Arrange
        var request = new V1ManageStatefulSetRequest
        {
            StatefulSetName = "qdrant-error",
            Namespace = "qdrant",
            OperationType = StatefulSetOperationType.Scale,
            Replicas = 3
        };

        _kubernetesManager.ScaleStatefulSetAsync(
            request.StatefulSetName,
            request.Replicas.Value,
            request.Namespace,
            Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new Exception("Test exception"));

        // Act
        var result = await _controller.ManageStatefulSetAsync(request, CancellationToken.None);

        // Assert
        result.Should().BeAssignableTo<ObjectResult>();
        _nodesProvider.DidNotReceive().SetStatefulSetName(Arg.Any<string>());
    }

    #endregion
}

