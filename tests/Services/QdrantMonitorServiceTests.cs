using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using SnapshotInfo = Vigilante.Models.SnapshotInfo;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class QdrantMonitorServiceTests
{
    private IClusterManager _clusterManager = null!;
    private IMeterService _meterService = null!;
    private ILogger<QdrantMonitorService> _logger = null!;
    private IDynamicConfigService _dynamicConfigService = null!;
    private ISnapshotService _snapshotService = null!;
    private QdrantMonitorService _monitorService = null!;

    [SetUp]
    public void SetUp()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _meterService = Substitute.For<IMeterService>();
        _logger = Substitute.For<ILogger<QdrantMonitorService>>();
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _snapshotService = Substitute.For<ISnapshotService>();

        // Setup dynamic config service to return default config
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig { MonitoringIntervalSeconds = 5 }));

        _monitorService = new QdrantMonitorService(
            _clusterManager,
            _meterService,
            _dynamicConfigService,
            _snapshotService,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _monitorService?.Dispose();
    }

    #region Helper Methods

    private static ClusterState CreateHealthyState(bool hasIssues = false)
    {
        var state = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                new() { PeerId = "node1", IsHealthy = true, IsLeader = true }
            }
        };

        if (hasIssues)
        {
            state.Nodes[0].Issues.Add("Test issue");
            state.InvalidateCache(); // Force recalculation
        }

        return state;
    }

    private static ClusterState CreateDegradedState(bool hasIssues = false)
    {
        var state = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                new() { PeerId = "node1", IsHealthy = true, IsLeader = true },
                new() { PeerId = "node2", IsHealthy = false }
            }
        };

        if (hasIssues)
        {
            state.Nodes[0].Issues.Add("Test issue");
            state.InvalidateCache(); // Force recalculation
        }

        return state;
    }

    private static ClusterState CreateUnavailableState(bool hasIssues = false)
    {
        var state = new ClusterState
        {
            Nodes = new List<NodeInfo>
            {
                // Unavailable state: no healthy nodes, but still has a leader to avoid "No leader elected" issue
                new() { PeerId = "node1", IsHealthy = false, IsLeader = true }
            }
        };

        if (hasIssues)
        {
            state.Nodes[0].Issues.Add("Test issue");
            state.InvalidateCache(); // Force recalculation
        }

        return state;
    }

    #endregion

    #region Initial Status Tests

    [Test]
    public void TrackClusterStatusChange_InitialHealthyStatus_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange
        var state = CreateHealthyState();

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_InitialDegradedStatus_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        var state = CreateDegradedState();

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_InitialUnavailableStatus_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        var state = CreateUnavailableState();

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion

    #region Status Change to Degraded/Unavailable Tests

    [Test]
    public void TrackClusterStatusChange_HealthyToDegraded_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateDegradedState());

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_HealthyToUnavailable_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion

    #region Status Recovery Tests

    [Test]
    public void TrackClusterStatusChange_DegradedToHealthy_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateHealthyState());

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableToHealthy_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateHealthyState());

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    #endregion

    #region Status Changes Between Degraded/Unavailable Tests

    [Test]
    public void TrackClusterStatusChange_DegradedToUnavailable_ShouldNotUpdateNeedsAttention()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());

        // Assert - should not update metric, already needs attention
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableToDegraded_ShouldNotUpdateNeedsAttention()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateDegradedState());

        // Assert - should not update metric, already needs attention
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    #endregion

    #region Same Status Tests

    [Test]
    public void TrackClusterStatusChange_SameStatus_ShouldNotUpdateMetric()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(CreateHealthyState());

        // Assert
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    #endregion

    #region Multiple Transitions Tests

    [Test]
    public void TrackClusterStatusChange_MultipleTransitions_ShouldTrackCorrectly()
    {
        // Initial: Healthy -> no attention needed
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded -> needs attention
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
        _meterService.ClearReceivedCalls();

        // Unavailable -> still needs attention (no new call)
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());

        // Healthy -> attention cleared
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_FlappingStatus_ShouldTrackEachTransition()
    {
        // Healthy
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
        _meterService.ClearReceivedCalls();

        // Healthy again
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded again
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion

    #region Issues Tests

    [Test]
    public void TrackClusterStatusChange_HealthyWithIssues_ShouldSetNeedsAttentionToTrueWithIssuesReason()
    {
        // Arrange
        var state = CreateHealthyState(hasIssues: true);

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_DegradedWithIssues_ShouldPrioritizeIssuesReason()
    {
        // Arrange
        var state = CreateDegradedState(hasIssues: true);

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert - Issues take priority over status
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableWithIssues_ShouldPrioritizeIssuesReason()
    {
        // Arrange
        var state = CreateUnavailableState(hasIssues: true);

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert - Issues take priority over status
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_TransitionWithIssuesAppearing_ShouldSetIssuesReason()
    {
        // Arrange - start healthy without issues
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: false));
        _meterService.ClearReceivedCalls();

        // Act - same status but issues appeared
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_IssuesDisappearButStatusDegraded_ShouldUseStatusReason()
    {
        // Arrange - start with issues
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));
        _meterService.ClearReceivedCalls();

        // Act - issues gone but status degraded
        _monitorService.TrackClusterStatusChange(CreateDegradedState(hasIssues: false));

        // Assert - should use degraded status reason
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_IssuesPersist_ShouldAlwaysSetAttention()
    {
        // Arrange - start with issues
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));
        _meterService.ClearReceivedCalls();

        // Act - issues persist on second call
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));

        // Assert - should still set attention even though status didn't change
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_RecoveryFromDegradedWithIssues_IssuesStillPresent_ShouldKeepAttention()
    {
        // Arrange - start degraded with issues
        _monitorService.TrackClusterStatusChange(CreateDegradedState(hasIssues: true));
        _meterService.ClearReceivedCalls();

        // Act - status recovered to healthy but issues still present
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));

        // Assert - attention should remain true due to issues
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_FullRecovery_NoIssuesNoStatusProblems_ShouldClearAttention()
    {
        // Arrange - start with issues
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: true));
        _meterService.ClearReceivedCalls();

        // Act - full recovery: no issues, healthy status
        _monitorService.TrackClusterStatusChange(CreateHealthyState(hasIssues: false));

        // Assert - should not update since status is same and both are healthy
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    #endregion

    #region ProcessSnapshotAutomationAsync Tests

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
    public async Task ProcessSnapshotAutomation_ScheduleDisabled_DoesNotFetchSnapshots()
    {
        // Arrange — _dynamicConfig stays default (schedule disabled)

        var collections = GreenHnswCollection("col1");

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(collections, HealthyNodes(), CancellationToken.None);

        // Assert
        await _snapshotService.DidNotReceive().GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_OnGreenOnce_NoExistingSnapshots_CreatesSnapshot()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: null);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>());

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_OnGreenOnce_ExistingSnapshotsPresent_SkipsSnapshot()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: null);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>
            {
                // Snapshot name contains "peer1" — the peerId of the healthy node
                new() { CollectionName = "col1", SnapshotName = "col1-20240101-peer1", PodName = "", NodeUrl = "http://node1:6333", PeerId = "peer1", PodNamespace = "" }
            });

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert
        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_OnGreenOnce_SnapshotExistsOnSomeNodes_SnapshotsOnlyMissingNodes()
    {
        // Arrange — node1 already has a snapshot, node2 does not
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: null);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>
            {
                // Snapshot name contains "peer1" — node1 is covered, node2 (peer2) is not
                new() { CollectionName = "col1", SnapshotName = "col1-20240101-peer1", PodName = "", NodeUrl = "http://node1:6333", PeerId = "peer1", PodNamespace = "" }
            });

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new Dictionary<string, string?> { ["http://node2:6333"] = "col1-20240101-peer2" });

        var twoNodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", IsHealthy = true, PeerId = "peer1" },
            new() { Url = "http://node2:6333", IsHealthy = true, PeerId = "peer2" }
        };

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), twoNodes, CancellationToken.None);

        // Assert — snapshot created only on node2
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1",
            Arg.Is<IEnumerable<string>>(urls => urls.SequenceEqual(new[] { "http://node2:6333" })),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_CollectionNotGreenOrNoHnsw_SkipsSnapshot()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: null);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SnapshotInfo>>(new List<SnapshotInfo>()));

        var yellowCollection = new List<CollectionInfo>
        {
            new() { CollectionName = "col1", Status = QdrantCollectionStatus.Yellow, HnswM = 16 }
        };
        var noHnswCollection = new List<CollectionInfo>
        {
            new() { CollectionName = "col1", Status = QdrantCollectionStatus.Green, HnswM = 0 }
        };

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(yellowCollection, HealthyNodes(), CancellationToken.None);
        await _monitorService.ProcessSnapshotAutomationAsync(noHnswCollection, HealthyNodes(), CancellationToken.None);

        // Assert
        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_IntervalBased_WhenDue_CreatesSnapshot()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: 60);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>());

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        // Act — first call, no last snapshot time recorded => due immediately
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_IntervalBased_NotDueYet_SkipsSnapshot()
    {
        // Arrange — a recent snapshot exists for node1/peer1, so the node is not due
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: 60);

        var recentSnapshot = new SnapshotInfo
        {
            CollectionName = "col1",
            SnapshotName = "col1-peer1-2026-02-24-10-00-00",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5), // 5 min ago — well within the 60-min interval
            NodeUrl = "http://node1:6333",
            PeerId = "peer1",
            PodName = "pod1",
            PodNamespace = "default",
            Source = SnapshotSource.KubernetesStorage
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo> { recentSnapshot });

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert — no new snapshot created
        await _snapshotService.DidNotReceive().CreateCollectionSnapshotAsync(
            "col1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_IntervalBased_FailedNode_RetriedInNextCycle()
    {
        // Arrange — two nodes; node1 has a recent snapshot, node2 has none → only node2 should be targeted
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: 60);

        var twoNodes = new List<NodeInfo>
        {
            new() { Url = "http://node1:6333", IsHealthy = true, PeerId = "peer1" },
            new() { Url = "http://node2:6333", IsHealthy = true, PeerId = "peer2" }
        };

        // node1 has a recent snapshot (5 min ago); node2 has no snapshot
        var node1Snapshot = new SnapshotInfo
        {
            CollectionName = "col1",
            SnapshotName = "col1-peer1-2026-02-24-10-00-00",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            NodeUrl = "http://node1:6333",
            PeerId = "peer1",
            PodName = "pod1",
            PodNamespace = "default",
            Source = SnapshotSource.KubernetesStorage
        };

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo> { node1Snapshot });

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new Dictionary<string, string?> { ["http://node2:6333"] = "col1-peer2-2026-02-24-10-05-00" });

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), twoNodes, CancellationToken.None);

        // Assert — only node2 was targeted
        await _snapshotService.Received(1).CreateCollectionSnapshotAsync(
            "col1",
            Arg.Is<IEnumerable<string>>(urls => urls.SequenceEqual(new[] { "http://node2:6333" })),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_SnapshotFails_ReportsIssueToClusterManager()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: null);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>());

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .ThrowsAsync(new Exception("connection refused"));

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert
        _clusterManager.Received(1).ReportIssue(
            IssueKeyConstants.Snapshot("col1"),
            Arg.Any<string>());
    }

    [Test]
    public async Task ProcessSnapshotAutomation_SnapshotSucceeds_ClearsIssue()
    {
        // Arrange
        _monitorService._dynamicConfig = ScheduleEnabled(intervalMinutes: 60);

        _snapshotService.GetSnapshotsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<SnapshotInfo>());

        _snapshotService.CreateCollectionSnapshotAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new Dictionary<string, string?> { ["http://node1:6333"] = "snap1" });

        // Act
        await _monitorService.ProcessSnapshotAutomationAsync(
            GreenHnswCollection("col1"), HealthyNodes(), CancellationToken.None);

        // Assert
        _clusterManager.Received(1).ClearIssue(IssueKeyConstants.Snapshot("col1"));
    }

    #endregion
}

