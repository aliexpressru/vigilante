using FluentAssertions;
using FluentAssertions.Execution;
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
public class CollectionsControllerTests
{
    private IClusterManager _clusterManager = null!;
    private IRestoreReplicationFactorJobService _restoreReplicationFactorJobService = null!;
    private ILogger<CollectionsController> _logger = null!;
    private CollectionsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _restoreReplicationFactorJobService = Substitute.For<IRestoreReplicationFactorJobService>();
        _logger = Substitute.For<ILogger<CollectionsController>>();

        _controller = new CollectionsController(_clusterManager, _restoreReplicationFactorJobService, _logger);
    }

    #region GetCollectionsInfo (Paginated) Tests

    [Test]
    public async Task GetCollectionsInfo_FirstPage_ReturnsCorrectCollections()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = 2,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection2",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            Page = 1,
            PageSize = 2
        };

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        response.Should().NotBeNull();
        response!.Collections.Should().HaveCount(3); // Both nodes of collection1 + collection2
        response.Pagination.CurrentPage.Should().Be(1);
        response.Pagination.TotalItems.Should().Be(2); // 2 unique collections
        response.Pagination.TotalPages.Should().Be(1);
    }

    [Test]
    public async Task GetCollectionsInfo_SecondPage_ReturnsCorrectCollections()
    {
        // Arrange
        var collections = new List<CollectionInfo>();
        for (int i = 1; i <= 5; i++)
        {
            collections.Add(new CollectionInfo
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = $"collection{i}",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            });
        }

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            Page = 2,
            PageSize = 2
        };

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;
        using (new AssertionScope())
        {
            response!.Collections.Should().HaveCount(2);
            response.Collections[0].CollectionName.Should().Be("collection3");
            response.Collections[1].CollectionName.Should().Be("collection4");
            response.Pagination.CurrentPage.Should().Be(2);
            response.Pagination.TotalItems.Should().Be(5);
            response.Pagination.TotalPages.Should().Be(3);
        }
    }

    [Test]
    public async Task GetCollectionsInfo_WithNameFilter_ReturnsFilteredCollections()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "test_collection_1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "other_collection",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "test_collection_2",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            NameFilter = "test",
            PageSize = 10
        };

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;
        response!.Collections.Should().HaveCount(2);
        response.Pagination.TotalItems.Should().Be(2);
        response.Collections.All(c => c.CollectionName.Contains("test")).Should().BeTrue();
    }

    [Test]
    public async Task GetCollectionsInfo_WithIssues_ReturnsFormattedIssues()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = ["Issue 1", "Issue 2"]
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest();

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;
        response!.Issues.Should().HaveCount(2);
        response.Issues[0].Should().Contain("[collection1@pod1]");
    }

    [Test]
    public async Task GetCollectionsInfo_ClearCacheTrue_CallsServiceWithClearCache()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            ClearCache = true
        };

        // Act
        await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        await _clusterManager.Received(1).GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCollectionsInfo_MultipleNodesPerCollection_GroupsCorrectly()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = 2,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            },
            new()
            {
                PodName = "pod3",
                NodeUrl = "http://node3:6333",
                PeerId = 3,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            PageSize = 10
        };

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;

        using (new AssertionScope())
        {
            response!.Collections.Should().HaveCount(3); // All 3 nodes for collection1
            response.Pagination.TotalItems.Should().Be(1); // Only 1 unique collection
            response.Collections.All(c => c.CollectionName == "collection1").Should().BeTrue();
        }
    }

    [Test]
    public async Task GetCollectionsInfo_EmptyResult_ReturnsEmptyArray()
    {
        // Arrange
        var collections = new List<CollectionInfo>();

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest();

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;
        response!.Collections.Should().BeEmpty();
        response.Pagination.TotalItems.Should().Be(0);
        response.Pagination.TotalPages.Should().Be(0);
    }

    [Test]
    public async Task GetCollectionsInfo_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<CollectionInfo>>(new Exception("Test error")));

        var request = new V1GetCollectionsInfoRequest();

        // Act
        var result = await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region DeleteCollection Tests

    [Test]
    public async Task DeleteCollection_SingleNodeViaApi_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            NodeUrls = ["http://node1:6333"],
            DeletionType = CollectionDeletionType.Api
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true
        };

        _clusterManager.DeleteCollectionViaApiAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 1),
            Arg.Any<bool?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        response!.Success.Should().BeTrue();
    }

    [Test]
    public async Task DeleteCollection_SpecificNodesFromDisk_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            DeletionType = CollectionDeletionType.Disk,
            Pods =
            [
                new() { PodName = "pod1", PodNamespace = "default" }
            ]
        };

        var results = new Dictionary<string, bool>
        {
            ["pod1"] = true
        };

        _clusterManager.DeleteCollectionFromDiskAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<(string, string)>>(pods => pods.Count() == 1),
            Arg.Any<bool?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        response!.Success.Should().BeTrue();
        response.Results.Should().ContainKey("pod1");
    }

    [Test]
    public async Task DeleteCollection_MultipleNodesViaApi_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            DeletionType = CollectionDeletionType.Api,
            NodeUrls =
            [
                "http://node1:6333",
                "http://node2:6333"
            ]
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true,
            ["http://node2:6333"] = true
        };

        _clusterManager.DeleteCollectionViaApiAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 2),
            Arg.Any<bool?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        response!.Success.Should().BeTrue();
        response.Results.Should().HaveCount(2);
    }

    [Test]
    public async Task DeleteCollection_MultipleNodesFromDisk_WhenPartialSuccess_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            DeletionType = CollectionDeletionType.Disk,
            Pods =
            [
                new() { PodName = "pod1", PodNamespace = "default" },
                new() { PodName = "pod2", PodNamespace = "default" }
            ]
        };

        var results = new Dictionary<string, bool>
        {
            ["pod1"] = true,
            ["pod2"] = false
        };

        _clusterManager.DeleteCollectionFromDiskAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<(string, string)>>(pods => pods.Count() == 2),
            Arg.Any<bool?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        response!.Success.Should().BeTrue();
        response.Message.Should().Contain("1/2");
    }

    [Test]
    public async Task DeleteCollection_WhenAllFail_Returns500()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            DeletionType = CollectionDeletionType.Api,
            NodeUrls =
            [
                "http://node1:6333",
                "http://node2:6333"
            ]
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = false,
            ["http://node2:6333"] = false
        };

        _clusterManager.DeleteCollectionViaApiAsync(
            request.CollectionName,
            Arg.Is<IEnumerable<string>>(urls => urls.Count() == 2),
            Arg.Any<bool?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion

    #region Cache Reset Tests

    [Test]
    public async Task GetCollectionsInfo_WithClearCacheTrue_CallsGetCollectionsInfoWithTrue()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            ClearCache = true,
            PageSize = 10
        };

        // Act
        await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        await _clusterManager.Received(1).GetCollectionsInfoAsync(true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCollectionsInfo_WithClearCacheFalse_CallsGetCollectionsInfoWithFalse()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            ClearCache = false,
            PageSize = 10
        };

        // Act
        await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        await _clusterManager.Received(1).GetCollectionsInfoAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCollectionsInfo_DefaultClearCache_CallsGetCollectionsInfoWithFalse()
    {
        // Arrange
        var collections = new List<CollectionInfo>
        {
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = 1,
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = [],
                Issues = []
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(collections);

        var request = new V1GetCollectionsInfoRequest
        {
            PageSize = 10
        };

        // Act
        await _controller.GetCollectionsInfo(request, CancellationToken.None);

        // Assert
        await _clusterManager.Received(1).GetCollectionsInfoAsync(false, Arg.Any<CancellationToken>());
    }

    #endregion

    #region SetCollectionAlias Tests

    [Test]
    public async Task SetCollectionAlias_WhenSuccessful_ReturnsOk()
    {
        var request = new V1SetCollectionAliasRequest
        {
            CollectionName = "my_collection",
            AliasName = "my_alias"
        };
        _clusterManager.SetCollectionAliasAsync(request.CollectionName, request.AliasName, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.SetCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1SetCollectionAliasResponse;
        response!.Success.Should().BeTrue();
        response.Message.Should().Contain("my_alias");
        response.Message.Should().Contain("my_collection");
    }

    [Test]
    public async Task SetCollectionAlias_WhenFails_Returns500()
    {
        var request = new V1SetCollectionAliasRequest
        {
            CollectionName = "my_collection",
            AliasName = "my_alias"
        };
        _clusterManager.SetCollectionAliasAsync(request.CollectionName, request.AliasName, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _controller.SetCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
        var response = objectResult.Value as V1SetCollectionAliasResponse;
        response!.Success.Should().BeFalse();
    }

    #endregion

    #region RenameCollectionAlias Tests

    [Test]
    public async Task RenameCollectionAlias_WhenSuccessful_ReturnsOk()
    {
        var request = new V1RenameCollectionAliasRequest
        {
            OldAliasName = "old_alias",
            NewAliasName = "new_alias"
        };
        _clusterManager.RenameCollectionAliasAsync(request.OldAliasName, request.NewAliasName, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.RenameCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1RenameCollectionAliasResponse;
        response!.Success.Should().BeTrue();
        response.Message.Should().Contain("old_alias");
        response.Message.Should().Contain("new_alias");
    }

    [Test]
    public async Task RenameCollectionAlias_WhenFails_Returns500()
    {
        var request = new V1RenameCollectionAliasRequest
        {
            OldAliasName = "old_alias",
            NewAliasName = "new_alias"
        };
        _clusterManager.RenameCollectionAliasAsync(request.OldAliasName, request.NewAliasName, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _controller.RenameCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
        var response = objectResult.Value as V1RenameCollectionAliasResponse;
        response!.Success.Should().BeFalse();
    }

    #endregion

    #region DeleteCollectionAlias Tests

    [Test]
    public async Task DeleteCollectionAlias_WhenSuccessful_ReturnsOk()
    {
        var request = new V1DeleteCollectionAliasRequest
        {
            AliasName = "my_alias"
        };
        _clusterManager.DeleteCollectionAliasAsync(request.AliasName, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.DeleteCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<OkObjectResult>();
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionAliasResponse;
        response!.Success.Should().BeTrue();
        response.Message.Should().Contain("my_alias");
    }

    [Test]
    public async Task DeleteCollectionAlias_WhenFails_Returns500()
    {
        var request = new V1DeleteCollectionAliasRequest
        {
            AliasName = "my_alias"
        };
        _clusterManager.DeleteCollectionAliasAsync(request.AliasName, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _controller.DeleteCollectionAlias(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
        var response = objectResult.Value as V1DeleteCollectionAliasResponse;
        response!.Success.Should().BeFalse();
    }

    #endregion

    #region RestoreReplicationFactor Tests

    [Test]
    public async Task RestoreReplicationFactor_WhenStarted_Returns202Accepted()
    {
        _restoreReplicationFactorJobService
            .RequestRestoreReplicationFactorAsync(Arg.Any<string>(), Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreReplicationFactorStartResult(
                ApiError: false,
                AlreadyInProgress: false,
                Message: "Restore replication factor process started for collection col1"));

        var request = new V1RestoreReplicationFactorRequest { CollectionName = "col1" };

        var result = await _controller.RestoreReplicationFactor(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<AcceptedResult>();
        var accepted = (AcceptedResult)result.Result!;
        var response = accepted.Value as V1RestoreReplicationFactorResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("Started");
        response.Message.Should().Contain("col1");
    }

    [Test]
    public async Task RestoreReplicationFactor_WhenAlreadyInProgress_Returns409Conflict()
    {
        _restoreReplicationFactorJobService
            .RequestRestoreReplicationFactorAsync(Arg.Any<string>(), Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreReplicationFactorStartResult(
                ApiError: false,
                AlreadyInProgress: true,
                Message: "Restore replication factor already in progress for this collection"));

        var request = new V1RestoreReplicationFactorRequest { CollectionName = "col1" };

        var result = await _controller.RestoreReplicationFactor(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var conflict = (ObjectResult)result.Result!;
        conflict.StatusCode.Should().Be(409);
        var response = conflict.Value as V1RestoreReplicationFactorResponse;
        response.Should().NotBeNull();
        response!.Status.Should().Be("AlreadyInProgress");
    }

    [Test]
    public async Task RestoreReplicationFactor_WhenApiError_Returns500()
    {
        _restoreReplicationFactorJobService
            .RequestRestoreReplicationFactorAsync(Arg.Any<string>(), Arg.Any<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreReplicationFactorStartResult(
                ApiError: true,
                AlreadyInProgress: false,
                Message: "No healthy node available"));

        var request = new V1RestoreReplicationFactorRequest { CollectionName = "col1" };

        var result = await _controller.RestoreReplicationFactor(request, CancellationToken.None);

        result.Result.Should().BeAssignableTo<ObjectResult>();
        var objectResult = (ObjectResult)result.Result!;
        objectResult.StatusCode.Should().Be(500);
    }

    #endregion
}

