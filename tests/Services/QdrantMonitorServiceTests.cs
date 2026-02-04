using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
using Vigilante.Models;
using Vigilante.Models.Enums;
using Vigilante.Services;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class QdrantMonitorServiceTests
{
    private IClusterManager _clusterManager = null!;
    private IMeterService _meterService = null!;
    private ILogger<QdrantMonitorService> _logger = null!;
    private IOptions<QdrantOptions> _options = null!;
    private QdrantMonitorService _monitorService = null!;

    [SetUp]
    public void SetUp()
    {
        _clusterManager = Substitute.For<IClusterManager>();
        _meterService = Substitute.For<IMeterService>();
        _logger = Substitute.For<ILogger<QdrantMonitorService>>();
        
        _options = Options.Create(new QdrantOptions
        {
            MonitoringIntervalSeconds = 5,
            EnableAutoRecovery = false
        });

        _monitorService = new QdrantMonitorService(
            _clusterManager,
            _meterService,
            _options,
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
    }

    [Test]
    public void TrackClusterStatusChange_InitialUnavailableStatus_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        var state = CreateUnavailableState();

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusUnavailable);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusUnavailable);
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
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>(), Arg.Any<ClusterAttentionReason>());
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
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>(), Arg.Any<ClusterAttentionReason>());
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
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>(), Arg.Any<ClusterAttentionReason>());
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
        _meterService.ClearReceivedCalls();

        // Unavailable -> still needs attention (no new call)
        _monitorService.TrackClusterStatusChange(CreateUnavailableState());
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>(), Arg.Any<ClusterAttentionReason>());

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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
        _meterService.ClearReceivedCalls();

        // Healthy again
        _monitorService.TrackClusterStatusChange(CreateHealthyState());
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded again
        _monitorService.TrackClusterStatusChange(CreateDegradedState());
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
    }

    [Test]
    public void TrackClusterStatusChange_DegradedWithIssues_ShouldPrioritizeIssuesReason()
    {
        // Arrange
        var state = CreateDegradedState(hasIssues: true);

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert - Issues take priority over status
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableWithIssues_ShouldPrioritizeIssuesReason()
    {
        // Arrange
        var state = CreateUnavailableState(hasIssues: true);

        // Act
        _monitorService.TrackClusterStatusChange(state);

        // Assert - Issues take priority over status
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.ClusterStatusDegraded);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
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
        _meterService.Received(1).UpdateClusterNeedsAttention(true, ClusterAttentionReason.HasActiveIssues);
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
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>(), Arg.Any<ClusterAttentionReason>());
    }

    #endregion
}

