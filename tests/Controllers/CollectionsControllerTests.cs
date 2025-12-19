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
    private ILogger<CollectionsController> _logger = null!;
    private CollectionsController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _logger = Substitute.For<ILogger<CollectionsController>>();
        
        _controller = new CollectionsController(_clusterManager, _logger);
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = "peer2",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "peer1",
                CollectionName = "collection2",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoPaginatedResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Collections, Has.Length.EqualTo(3)); // Both nodes of collection1 + collection2
        Assert.That(response.Pagination.CurrentPage, Is.EqualTo(1));
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(2)); // 2 unique collections
        Assert.That(response.Pagination.TotalPages, Is.EqualTo(1));
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
                PeerId = "peer1",
                CollectionName = $"collection{i}",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
        Assert.That(response!.Collections, Has.Length.EqualTo(2));
        Assert.That(response.Collections[0].CollectionName, Is.EqualTo("collection3"));
        Assert.That(response.Collections[1].CollectionName, Is.EqualTo("collection4"));
        Assert.That(response.Pagination.CurrentPage, Is.EqualTo(2));
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(5));
        Assert.That(response.Pagination.TotalPages, Is.EqualTo(3));
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
                PeerId = "peer1",
                CollectionName = "test_collection_1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "peer1",
                CollectionName = "other_collection",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod1",
                NodeUrl = "http://node1:6333",
                PeerId = "peer1",
                CollectionName = "test_collection_2",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
        Assert.That(response!.Collections, Has.Length.EqualTo(2));
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(2));
        Assert.That(response.Collections.All(c => c.CollectionName.Contains("test")), Is.True);
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string> { "Issue 1", "Issue 2" }
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
        Assert.That(response!.Issues, Has.Length.EqualTo(2));
        Assert.That(response.Issues[0], Does.Contain("[collection1@pod1]"));
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod2",
                NodeUrl = "http://node2:6333",
                PeerId = "peer2",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
            },
            new()
            {
                PodName = "pod3",
                NodeUrl = "http://node3:6333",
                PeerId = "peer3",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
        Assert.That(response!.Collections, Has.Length.EqualTo(3)); // All 3 nodes for collection1
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(1)); // Only 1 unique collection
        Assert.That(response.Collections.All(c => c.CollectionName == "collection1"), Is.True);
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
        Assert.That(response!.Collections, Is.Empty);
        Assert.That(response.Pagination.TotalItems, Is.EqualTo(0));
        Assert.That(response.Pagination.TotalPages, Is.EqualTo(0));
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
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
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
            SingleNode = true,
            NodeUrl = "http://node1:6333",
            DeletionType = CollectionDeletionType.Api
        };

        _clusterManager.DeleteCollectionViaApiAsync(request.NodeUrl, request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public async Task DeleteCollection_SingleNodeFromDisk_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            SingleNode = true,
            PodName = "pod1",
            PodNamespace = "default",
            DeletionType = CollectionDeletionType.Disk
        };

        _clusterManager.DeleteCollectionFromDiskAsync(request.PodName!, request.PodNamespace!, request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Results, Contains.Key(request.PodName!));
    }

    [Test]
    public async Task DeleteCollection_AllNodesViaApi_WhenSuccessful_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            SingleNode = false,
            DeletionType = CollectionDeletionType.Api
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = true,
            ["http://node2:6333"] = true
        };

        _clusterManager.DeleteCollectionViaApiOnAllNodesAsync(request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Results, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DeleteCollection_AllNodesFromDisk_WhenPartialSuccess_ReturnsOk()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            SingleNode = false,
            DeletionType = CollectionDeletionType.Disk
        };

        var results = new Dictionary<string, bool>
        {
            ["pod1"] = true,
            ["pod2"] = false
        };

        _clusterManager.DeleteCollectionFromDiskOnAllNodesAsync(request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1DeleteCollectionResponse;
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Message, Does.Contain("1/2"));
    }

    [Test]
    public async Task DeleteCollection_WhenAllFail_Returns500()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            SingleNode = false,
            DeletionType = CollectionDeletionType.Api
        };

        var results = new Dictionary<string, bool>
        {
            ["http://node1:6333"] = false,
            ["http://node2:6333"] = false
        };

        _clusterManager.DeleteCollectionViaApiOnAllNodesAsync(request.CollectionName, Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
                PeerId = "peer1",
                CollectionName = "collection1",
                PodNamespace = "default",
                Metrics = new Dictionary<string, object>(),
                Issues = new List<string>()
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
}

