using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
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

    private static List<NodeInfo> NodeList(params string[] urls) =>
        [.. urls.Select(url => new NodeInfo { Url = url })];

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

    private static SnapshotInfo S3Snapshot(
        string collectionName,
        DateTime createdAt,
        string snapshotName = "snap-1",
        DateTime? s3ModifiedUtc = null) =>
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
            CreatedAt = createdAt,
            S3StorageModifiedUtc = s3ModifiedUtc ?? createdAt
        };

    private PendingSnapshotCreationJob CreateJob(
        string collectionName = "my-collection",
        IReadOnlyList<NodeInfo>? nodes = null,
        DateTime? requestedAtUtc = null,
        IReadOnlySet<string>? baselineSnapshotKeys = null,
        int? retainLastNAfterVisible = null,
        IReadOnlySet<string>? retentionClusterPeerIds = null,
        TimeSpan? timeout = null)
    {
        return new PendingSnapshotCreationJob(
            _serviceProvider,
            collectionName,
            nodes ?? NodeList("http://node1:6333"),
            requestedAtUtc ?? DateTime.UtcNow,
            baselineSnapshotKeys ?? new HashSet<string>(StringComparer.Ordinal),
            retainLastNAfterVisible,
            retentionClusterPeerIds,
            timeout);
    }

    [Test]
    public void Key_ReturnsPrefixAndCollectionName()
    {
        var job = CreateJob("my-collection");

        job.Key.Should().Be(PendingSnapshotCreationJob.KeyPrefix + "my-collection");
    }

    [Test]
    public void IsWaitingForReady_ReturnsFalse()
    {
        var job = CreateJob("c");
        job.IsWaitingForReady.Should().BeFalse();
    }

    [Test]
    public void GetMetadata_ReturnsCurrentActionWithCollectionName()
    {
        var requestedAt = DateTime.UtcNow;
        var job = CreateJob("my-collection", requestedAtUtc: requestedAt);

        var metadata = job.GetMetadata();

        metadata.Should().NotBeNull();
        metadata![JobMetadataKeys.CurrentAction].Should().Be("Waiting for snapshot: my-collection");
        metadata[JobMetadataKeys.StartedAtUtc].Should().Be(requestedAt);
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotsAppearOnAllNodes_CompletesSuccessfully()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotMissingOnSomeNodes_ReturnsHasMore()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333", "http://node2:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeTrue();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenTimeoutExceeded_FailsWithErrorMessage()
    {
        var requestedAt = DateTime.UtcNow - PendingSnapshotCreationJob.DefaultTimeout - TimeSpan.FromMinutes(1);
        var job = CreateJob("col", requestedAtUtc: requestedAt);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeFalse();
        errorMessage.Should().Contain("Snapshot did not appear within");
        await _snapshotService.DidNotReceive()
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_WhenCustomTimeoutIsLonger_DoesNotFailByDefaultTimeout()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-35);
        var job = CreateJob("col", requestedAtUtc: requestedAt, timeout: TimeSpan.FromSeconds(60));
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeTrue();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenGetSnapshotsThrows_FailsWithExceptionMessage()
    {
        var job = CreateJob("col");
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(Task.FromException<IReadOnlyList<SnapshotInfo>>(new InvalidOperationException("Node unavailable")));

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeFalse();
        errorMessage.Should().Be("Node unavailable");
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotCreatedBeforeCutoff_StillWaits()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333");
        var job = CreateJob("col", nodes, requestedAt);

        // Job treats CreatedAt >= requestedAt - 15s as "at request"; older rows are ignored → keep waiting.
        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(-20))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeTrue();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotsAppearWithEnoughNewCount_CompletesSuccessfully()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333", "http://node2:6333", "http://node3:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", requestedAt.AddSeconds(1), "col-peer1-1.snapshot", requestedAt.AddSeconds(1)),
            S3Snapshot("col", requestedAt.AddSeconds(2), "col-peer2-2.snapshot", requestedAt.AddSeconds(2)),
            S3Snapshot("col", requestedAt.AddSeconds(3), "col-peer3-3.snapshot", requestedAt.AddSeconds(3))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenS3SnapshotsButNotEnoughNew_ReturnsHasMore()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333", "http://node2:6333", "http://node3:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", requestedAt.AddSeconds(1), "col-peer1-1.snapshot"),
            S3Snapshot("col", requestedAt.AddSeconds(2), "col-peer2-2.snapshot")
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeTrue();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    [Test]
    public async Task AdvanceAsync_WhenS3UploadAfterRequest_CompletesEvenIfFilenameTimestampPredatesRequest()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333", "http://node2:6333", "http://node3:6333");
        var job = CreateJob("col", nodes, requestedAt);

        var oldNameTime = requestedAt.AddHours(-3);
        var uploadTime = requestedAt.AddSeconds(30);
        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", oldNameTime, "col-111-2026-03-18-10-00-00.snapshot", uploadTime),
            S3Snapshot("col", oldNameTime, "col-222-2026-03-18-10-00-00.snapshot", uploadTime),
            S3Snapshot("col", oldNameTime, "col-333-2026-03-18-10-00-00.snapshot", uploadTime)
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
    }

    [Test]
    public async Task AdvanceAsync_WhenBaselineContainsOld_IgnoresThem_CountsOnlyNewS3Snapshots()
    {
        var requestedAt = DateTime.UtcNow;
        var nodes = NodeList("http://n1", "http://n2", "http://n3");
        var baseline = new HashSet<string>(StringComparer.Ordinal)
        {
            PendingSnapshotCreationJob.BaselineKey(S3Snapshot("col", requestedAt.AddDays(-1), "col-old1.snapshot")),
            PendingSnapshotCreationJob.BaselineKey(S3Snapshot("col", requestedAt.AddDays(-1), "col-old2.snapshot")),
            PendingSnapshotCreationJob.BaselineKey(S3Snapshot("col", requestedAt.AddDays(-1), "col-old3.snapshot"))
        };
        var job = CreateJob("col", nodes, requestedAt, baseline);

        var uploadAfter = requestedAt.AddMinutes(2);
        var snapshots = new List<SnapshotInfo>
        {
            S3Snapshot("col", requestedAt.AddDays(-1), "col-old1.snapshot", uploadAfter.AddMinutes(-10)),
            S3Snapshot("col", requestedAt.AddDays(-1), "col-old2.snapshot", uploadAfter.AddMinutes(-10)),
            S3Snapshot("col", requestedAt.AddDays(-1), "col-old3.snapshot", uploadAfter.AddMinutes(-10)),
            S3Snapshot("col", requestedAt.AddHours(-5), "col-p1-new.snapshot", uploadAfter),
            S3Snapshot("col", requestedAt.AddHours(-5), "col-p2-new.snapshot", uploadAfter),
            S3Snapshot("col", requestedAt.AddHours(-5), "col-p3-new.snapshot", uploadAfter)
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
    }

    [Test]
    public async Task AdvanceAsync_WhenSnapshotsVisible_AppliesRetentionWhenConfigured()
    {
        var requestedAt = DateTime.UtcNow.AddSeconds(-10);
        var nodes = NodeList("http://node1:6333");
        var peerIds = new HashSet<string> { "p1" };
        var job = CreateJob(
            "col",
            nodes,
            requestedAt,
            baselineSnapshotKeys: null,
            retainLastNAfterVisible: 3,
            retentionClusterPeerIds: peerIds);

        var snapshots = new List<SnapshotInfo>
        {
            Snapshot("col", "http://node1:6333", requestedAt.AddSeconds(1))
        };
        _snapshotService
            .GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(snapshots);

        var (hasMore, success, errorMessage) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
        errorMessage.Should().BeNull();
        await _snapshotService.Received(1).EnforceRetentionAsync("col", 3, Arg.Any<CancellationToken>(), peerIds);
    }
}
