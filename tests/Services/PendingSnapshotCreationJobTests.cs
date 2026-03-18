using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class PendingSnapshotCreationJobTests
{
    private ISnapshotService _snapshotService = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _snapshotService = Substitute.For<ISnapshotService>();
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddLogging()
            .BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    private static IReadOnlyList<NodeInfo> NodeList(params string[] urls) =>
        urls.Select(url => new NodeInfo { Url = url }).ToList();

    private static SnapshotInfo Snapshot(string collectionName, string nodeUrl, DateTime createdAt, SnapshotSource source = SnapshotSource.QdrantApi) =>
        new()
        {
            PodName = "pod-1",
            NodeUrl = nodeUrl,
            PeerId = "peer-1",
            CollectionName = collectionName,
            SnapshotName = "snap-1",
            SizeBytes = 0,
            PodNamespace = "default",
            Source = source,
            CreatedAt = createdAt
        };

    private static SnapshotInfo S3Snapshot(string collectionName, DateTime createdAt, string snapshotName = "snap-1") =>
        new()
        {
            PodName = S3Constants.StorageIdentifier,
            NodeUrl = S3Constants.StorageIdentifier,
            PeerId = S3Constants.StorageIdentifier,
            CollectionName = collectionName,
            SnapshotName = snapshotName,
            SizeBytes = 0,
            PodNamespace = "default",
            Source = SnapshotSource.S3Storage,
            CreatedAt = createdAt
        };

    private PendingSnapshotCreationJob CreateJob(
        string collectionName = "my-collection",
        IReadOnlyList<NodeInfo>? nodes = null,
        DateTime? requestedAtUtc = null,
        int? retainLastNAfterVisible = null,
        IReadOnlySet<string>? retentionClusterPeerIds = null)
    {
        return new PendingSnapshotCreationJob(
            _serviceProvider,
            collectionName,
            nodes ?? NodeList("http://node1:6333"),
            requestedAtUtc ?? DateTime.UtcNow,
            retainLastNAfterVisible,
            retentionClusterPeerIds);
    }

    [Test]
    public void Key_ReturnsPrefixAndCollectionName()
    {
        var job = CreateJob("my-collection");

        Assert.That(job.Key, Is.EqualTo(PendingSnapshotCreationJob.KeyPrefix + "my-collection"));
    }

    [Test]
    public void IsWaitingForReady_ReturnsFalse()
    {
        var job = CreateJob("c");
        Assert.That(job.IsWaitingForReady, Is.False);
    }

    [Test]
    public void GetMetadata_ReturnsCurrentActionWithCollectionName()
    {
        var job = CreateJob("my-collection");

        var metadata = job.GetMetadata();

        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata![PendingSnapshotCreationJob.MetadataCurrentAction], Is.EqualTo("Waiting for snapshot: my-collection"));
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotsAppearOnAllNodes_CompletesSuccessfully()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        Assert.That(errorMessage, Is.Null);
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotMissingOnSomeNodes_ReturnsHasMore()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333", "http://node2:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.True);
        Assert.That(success, Is.True);
        Assert.That(errorMessage, Is.Null);
    }

    [Test]
    public async Task AdvanceAsync_WhenTimeoutExceeded_FailsWithErrorMessage()
    {
        var requestedAt = DateTime.UtcNow - PendingSnapshotCreationJob.Timeout - TimeSpan.FromMinutes(1);
        var job = CreateJob("col", requestedAtUtc: requestedAt);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.False);
        Assert.That(errorMessage, Does.Contain("Snapshot did not appear within"));
        await _snapshotService.DidNotReceive()
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_WhenGetSnapshotsThrows_FailsWithExceptionMessage()
    {
        var job = CreateJob("col");
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(Task.FromException<IReadOnlyList<SnapshotInfo>>(new InvalidOperationException("Node unavailable")));

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.False);
        Assert.That(errorMessage, Is.EqualTo("Node unavailable"));
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotCreatedBeforeCutoff_StillWaits()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(-5))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.True);
        Assert.That(success, Is.True);
        Assert.That(errorMessage, Is.Null);
    }

    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotsAppearWithEnoughNewCount_CompletesSuccessfully()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333", "http://node2:6333", "http://node3:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", requestedAt.AddSeconds(1), "col-peer1-1.snapshot"),
            S3Snapshot("col", requestedAt.AddSeconds(2), "col-peer2-2.snapshot"),
            S3Snapshot("col", requestedAt.AddSeconds(3), "col-peer3-3.snapshot")
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        Assert.That(errorMessage, Is.Null);
    }

    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotsButNotEnoughNew_ReturnsHasMore()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333", "http://node2:6333", "http://node3:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", requestedAt.AddSeconds(1), "col-peer1-1.snapshot"),
            S3Snapshot("col", requestedAt.AddSeconds(2), "col-peer2-2.snapshot")
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.True);
        Assert.That(success, Is.True);
        Assert.That(errorMessage, Is.Null);
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotsVisible_AppliesRetentionWhenConfigured()
    {
        var requestedAt = DateTime.UtcNow.AddMinutes(-1);
        var nodes = NodeList("http://node1:6333");
        var peerIds = new HashSet<string> { "p1" };
        var job = CreateJob("col", nodes, requestedAt, retainLastNAfterVisible: 3, retentionClusterPeerIds: peerIds);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        await _snapshotService.Received(1).EnforceRetentionAsync("col", 3, peerIds, Arg.Any<CancellationToken>());
    }
}
