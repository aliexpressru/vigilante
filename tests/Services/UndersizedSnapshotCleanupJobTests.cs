using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class UndersizedSnapshotCleanupJobTests
{
    private IClusterManager _clusterManager = null!;
    private ISnapshotService _snapshotService = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _snapshotService = Substitute.For<ISnapshotService>();
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_clusterManager)
            .AddSingleton(_snapshotService)
            .AddLogging()
            .BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    private static IReadOnlyList<NodeInfo> TestNodes() =>
        [new NodeInfo { Url = "http://n1:6333", IsHealthy = true, PodName = "p1", PeerId = "peer1", Namespace = "qdrant" }];

    private static DynamicConfig ConfigWithMinPercent(decimal percent) =>
        new()
        {
            Snapshot = new SnapshotConfiguration { MinSnapshotSizePercentOfCollection = percent }
        };

    private UndersizedSnapshotCleanupJob CreateJob(DynamicConfig? config = null) =>
        new(_serviceProvider, TestNodes(), config ?? ConfigWithMinPercent(50m));

    private static CollectionInfo CollectionRow(string collectionName, string nodeUrl, long sizeBytes, string peerId = "peer") =>
        new()
        {
            CollectionName = collectionName,
            NodeUrl = nodeUrl,
            PodName = "pod",
            PeerId = peerId,
            PodNamespace = "default",
            Metrics = new CollectionMetrics { SizeBytes = sizeBytes }
        };

    private static SnapshotInfo ApiSnapshot(
        string collectionName,
        string nodeUrl,
        long sizeBytes,
        string snapshotName = "c.snap",
        string peerId = "peer") =>
        new()
        {
            CollectionName = collectionName,
            NodeUrl = nodeUrl,
            PodName = "pod",
            PeerId = peerId,
            SnapshotName = snapshotName,
            SizeBytes = sizeBytes,
            PodNamespace = "default",
            Source = SnapshotSource.QdrantApi,
            CreatedAt = DateTime.UtcNow
        };

    private static SnapshotInfo S3SnapshotRow(string collectionName, long sizeBytes, string snapshotName = "s3.snap") =>
        new()
        {
            CollectionName = collectionName,
            NodeUrl = S3Constants.StorageIdentifier,
            PodName = S3Constants.StorageIdentifier,
            PeerId = S3Constants.StorageIdentifier,
            SnapshotName = snapshotName,
            SizeBytes = sizeBytes,
            PodNamespace = "default",
            Source = SnapshotSource.S3Storage,
            CreatedAt = DateTime.UtcNow,
            S3StorageModifiedUtc = DateTime.UtcNow
        };

    [Test]
    public void Key_ReturnsJobKeyConstant()
    {
        var job = CreateJob();
        Assert.That(job.Key, Is.EqualTo(UndersizedSnapshotCleanupJob.JobKey));
    }

    [Test]
    public void IsWaitingForReady_ReturnsFalse()
    {
        var job = CreateJob();
        Assert.That(job.IsWaitingForReady, Is.False);
    }

    [Test]
    public void GetMetadata_IncludesActionAndStartedAtUtc()
    {
        var before = DateTime.UtcNow;
        var job = CreateJob();
        var meta = job.GetMetadata();

        Assert.That(meta, Is.Not.Null);
        Assert.That(meta![JobMetadataKeys.CurrentAction], Is.EqualTo("Undersized snapshot cleanup"));
        Assert.That(meta[JobMetadataKeys.StartedAtUtc], Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(DateTime.UtcNow.AddSeconds(2)));
    }

    [Test]
    public async Task AdvanceAsync_WhenMinPercentOutOfRange_DoesNotQueryClusterOrSnapshots()
    {
        var job = new UndersizedSnapshotCleanupJob(_serviceProvider, TestNodes(), ConfigWithMinPercent(101m));

        var (hasMore, success, err) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        Assert.That(err, Is.Null);
        await _clusterManager.DidNotReceive().GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _snapshotService.DidNotReceive().GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_WhenMinPercentZero_DoesNotQueryClusterOrSnapshots()
    {
        var job = new UndersizedSnapshotCleanupJob(_serviceProvider, TestNodes(), ConfigWithMinPercent(0m));

        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        await _clusterManager.DidNotReceive().GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenCollectionsLoadFails_ReturnsFailure()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("api down"));

        var job = CreateJob();

        var (hasMore, success, err) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.False);
        Assert.That(err, Is.EqualTo("api down"));
        await _snapshotService.DidNotReceive().GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotsLoadFails_ReturnsFailure()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("c", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .ThrowsAsync(new InvalidOperationException("snap list failed"));

        var job = CreateJob();

        var (hasMore, success, err) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.False);
        Assert.That(err, Is.EqualTo("snap list failed"));
    }

    [Test]
    public async Task AdvanceAsync_WhenApiSnapshotUndersized_DeletesSnapshot()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("my-col", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([ApiSnapshot("my-col", "http://n1:6333", 100L, "small.snap")]);

        var job = CreateJob(ConfigWithMinPercent(50m));

        var (hasMore, success, err) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        Assert.That(err, Is.Null);
        await _snapshotService.Received(1).DeleteSnapshotAsync(
            "my-col",
            "small.snap",
            SnapshotSource.QdrantApi,
            nodeUrl: "http://n1:6333",
            podName: "pod",
            podNamespace: "default",
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenApiSnapshotMeetsMinSize_DoesNotDelete()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("my-col", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([ApiSnapshot("my-col", "http://n1:6333", 600L, "ok.snap")]);

        var job = CreateJob(ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().DeleteSnapshotAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<SnapshotSource>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotSizeBytesZero_DoesNotDelete()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("my-col", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([ApiSnapshot("my-col", "http://n1:6333", 0L, "unknown.snap")]);

        var job = CreateJob(ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().DeleteSnapshotAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<SnapshotSource>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotUndersizedVsMaxOfNodeSizes_Deletes()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                CollectionRow("dist", "http://n1:6333", 400L),
                CollectionRow("dist", "http://n2:6333", 600L)
            ]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([S3SnapshotRow("dist", 200L, "bundle.snap")]);

        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://n1:6333", IsHealthy = true, PodName = "p1", PeerId = "a", Namespace = "qdrant" },
            new() { Url = "http://n2:6333", IsHealthy = true, PodName = "p2", PeerId = "b", Namespace = "qdrant" }
        };
        var job = new UndersizedSnapshotCleanupJob(_serviceProvider, nodes, ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).DeleteSnapshotAsync(
            "dist",
            "bundle.snap",
            SnapshotSource.S3Storage,
            nodeUrl: S3Constants.StorageIdentifier,
            podName: S3Constants.StorageIdentifier,
            podNamespace: "default",
            cancellationToken: Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Replicated cluster: two nodes report similar on-disk size; sum would double-count and mark a valid single-file S3 backup undersized.
    /// </summary>
    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotMeetsHalfOfMaxNodeSize_DoesNotDelete()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                CollectionRow("rep", "http://n1:6333", 500L),
                CollectionRow("rep", "http://n2:6333", 500L)
            ]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([S3SnapshotRow("rep", 300L, "ok-s3.snap")]);

        var nodes = new List<NodeInfo>
        {
            new() { Url = "http://n1:6333", IsHealthy = true, PodName = "p1", PeerId = "a", Namespace = "qdrant" },
            new() { Url = "http://n2:6333", IsHealthy = true, PodName = "p2", PeerId = "b", Namespace = "qdrant" }
        };
        var job = new UndersizedSnapshotCleanupJob(_serviceProvider, nodes, ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().DeleteSnapshotAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<SnapshotSource>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenApiSnapshotNodeUrlDiffersButPeerIdMatches_DeletesIfUndersized()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("my-col", "http://n1:6333", 1000L, peerId: "peer-a")]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([ApiSnapshot("my-col", "http://other-host:6333", 100L, "small.snap", peerId: "peer-a")]);

        var job = CreateJob(ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).DeleteSnapshotAsync(
            "my-col",
            "small.snap",
            SnapshotSource.QdrantApi,
            nodeUrl: "http://other-host:6333",
            podName: "pod",
            podNamespace: "default",
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotCollectionMissingFromCluster_SkipsWithoutDelete()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("only-this", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([ApiSnapshot("ghost-col", "http://n1:6333", 10L, "x.snap")]);

        var job = CreateJob(ConfigWithMinPercent(50m));

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().DeleteSnapshotAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<SnapshotSource>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_WhenDeleteThrows_ContinuesAndCompletesSuccess()
    {
        _clusterManager
            .GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([CollectionRow("c", "http://n1:6333", 1000L)]);
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(
            [
                ApiSnapshot("c", "http://n1:6333", 50L, "a.snap"),
                ApiSnapshot("c", "http://n1:6333", 40L, "b.snap")
            ]);

        _snapshotService
            .DeleteSnapshotAsync("c", "a.snap", Arg.Any<SnapshotSource>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk busy"));

        var job = CreateJob(ConfigWithMinPercent(50m));

        var (hasMore, success, err) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        Assert.That(err, Is.Null);
        await _snapshotService.Received(1).DeleteSnapshotAsync(
            "c",
            "b.snap",
            SnapshotSource.QdrantApi,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
