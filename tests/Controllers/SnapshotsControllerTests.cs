using Aer.QdrantClient.Http.Models.Shared;
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
using SnapshotInfo = Vigilante.Models.SnapshotInfo;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class SnapshotsControllerTests
{
    private ISnapshotService _snapshotService = null!;
    private IS3SnapshotService _s3SnapshotService = null!;
    private IClusterManager _clusterManager = null!;
    private ILogger<SnapshotsController> _logger = null!;
    private SnapshotsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _snapshotService = Substitute.For<ISnapshotService>();
        _s3SnapshotService = Substitute.For<IS3SnapshotService>();
        _clusterManager = Substitute.For<IClusterManager>();
        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ClusterState { Nodes = [] });
        _logger = Substitute.For<ILogger<SnapshotsController>>();
        _controller = new SnapshotsController(_snapshotService, _s3SnapshotService, _clusterManager, _logger);
    }

    #region GetSnapshotsInfo Tests

    [Test]
    public async Task GetSnapshotsInfo_WhenSuccessful_ReturnsPaginatedSnapshots()
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
                SnapshotName = "collection1-peer1-snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = "peer2",
                CollectionName = "collection2",
                SnapshotName = "collection2-peer2-snapshot2.snapshot",
                SizeBytes = 2048,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            Page = 1,
            PageSize = 10,
            ClearCache = false
        };

        // Act
        var result = await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetSnapshotsInfoPaginatedResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.GroupedSnapshots, Has.Length.EqualTo(2));
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(2));
        Assert.That(response.Pagination.CurrentPage, Is.EqualTo(1));
    }

    [Test]
    public async Task GetSnapshotsInfo_WithFilter_ReturnsFilteredSnapshots()
    {
        // Arrange
        var snapshots = new List<SnapshotInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "peer1",
                CollectionName = "test_collection",
                SnapshotName = "test_collection-peer1-snapshot.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = "peer2",
                CollectionName = "other_collection",
                SnapshotName = "other_collection-peer2-snapshot.snapshot",
                SizeBytes = 2048,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            Page = 1,
            PageSize = 10,
            NameFilter = "test",
            ClearCache = false
        };

        // Act
        var result = await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetSnapshotsInfoPaginatedResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.GroupedSnapshots, Has.Length.EqualTo(1));
        Assert.That(response.GroupedSnapshots[0].CollectionName, Is.EqualTo("test_collection"));
    }

    [Test]
    public async Task GetSnapshotsInfo_WithPagination_ReturnsCorrectPage()
    {
        // Arrange
        var snapshots = Enumerable.Range(1, 15).Select(i => new SnapshotInfo
        {
            PodName = $"pod{i}",
            NodeUrl = $"http://node{i}:6333",
            PeerId = $"peer{i}",
            CollectionName = $"collection{i}",
            SnapshotName = $"collection{i}-peer{i}-snapshot.snapshot",
            SizeBytes = 1024 * i,
            PodNamespace = "default",
            Source = SnapshotSource.KubernetesStorage
        }).ToList();

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            Page = 2,
            PageSize = 5,
            ClearCache = false
        };

        // Act
        var result = await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetSnapshotsInfoPaginatedResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.GroupedSnapshots, Has.Length.EqualTo(5));
        Assert.That(response.Pagination.CurrentPage, Is.EqualTo(2));
        Assert.That(response.Pagination.PageSize, Is.EqualTo(5));
        Assert.That(response.Pagination.TotalPages, Is.EqualTo(3));
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(15));
    }

    [Test]
    public async Task GetSnapshotsInfo_WithClearCache_PassesTrueToService()
    {
        // Arrange
        var snapshots = new List<SnapshotInfo>();
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            Page = 1,
            PageSize = 10,
            ClearCache = true
        };

        // Act
        await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), true, Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task GetSnapshotsInfo_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(Task.FromException<IReadOnlyList<SnapshotInfo>>(new Exception("Test error")));

        var request = new V1GetSnapshotsInfoRequest
        {
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _controller.GetSnapshotsInfo(request, CancellationToken.None);

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
            NodeUrls = ["http://node1:6333"]
        };

        var results = new Dictionary<string, string?>
        {
            ["http://node1:6333"] = "snapshot-123.snapshot"
        };

        _snapshotService.CreateCollectionSnapshotAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 1),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = results,
                SkippedDuplicatePending = false
            }));

        // Act
        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1CreateSnapshotResponse;
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public async Task CreateSnapshot_SingleNode_WhenFailed_Returns500()
    {
        // Arrange
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            NodeUrls = ["http://node1:6333"]
        };

        var results = new Dictionary<string, string?>
        {
            ["http://node1:6333"] = null
        };

        _snapshotService.CreateCollectionSnapshotAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 1),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = results,
                SkippedDuplicatePending = false
            }));

        // Act
        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task CreateSnapshot_WhenDuplicatePending_Returns409()
    {
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            NodeUrls = ["http://node1:6333"]
        };

        _snapshotService.CreateCollectionSnapshotAsync(
                request.CollectionName,
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateCollectionSnapshotBatchResult.SkippedForNodes(request.NodeUrls!)));

        var result = await _controller.CreateSnapshot(request, CancellationToken.None);

        Assert.That(result.Result, Is.InstanceOf<ConflictObjectResult>());
        var conflict = (ConflictObjectResult)result.Result!;
        Assert.That(conflict.StatusCode, Is.EqualTo(409));
        var response = conflict.Value as V1CreateSnapshotResponse;
        Assert.That(response!.Success, Is.False);
        Assert.That(response.Message, Does.Contain("already in progress"));
    }

    [Test]
    public async Task CreateSnapshot_AllNodes_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1CreateSnapshotRequest
        {
            CollectionName = "test_collection",
            NodeUrls = ["http://node1:6333", "http://node2:6333"]
        };

        var results = new Dictionary<string, string?>
        {
            ["http://node1:6333"] = "snapshot-1.snapshot",
            ["http://node2:6333"] = "snapshot-2.snapshot"
        };

        _snapshotService.CreateCollectionSnapshotAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 2),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = results,
                SkippedDuplicatePending = false
            }));

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
            Source = SnapshotSource.QdrantApi,
            NodeUrls = ["http://node1:6333"]
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true
        };

        _snapshotService.DeleteCollectionSnapshotApiAsync(
            request.CollectionName,
            request.SnapshotName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 1),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task DeleteSnapshot_KubernetesStorage_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = SnapshotSource.KubernetesStorage,
            Pods =
            [
                new() { PodName = "pod1", PodNamespace = "default" }
            ]
        };

        var results = new Dictionary<string, bool>
        {
            ["pod1"] = true
        };

        _snapshotService.DeleteSnapshotFromDiskAsync(
            request.CollectionName,
            request.SnapshotName,
            Arg.Is<IEnumerable<(string, string)>>(pods => pods.Count() == 1),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task DeleteSnapshot_MultipleNodes_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteSnapshotRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = SnapshotSource.QdrantApi,
            NodeUrls = ["http://node1:6333", "http://node2:6333"]
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true,
            ["http://node2:6333"] = true
        };

        _snapshotService.DeleteCollectionSnapshotApiAsync(
            request.CollectionName,
            request.SnapshotName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 2),
            Arg.Any<CancellationToken>())
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
            Arg.Any<CancellationToken>(),
            request.PodName,
            request.PodNamespace)
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
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>())
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
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage",
            TargetNodeUrl = "http://node1:6333"
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                SnapshotSource.KubernetesStorage,
                request.SnapshotName,
                request.SourceCollectionName,
                null,
                null)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result.Result!;
        var response = ok.Value as V1RecoverFromSnapshotResponse;
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public async Task RecoverFromSnapshot_WhenAlreadyInProgress_Returns409()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage",
            TargetNodeUrl = "http://node1:6333"
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                SnapshotSource.KubernetesStorage,
                request.SnapshotName,
                request.SourceCollectionName,
                null,
                null)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: true,
                Message: "Already running"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task RecoverFromSnapshot_WithS3Storage_GeneratesPresignedUrl()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            CollectionName = "new_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "S3Storage",
            TargetNodeUrl = "http://node1:6333",
            SourceCollectionName = "original_collection"
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                SnapshotSource.S3Storage,
                request.SnapshotName,
                request.SourceCollectionName,
                null,
                null)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());

        await _snapshotService.Received(1).RequestRecoverAsync(
            request.CollectionName,
            request.TargetNodeUrl,
            Arg.Any<SnapshotPriority>(),
            request.WaitForResult,
            Arg.Any<CancellationToken>(),
            SnapshotSource.S3Storage,
            request.SnapshotName,
            request.SourceCollectionName,
            null,
            null);
    }

    [Test]
    public async Task RecoverFromSnapshot_WithS3Storage_NoSourceCollectionName_UsesCollectionName()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "S3Storage",
            TargetNodeUrl = "http://node1:6333"
            // SourceCollectionName not provided
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                SnapshotSource.S3Storage,
                request.SnapshotName,
                request.SourceCollectionName,
                null,
                null)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());

        await _snapshotService.Received(1).RequestRecoverAsync(
            request.CollectionName,
            request.TargetNodeUrl,
            Arg.Any<SnapshotPriority>(),
            request.WaitForResult,
            Arg.Any<CancellationToken>(),
            SnapshotSource.S3Storage,
            request.SnapshotName,
            request.SourceCollectionName,
            null,
            null);
    }

    [Test]
    public async Task RecoverFromSnapshot_WhenWaitForResultFalse_ReturnsAccepted()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            CollectionName = "test_collection",
            SnapshotName = "snapshot.snapshot",
            Source = "KubernetesStorage",
            TargetNodeUrl = "http://node1:6333",
            WaitForResult = false
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                SnapshotSource.KubernetesStorage,
                request.SnapshotName,
                request.SourceCollectionName,
                null,
                null)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<AcceptedResult>());
    }

    #endregion

    #region Cache Reset Tests

    [Test]
    public async Task GetSnapshotsInfo_WithClearCacheTrue_CallsGetSnapshotsInfoWithTrue()
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
                SnapshotName = "collection1-peer1-snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            ClearCache = true,
            PageSize = 10
        };

        // Act
        await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), true, Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task GetSnapshotsInfo_WithClearCacheFalse_CallsGetSnapshotsInfoWithFalse()
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
                SnapshotName = "collection1-peer1-snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            ClearCache = false,
            PageSize = 10
        };

        // Act
        await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), false, Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task GetSnapshotsInfo_DefaultClearCache_CallsGetSnapshotsInfoWithFalse()
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
                SnapshotName = "collection1-peer1-snapshot1.snapshot",
                SizeBytes = 1024,
                PodNamespace = "default",
                Source = SnapshotSource.KubernetesStorage
            }
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var request = new V1GetSnapshotsInfoRequest
        {
            PageSize = 10
        };

        // Act
        await _controller.GetSnapshotsInfo(request, CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), false, Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    #endregion

    #region RecoverFromUrl Tests

    [Test]
    public async Task RecoverFromUrl_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            TargetNodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.amazonaws.com/bucket/snapshot.snapshot",
            SnapshotChecksum = "abc123",
            WaitForResult = true
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                null,
                null,
                null,
                request.SnapshotUrl,
                request.SnapshotChecksum)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

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
        var request = new V1RecoverRequest
        {
            TargetNodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.amazonaws.com/bucket/snapshot.snapshot"
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                null,
                null,
                null,
                request.SnapshotUrl,
                request.SnapshotChecksum)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: true,
                AlreadyInProgress: false,
                Message: "Recovery failed"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task RecoverFromUrl_WhenWaitForResultFalse_ReturnsAccepted()
    {
        // Arrange
        var request = new V1RecoverRequest
        {
            TargetNodeUrl = "http://node1:6333",
            CollectionName = "test_collection",
            SnapshotUrl = "https://s3.amazonaws.com/bucket/snapshot.snapshot",
            WaitForResult = false
        };

        _snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                Arg.Any<SnapshotPriority>(),
                request.WaitForResult,
                Arg.Any<CancellationToken>(),
                null,
                null,
                null,
                request.SnapshotUrl,
                request.SnapshotChecksum)
            .Returns(new SnapshotRecoveryStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Recovery started"));

        // Act
        var result = await _controller.RecoverFromSnapshot(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<AcceptedResult>());
    }

    #endregion
}

