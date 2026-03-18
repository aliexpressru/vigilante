using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;
using Aer.QdrantClient.Http.Models.Shared;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class SnapshotAutomationJobTests
{
    private ISnapshotService _snapshotService = null!;
    private IClusterManager _clusterManager = null!;
    private SnapshotOrphanedState _orphanedState = null!;
    private ILogger<SnapshotAutomationJob> _logger = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _snapshotService = Substitute.For<ISnapshotService>();
        _clusterManager = Substitute.For<IClusterManager>();
        _orphanedState = new SnapshotOrphanedState();
        _logger = Substitute.For<ILogger<SnapshotAutomationJob>>();
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_clusterManager)
            .AddSingleton(_orphanedState)
            .AddSingleton(_logger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    private SnapshotAutomationJob CreateJob(IReadOnlyList<NodeInfo>? nodes = null, DynamicConfig? config = null) =>
        new(_serviceProvider, nodes ?? HealthyNodes(), config ?? new DynamicConfig());

    private static IReadOnlyList<CollectionInfo> GreenHnswCollection(string name, string nodeUrl = "http://node1:6333") =>
        new List<CollectionInfo>
        {
            new()
            {
                CollectionName = name,
                NodeUrl = nodeUrl,
                Status = QdrantCollectionStatus.Green,
                HnswM = 16
            }
        };

    private static IReadOnlyList<NodeInfo> HealthyNodes(string url = "http://node1:6333", string peerId = "peer1") =>
        new List<NodeInfo> { new() { Url = url, IsHealthy = true, PeerId = peerId } };

    private static DynamicConfig ScheduleEnabled(int? intervalMinutes = null, int? retainLastN = null) =>
        new()
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule
                {
                    Enabled = true,
                    IntervalMinutes = intervalMinutes,
                    RetainLastN = retainLastN
                }
            }
        };

    [Test]
    public async Task AdvanceAsync_ScheduleDisabled_DoesNotFetchSnapshots()
    {
        var config = new DynamicConfig(); // Schedule.Enabled = false
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_OnGreenOnce_NoExistingSnapshots_CreatesSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        var job = CreateJob(config: config);

        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True);
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<string>>());
    }

    [Test]
    public async Task AdvanceAsync_OnGreenOnce_ExistingSnapshotsPresent_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>
            {
                new() { CollectionName = "col1", SnapshotName = "col1-20240101-peer1", PodName = "", NodeUrl = "http://node1:6333", PeerId = "peer1", PodNamespace = "", Source = SnapshotSource.KubernetesStorage }
            });

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>());
    }

    [Test]
    public async Task AdvanceAsync_CollectionNotGreenOrNoHnsw_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());

        var yellowCollection = new List<CollectionInfo> { new() { CollectionName = "col1", Status = QdrantCollectionStatus.Yellow, HnswM = 16 } };
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(yellowCollection);
        var job1 = CreateJob(config: config);
        await job1.AdvanceAsync(CancellationToken.None);

        var noHnswCollection = new List<CollectionInfo> { new() { CollectionName = "col1", Status = QdrantCollectionStatus.Green, HnswM = 0 } };
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(noHnswCollection);
        var job2 = CreateJob(config: config);
        await job2.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>());
    }

    [Test]
    public async Task AdvanceAsync_IntervalBased_WhenDue_CreatesSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<string>>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotFails_ReportsIssueToClusterManager()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .ThrowsAsync(new Exception("connection refused"));

        var job = CreateJob(config: config);

        var (hasMore, success, error) = await job.AdvanceAsync(CancellationToken.None);

        Assert.That(hasMore, Is.False);
        Assert.That(success, Is.True); // job reports success; ReportIssue is side effect
        _clusterManager.Received(1).ReportIssue(IssueKeyConstants.Snapshot("col1"), Arg.Any<string>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotSucceeds_ClearsIssue()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        _clusterManager.Received(1).ClearIssue(IssueKeyConstants.Snapshot("col1"));
    }

    [Test]
    public async Task AdvanceAsync_RemovesSnapshotOverridesForCollectionsNotInCluster()
    {
        var cfg = new DynamicConfig
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule { Enabled = false },
                CollectionOverrides = new Dictionary<string, Schedule>(StringComparer.Ordinal)
                {
                    ["deleted_col"] = new Schedule { Enabled = true },
                    ["live_col"] = new Schedule { Enabled = true }
                }
            }
        };

        var dynamicConfig = Substitute.For<IDynamicConfigService>();
        dynamicConfig.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(cfg));
        dynamicConfig.UpdateConfigAsync(Arg.Any<DynamicConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(Task.FromResult((
                new List<CollectionInfo>
                {
                    new() { CollectionName = "live_col", NodeUrl = "http://node1:6333", PeerId = "p1" }
                },
                true,
                (string?)null)));

        await using var sp = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_clusterManager)
            .AddSingleton(_orphanedState)
            .AddSingleton(_logger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .AddSingleton(collectionService)
            .AddSingleton(dynamicConfig)
            .BuildServiceProvider();

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("live_col"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap" });

        var job = new SnapshotAutomationJob(sp, HealthyNodes(), cfg);

        await job.AdvanceAsync(CancellationToken.None);

        Assert.That(cfg.Snapshot.CollectionOverrides, Is.Not.Null);
        Assert.That(cfg.Snapshot.CollectionOverrides.ContainsKey("live_col"), Is.True);
        Assert.That(cfg.Snapshot.CollectionOverrides.ContainsKey("deleted_col"), Is.False);
        await dynamicConfig.Received(1).UpdateConfigAsync(cfg, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_DoesNotPruneOverridesWhenQdrantListingUnhealthy()
    {
        var cfg = new DynamicConfig
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule { Enabled = false },
                CollectionOverrides = new Dictionary<string, Schedule>(StringComparer.Ordinal)
                {
                    ["deleted_col"] = new Schedule { Enabled = true }
                }
            }
        };

        var dynamicConfig = Substitute.For<IDynamicConfigService>();
        dynamicConfig.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(cfg));

        var collectionService = Substitute.For<ICollectionService>();
        collectionService
            .GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, string PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(Task.FromResult((new List<CollectionInfo>(), false, (string?)null)));

        await using var sp = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_clusterManager)
            .AddSingleton(_orphanedState)
            .AddSingleton(_logger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .AddSingleton(collectionService)
            .AddSingleton(dynamicConfig)
            .BuildServiceProvider();

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CollectionInfo>());
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());

        var job = new SnapshotAutomationJob(sp, HealthyNodes(), cfg);

        await job.AdvanceAsync(CancellationToken.None);

        Assert.That(cfg.Snapshot.CollectionOverrides!.ContainsKey("deleted_col"), Is.True);
        await dynamicConfig.DidNotReceive()
            .UpdateConfigAsync(Arg.Any<DynamicConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Key_ReturnsSnapshotAutomationJobKey()
    {
        var job = CreateJob();
        Assert.That(job.Key, Is.EqualTo(SnapshotAutomationJob.JobKey));
    }

    [Test]
    public void IsWaitingForReady_IsFalse()
    {
        var job = CreateJob();
        Assert.That(job.IsWaitingForReady, Is.False);
    }

    [Test]
    public async Task AdvanceAsync_WhenRetainLastNSet_PassesRetentionToCreateForDeferredEnforcement()
    {
        var config = ScheduleEnabled(intervalMinutes: 60, retainLastN: 1);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(new List<SnapshotInfo>());
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<string>>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        var nodes = HealthyNodes("http://node1:6333", "111");
        var job = CreateJob(nodes, config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>(),
            false,
            1,
            Arg.Is<IReadOnlySet<string>>(set => set != null && set.Contains("111")));
        await _snapshotService.DidNotReceive()
            .EnforceRetentionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
    }
}
