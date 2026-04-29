using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class QdrantMonitorServiceTests
{
    private IClusterManager _clusterManager = null!;
    private IJobRegistry _jobRegistry = null!;
    private IMeterService _meterService = null!;
    private ILogger<QdrantMonitorService> _logger = null!;
    private IDynamicConfigService _dynamicConfigService = null!;
    private IServiceProvider _serviceProvider = null!;
    private QdrantMonitorService _monitorService = null!;

    [SetUp]
    public void SetUp()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _jobRegistry = Substitute.For<IJobRegistry>();
        _meterService = Substitute.For<IMeterService>();
        _logger = Substitute.For<ILogger<QdrantMonitorService>>();
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        var snapshotService = Substitute.For<ISnapshotService>();
        var snapshotOrphanedState = new SnapshotOrphanedState();
        var snapshotJobLogger = Substitute.For<ILogger<SnapshotAutomationJob>>();
        _serviceProvider = new ServiceCollection()
            .AddSingleton(_clusterManager)
            .AddSingleton(_jobRegistry)
            .AddSingleton(snapshotService)
            .AddSingleton(snapshotOrphanedState)
            .AddSingleton(snapshotJobLogger)
            .AddSingleton<ISnapshotAutomationStatus, SnapshotAutomationStatus>()
            .BuildServiceProvider();

        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig { MonitoringIntervalSeconds = 5 }));

        _jobRegistry.ProcessPendingJobsAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _monitorService = new QdrantMonitorService(
            _clusterManager,
            _jobRegistry,
            _meterService,
            _dynamicConfigService,
            _serviceProvider,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _monitorService?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
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
    public void TrackClusterStatusChange_HealthyWithDiskUsageIssue_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        var state = CreateHealthyState();
        state.Nodes[0].Issues.Add("Disk usage is 94.44% (threshold: 80.00%)");
        state.InvalidateCache();

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

    #region Job execution (healthy cluster)

    [Test]
    public async Task ExecuteAsync_WhenClusterHealthy_AddsSnapshotJobAndProcesses()
    {
        var healthyState = CreateHealthyState();
        healthyState.Nodes[0].Url = "http://node1:6333";
        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(healthyState);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CollectionInfo>());
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig { MonitoringIntervalSeconds = 60 }));

        using var cts = new CancellationTokenSource();
        _ = _monitorService.StartAsync(cts.Token);

        await Task.Delay(2500, CancellationToken.None);
        cts.Cancel();
        await Task.Delay(500, CancellationToken.None);

        _jobRegistry.Received(1).TryAddJob(Arg.Is<SnapshotAutomationJob>(j => j.Key == SnapshotAutomationJob.JobKey), Arg.Any<CancellationTokenSource>());
    }

    [Test]
    public async Task ExecuteAsync_WhenSnapshotJobFails_RecordsJobFailure()
    {
        var healthyState = CreateHealthyState();
        healthyState.Nodes[0].Url = "http://node1:6333";
        _clusterManager.GetClusterStateAsync(Arg.Any<CancellationToken>())
            .Returns(healthyState);
        _clusterManager.GetCollectionsInfoAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("collections failed"));

        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DynamicConfig
            {
                MonitoringIntervalSeconds = 60,
                Snapshot = new SnapshotConfiguration { Schedule = new Schedule { Enabled = true } }
            }));

        var jobRegistryLogger = Substitute.For<ILogger<JobRegistry>>();
        var realJobRegistry = new JobRegistry(jobRegistryLogger);
        var monitor = new QdrantMonitorService(
            _clusterManager,
            realJobRegistry,
            _meterService,
            _dynamicConfigService,
            _serviceProvider,
            _logger);

        using var cts = new CancellationTokenSource();
        _ = monitor.StartAsync(cts.Token);

        await Task.Delay(3000, CancellationToken.None);
        cts.Cancel();
        await Task.Delay(500, CancellationToken.None);

        var errors = realJobRegistry.GetActiveErrorsAndPruneExpired(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Key, Is.EqualTo("snapshot-automation"));
        Assert.That(errors[0].Message, Is.EqualTo("collections failed"));
    }

    #endregion
}

