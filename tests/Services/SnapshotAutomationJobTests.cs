using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services.Interfaces;
using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Services.Snapshots;
using Vigilante.Models.Snapshots;
using Vigilante.Services.Jobs.Snapshots;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class SnapshotAutomationJobTests
{
    private ISnapshotService _snapshotService = null!;
    private IS3SnapshotService _s3SnapshotService = null!;
    private IJobRegistry _jobRegistry = null!;
    private IClusterManager _clusterManager = null!;
    private ICollectionService _collectionService = null!;
    private IDynamicConfigService _dynamicConfigService = null!;
    private SnapshotOrphanedState _orphanedState = null!;
    private ILogger<SnapshotAutomationJob> _logger = null!;
    private IServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _snapshotService = Substitute.For<ISnapshotService>();
        _s3SnapshotService = Substitute.For<IS3SnapshotService>();
        _jobRegistry = Substitute.For<IJobRegistry>();
        _clusterManager = Substitute.For<IClusterManager>();
        _collectionService = Substitute.For<ICollectionService>();
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _orphanedState = new SnapshotOrphanedState();
        _logger = Substitute.For<ILogger<SnapshotAutomationJob>>();
        _collectionService
            .GetCollectionsFromQdrantAsync(
                Arg.Any<IEnumerable<(string Url, ulong PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(Task.FromResult((new List<CollectionInfo>(), false, (string?)null)));
        _jobRegistry.GetPendingJobs().Returns([]);
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_s3SnapshotService)
            .AddSingleton(_jobRegistry)
            .AddSingleton(_clusterManager)
            .AddSingleton(_collectionService)
            .AddSingleton(_dynamicConfigService)
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
        [
            new()
            {
                CollectionName = name,
                NodeUrl = nodeUrl,
                Status = QdrantCollectionStatus.Green,
                HnswM = 16
            }
        ];

    private static IReadOnlyList<CollectionInfo> GreenHnswCollectionWithShards(
        string name,
        List<ShardDetails> shards,
        string nodeUrl = "http://node1:6333") =>
        [
            new()
            {
                CollectionName = name,
                NodeUrl = nodeUrl,
                Status = QdrantCollectionStatus.Green,
                HnswM = 16,
                Metrics = new CollectionMetrics { Shards = shards }
            }
        ];

    private static IReadOnlyList<NodeInfo> HealthyNodes(string url = "http://node1:6333", ulong peerId = 1) =>
        [new() { Url = url, IsHealthy = true, PeerId = peerId }];

    private static DynamicConfig ScheduleEnabled(int? intervalMinutes = null, int? retainLastN = null, DateTimeOffset? startAt = null) =>
        new()
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule
                {
                    Enabled = true,
                    IntervalMinutes = intervalMinutes,
                    StartAt = startAt,
                    RetainLastN = retainLastN
                }
            }
        };

    [Test]
    public async Task AdvanceAsync_ScheduleDisabled_FetchesSnapshotsForMultipartCleanup()
    {
        var config = new DynamicConfig(); // Schedule.Enabled = false
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _s3SnapshotService
            .CleanupIncompleteMultipartUploadsAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((Found: 0, Aborted: 0, Failed: 0)));

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>());
    }

    [Test]
    public async Task AdvanceAsync_MultipartCleanupEnabled_CleansStaleMultipartUploads()
    {
        var config = new DynamicConfig
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule { Enabled = true }
            }
        };
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _s3SnapshotService
            .CleanupIncompleteMultipartUploadsAsync(1440, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((Found: 2, Aborted: 2, Failed: 0)));

        var job = CreateJob(config: config);
        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
        await _s3SnapshotService.Received(1)
            .CleanupIncompleteMultipartUploadsAsync(1440, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdvanceAsync_OnGreenOnce_NoExistingSnapshots_CreatesSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var job = CreateJob(config: config);

        var (hasMore, success, _) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue();
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_WhenOverridesExist_OverrideAppliedAndOthersUseGlobalSchedule()
    {
        var config = new DynamicConfig
        {
            Snapshot = new SnapshotConfiguration
            {
                Schedule = new Schedule { Enabled = true, IntervalMinutes = null },
                CollectionOverrides = new Dictionary<string, Schedule>(StringComparer.Ordinal)
                {
                    ["col1"] = new() { Enabled = true, IntervalMinutes = null }
                }
            }
        };

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new() { CollectionName = "col1", NodeUrl = "http://node1:6333", Status = QdrantCollectionStatus.Green, HnswM = 16 },
                new() { CollectionName = "col2", NodeUrl = "http://node1:6333", Status = QdrantCollectionStatus.Green, HnswM = 16 }
            ]);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<ulong>>());
        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            "col2", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_OnGreenOnce_ExistingSnapshotsPresent_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns(
            [
                new() { CollectionName = "col1", SnapshotName = "col1-20240101-peer1", PodName = "", NodeUrl = "http://node1:6333", PeerId = 1, PodNamespace = "", Source = SnapshotSource.KubernetesStorage }
            ]);

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_CollectionNotGreenOrNoHnsw_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

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
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_GreenCollectionWithRunningOptimizations_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var optimizingCollection = new List<CollectionInfo>
        {
            new()
            {
                CollectionName = "col1",
                Status = QdrantCollectionStatus.Green,
                HnswM = 16,
                RunningOptimizations =
                [
                    new() { Optimizer = "merge", SegmentsCount = 4, Done = 1, Total = 10 }
                ]
            }
        };
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(optimizingCollection);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_GreenCollectionWithEmptyShard_SkipsSnapshot_OnGreenOnce()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollectionWithShards("col1",
            [
                new() { ShardId = 0, State = ShardState.Active.ToString(), IsEmpty = false },
                new() { ShardId = 1, State = ShardState.Active.ToString(), IsEmpty = true }
            ]));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_GreenCollectionWithEmptyShard_SkipsSnapshot_IntervalBased()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollectionWithShards("col1",
            [
                new() { ShardId = 0, State = ShardState.Active.ToString(), IsEmpty = true }
            ]));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_GreenCollectionWithInactiveShard_SkipsSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollectionWithShards("col1",
            [
                new() { ShardId = 0, State = ShardState.Partial.ToString(), IsEmpty = false }
            ]));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_GreenCollectionWithAllShardsActiveAndNonEmpty_CreatesSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollectionWithShards("col1",
            [
                new() { ShardId = 0, State = ShardState.Active.ToString(), IsEmpty = false },
                new() { ShardId = 1, State = ShardState.Active.ToString(), IsEmpty = false }
            ]));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_IntervalBased_WhenDue_CreatesSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var job = CreateJob(config: config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotFails_ReportsIssueToClusterManager()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .ThrowsAsync(new Exception("connection refused"));

        var job = CreateJob(config: config);

        var (hasMore, success, error) = await job.AdvanceAsync(CancellationToken.None);

        hasMore.Should().BeFalse();
        success.Should().BeTrue(); // job reports success; ReportIssue is side effect
        error.Should().BeNullOrEmpty();

        _clusterManager.Received(1).ReportIssue(IssueKeyConstants.Snapshot("col1"), Arg.Any<string>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotSucceeds_ClearsIssue()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

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
                Arg.Any<IEnumerable<(string Url, ulong PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(Task.FromResult((
                new List<CollectionInfo>
                {
                    new() { CollectionName = "live_col", NodeUrl = "http://node1:6333", PeerId = 1 }
                },
                true,
                (string?)null)));

        await using var sp = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_jobRegistry)
            .AddSingleton(_clusterManager)
            .AddSingleton(_orphanedState)
            .AddSingleton(_logger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .AddSingleton(collectionService)
            .AddSingleton(dynamicConfig)
            .BuildServiceProvider();

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("live_col"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap" },
                SkippedDuplicatePending = false
            }));

        var job = new SnapshotAutomationJob(sp, HealthyNodes(), cfg);

        await job.AdvanceAsync(CancellationToken.None);

        cfg.Snapshot.CollectionOverrides.Should().NotBeNull();
        cfg.Snapshot.CollectionOverrides.ContainsKey("live_col").Should().BeTrue();
        cfg.Snapshot.CollectionOverrides.ContainsKey("deleted_col").Should().BeFalse();
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
                Arg.Any<IEnumerable<(string Url, ulong PeerId, string? Namespace, string? PodName)>>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(Task.FromResult((new List<CollectionInfo>(), false, (string?)null)));

        await using var sp = new ServiceCollection()
            .AddSingleton(_snapshotService)
            .AddSingleton(_jobRegistry)
            .AddSingleton(_clusterManager)
            .AddSingleton(_orphanedState)
            .AddSingleton(_logger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .AddSingleton(collectionService)
            .AddSingleton(dynamicConfig)
            .BuildServiceProvider();

        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);

        var job = new SnapshotAutomationJob(sp, HealthyNodes(), cfg);

        await job.AdvanceAsync(CancellationToken.None);

        cfg.Snapshot.CollectionOverrides!.ContainsKey("deleted_col").Should().BeTrue();
        await dynamicConfig.DidNotReceive()
            .UpdateConfigAsync(Arg.Any<DynamicConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Key_ReturnsSnapshotAutomationJobKey()
    {
        var job = CreateJob();
        job.Key.Should().Be(SnapshotAutomationJob.JobKey);
    }

    [Test]
    public void IsWaitingForReady_IsFalse()
    {
        var job = CreateJob();
        job.IsWaitingForReady.Should().BeFalse();
    }

    [Test]
    public async Task AdvanceAsync_WhenRetainLastNSet_PassesRetentionToCreateForDeferredEnforcement()
    {
        var config = ScheduleEnabled(intervalMinutes: 60, retainLastN: 1);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var nodes = HealthyNodes("http://node1:6333", 111);
        var job = CreateJob(nodes, config);

        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>(),
            false,
            1,
            Arg.Is<IReadOnlySet<ulong>>(set => set != null && set.Contains(111)));
        await _snapshotService.DidNotReceive()
            .EnforceRetentionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_WhenCreateSkipped_DoesNotReportSnapshotFailure()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(CreateCollectionSnapshotBatchResult.SkippedForNodes(["http://node1:6333"])));

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        _clusterManager.DidNotReceive().ReportIssue(IssueKeyConstants.Snapshot("col1"), Arg.Any<string>());
    }

    [Test]
    public async Task AdvanceAsync_MultiSnapshotRecoveryInProgress_SkipsAutoSnapshot_OnGreenOnce()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _jobRegistry.HasPendingMultiSnapshotRecoveryForCollection("col1").Returns(true);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_MultiSnapshotRecoveryInProgress_SkipsAutoSnapshot_IntervalBased()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _jobRegistry.HasPendingMultiSnapshotRecoveryForCollection("col1").Returns(true);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotRecoveryInProgress_SkipsAutoSnapshot_OnGreenOnce()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _jobRegistry.HasPendingSnapshotRecoveryForCollection("col1").Returns(true);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_SnapshotRecoveryInProgress_SkipsAutoSnapshot_IntervalBased()
    {
        var config = ScheduleEnabled(intervalMinutes: 60);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _jobRegistry.HasPendingSnapshotRecoveryForCollection("col1").Returns(true);

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
            Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public async Task AdvanceAsync_MultiSnapshotRecoveryForDifferentCollection_DoesNotBlockAutoSnapshot()
    {
        var config = ScheduleEnabled(intervalMinutes: null);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(GreenHnswCollection("col1"));
        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<CancellationToken>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<NodeInfo>?>())
            .Returns([]);
        _jobRegistry.HasPendingMultiSnapshotRecoveryForCollection("col1").Returns(false);
        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>(),
                Arg.Any<int?>(), Arg.Any<IReadOnlySet<ulong>>())
            .Returns(Task.FromResult(new CreateCollectionSnapshotBatchResult
            {
                Results = new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" },
                SkippedDuplicatePending = false
            }));

        var job = CreateJob(config: config);
        await job.AdvanceAsync(CancellationToken.None);

        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), false, null, Arg.Any<IReadOnlySet<ulong>>());
    }

    [Test]
    public void IsIntervalSnapshotDue_WithoutStartAt_UsesLegacyBehavior()
    {
        var now = new DateTime(2026, 3, 23, 12, 0, 0, DateTimeKind.Utc);
        var lastCreatedAt = now.AddMinutes(-59);

        var due = SnapshotAutomationJob.IsIntervalSnapshotDue(
            now,
            lastCreatedAt,
            intervalMinutes: 60,
            startAt: null);

        due.Should().BeFalse();
    }

    [Test]
    public void IsIntervalSnapshotDue_WithStartAt_DueWhenCurrentWindowHasNoSnapshot()
    {
        var now = new DateTime(2026, 3, 23, 15, 0, 0, DateTimeKind.Utc);
        var lastCreatedAt = new DateTime(2026, 3, 22, 23, 50, 0, DateTimeKind.Utc);

        var due = SnapshotAutomationJob.IsIntervalSnapshotDue(
            now,
            lastCreatedAt,
            intervalMinutes: 1440,
            startAt: new DateTimeOffset(2026, 3, 23, 0, 0, 0, TimeSpan.Zero));

        due.Should().BeTrue();
    }

    [Test]
    public void IsIntervalSnapshotDue_WithStartAt_NotDueWhenSnapshotAlreadyInCurrentWindow()
    {
        var now = new DateTime(2026, 3, 23, 15, 0, 0, DateTimeKind.Utc);
        var lastCreatedAt = new DateTime(2026, 3, 23, 1, 0, 0, DateTimeKind.Utc);

        var due = SnapshotAutomationJob.IsIntervalSnapshotDue(
            now,
            lastCreatedAt,
            intervalMinutes: 1440,
            startAt: new DateTimeOffset(2026, 3, 23, 0, 0, 0, TimeSpan.Zero));

        due.Should().BeFalse();
    }
}
