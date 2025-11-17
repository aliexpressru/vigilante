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

    #region GetCollectionsInfo Tests

    [Test]
    public async Task GetCollectionsInfo_WhenSuccessful_ReturnsOkWithCollections()
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

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<CancellationToken>())
            .Returns(collections);

        // Act
        var result = await _controller.GetCollectionsInfo(CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Collections, Has.Length.EqualTo(1));
        Assert.That(response.Collections[0].CollectionName, Is.EqualTo("collection1"));
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

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<CancellationToken>())
            .Returns(collections);

        // Act
        var result = await _controller.GetCollectionsInfo(CancellationToken.None);

        // Assert
        var okResult = (OkObjectResult)result.Result!;
        var response = okResult.Value as V1GetCollectionsInfoResponse;
        Assert.That(response!.Issues, Has.Length.EqualTo(2));
        Assert.That(response.Issues[0], Does.Contain("[collection1@pod1]"));
    }

    [Test]
    public async Task GetCollectionsInfo_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<CollectionInfo>>(new Exception("Test error")));

        // Act
        var result = await _controller.GetCollectionsInfo(CancellationToken.None);

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

    [Test]
    public async Task DeleteCollection_WhenExceptionThrown_Returns500()
    {
        // Arrange
        var request = new V1DeleteCollectionRequest
        {
            CollectionName = "test_collection",
            SingleNode = true,
            NodeUrl = "http://node1:6333",
            DeletionType = CollectionDeletionType.Api
        };

        _clusterManager.DeleteCollectionViaApiAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new Exception("Test error")));

        // Act
        var result = await _controller.DeleteCollection(request, CancellationToken.None);

        // Assert
        Assert.That(result.Result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result.Result!;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    #endregion
}

