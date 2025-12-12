using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class SnapshotsControllerTests
{
    private ISnapshotService _snapshotService = null!;
    private IS3SnapshotService _s3SnapshotService = null!;
    private ICollectionService _collectionService = null!;
    private ILogger<SnapshotsController> _logger = null!;
    private SnapshotsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _snapshotService = Substitute.For<ISnapshotService>();
        _s3SnapshotService = Substitute.For<IS3SnapshotService>();
        _collectionService = Substitute.For<ICollectionService>();
        _logger = Substitute.For<ILogger<SnapshotsController>>();
        _controller = new SnapshotsController(_snapshotService, _s3SnapshotService, _collectionService, _logger);
    }

    #region GetSnapshotsInfo Tests

    [Test]
    public async Task GetSnapshotsInfo_WhenSuccessful_ReturnsOkWithSnapshots()
    {
        // Arrange
        var snapshots = new List<SnapshotInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "peer1",
                CollectionName = "collection1",
                SnapshotName = "snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>())
            .Returns(snapshots);

        // Act
        var result = await _controller.GetSnapshotsInfo(CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetSnapshotsInfoResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Snapshots, Has.Length.EqualTo(1));
        Assert.That(response.Snapshots[0].SnapshotName, Is.EqualTo("snapshot1.snapshot"));
    }

    [Test]
    public async Task GetSnapshotsInfo_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<SnapshotInfo>>(new Exception("Test error")));

        // Act
        var result = await _controller.GetSnapshotsInfo(CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion

    #region CreateSnapshot Tests

    [Test]
    public async Task CreateSnapshot_SingleNode_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            SingleNode = true,
            NodeUrl = "http://node1:6333"
        };

        _collectionService.CreateCollectionSnapshotAsync(request.NodeUrl!, request.CollectionName, Arg.Any<CancellationToken>())
            .Returns("snapshot-123.snapshot");

        // Act
        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1CreateSnapshotResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.SnapshotName, Is.EqualTo("snapshot-123.snapshot"));
    }

    [Test]
    public async Task CreateSnapshot_SingleNode_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            SingleNode = true,
            NodeUrl = "http://node1:6333"
        };

        _collectionService.CreateCollectionSnapshotAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Act
        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task CreateSnapshot_AllNodes_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            SingleNode = false
        };

        var results = new Dictionary<string, string?>
        {
            ["http://node1:6333"] = "snapshot-1.snapshot",
            ["http://node2:6333"] = "snapshot-2.snapshot"
        };

        _snapshotService.CreateCollectionSnapshotOnAllNodesAsync(request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1CreateSnapshotResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Message, Does.Contain("2/2"));
    }

    #endregion

    #region DeleteSnapshot Tests

    [Test]
    public async Task DeleteSnapshot_SingleNode_WithValidSource_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            SingleNode = true,
            Source = "QdrantApi",
            NodeUrl = "http://node1:6333"
        };

        _snapshotService.DeleteSnapshotAsync(
            request.CollectionName,
            request.SnapshotName,
            SnapshotSource.QdrantApi,
            request.NodeUrl,
            null,
            null,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DeleteSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task DeleteSnapshot_SingleNode_WithInvalidSource_ReturnsBadRequest()
    {
        // Arrange
        var request = new V1DeleteSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            SingleNode = true,
            Source = "InvalidSource",
            NodeUrl = "http://node1:6333"
        };

        // Act
        var result = await _controller.DeleteSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteSnapshot_AllNodes_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            SingleNode = false
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true,
            ["http://node2:6333"] = true
        };

        _snapshotService.DeleteCollectionSnapshotOnAllNodesAsync(request.CollectionName, request.SnapshotName, Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteSnapshotResponse;
        Assert.That(response!.Success, Is.True);
    }

    #endregion

    #region DownloadSnapshot Tests

    [Test]
    public async Task DownloadSnapshot_WhenSuccessful_ReturnsFile()
    {
        // Arrange
        var request = new V1DownloadSnapshotRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            PodName = "pod1",
            PodNamespace = "default"
        };

        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        _snapshotService.DownloadSnapshotWithFallbackAsync(
            request.NodeUrl,
            request.CollectionName,
            request.SnapshotName,
            request.PodName,
            request.PodNamespace,
            Arg.Any<CancellationToken>())
            .Returns(stream);

        // Act
        var result = await _controller.DownloadSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<FileStreamResult>());
        var fileResult = (FileStreamResult)result;
        Assert.That(fileResult.FileDownloadName, Is.EqualTo(request.SnapshotName));
    }

    [Test]
    public async Task DownloadSnapshot_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1DownloadSnapshotRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            PodName = "pod1",
            PodNamespace = "default"
        };

        _snapshotService.DownloadSnapshotWithFallbackAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns((Stream?)null);

        // Act
        var result = await _controller.DownloadSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion

    #region RecoverFromSnapshot Tests

    [Test]
    public async Task RecoverFromSnapshot_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage",
            TargetNodeUrl = "http://node1:6333"
        };

        _collectionService.RecoverCollectionFromSnapshotAsync(
            request.TargetNodeUrl,
            request.CollectionName,
            request.SnapshotName,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1RecoverFromSnapshotResponse;
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public async Task RecoverFromSnapshot_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1RecoverFromSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage",
            TargetNodeUrl = "http://node1:6333"
        };

        _collectionService.RecoverCollectionFromSnapshotAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion

    #region RecoverFromUrl Tests

    [Test]
    public async Task RecoverFromUrl_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.amazonaws.com/bucket/snapshot.snapshot",
            SnapshotChecksum = "abc123",
            WaitForResult = true
        };

        _collectionService.RecoverCollectionFromUrlAsync(
            request.NodeUrl,
            request.CollectionName,
            request.SnapshotUrl,
            request.SnapshotChecksum,
            request.WaitForResult,
            Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.RecoverFromUrl(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1RecoverFromSnapshotResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Message, Does.Contain(request.SnapshotUrl));
    }

    [Test]
    public async Task RecoverFromUrl_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1RecoverFromUrlRequest
        {
            NodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.amazonaws.com/bucket/snapshot.snapshot"
        };

        _collectionService.RecoverCollectionFromUrlAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await _controller.RecoverFromUrl(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion
}

