using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Configuration;
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

    #region Initial Status Tests

    [Test]
    public void TrackClusterStatusChange_InitialHealthyStatus_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange & Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_InitialDegradedStatus_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange & Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_InitialUnavailableStatus_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange & Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion

    #region Status Change to Degraded/Unavailable Tests

    [Test]
    public void TrackClusterStatusChange_HealthyToDegraded_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    [Test]
    public void TrackClusterStatusChange_HealthyToUnavailable_ShouldSetNeedsAttentionToTrue()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion

    #region Status Recovery Tests

    [Test]
    public void TrackClusterStatusChange_DegradedToHealthy_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableToHealthy_ShouldSetNeedsAttentionToFalse()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);

        // Assert
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    #endregion

    #region Status Changes Between Degraded/Unavailable Tests

    [Test]
    public void TrackClusterStatusChange_DegradedToUnavailable_ShouldNotUpdateNeedsAttention()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);

        // Assert - should not update metric, already needs attention
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    [Test]
    public void TrackClusterStatusChange_UnavailableToDegraded_ShouldNotUpdateNeedsAttention()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);

        // Assert - should not update metric, already needs attention
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    #endregion

    #region Same Status Tests

    [Test]
    public void TrackClusterStatusChange_SameStatus_ShouldNotUpdateMetric()
    {
        // Arrange
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.ClearReceivedCalls();

        // Act
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);

        // Assert
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());
    }

    #endregion

    #region Multiple Transitions Tests

    [Test]
    public void TrackClusterStatusChange_MultipleTransitions_ShouldTrackCorrectly()
    {
        // Initial: Healthy -> no attention needed
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded -> needs attention
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
        _meterService.ClearReceivedCalls();

        // Unavailable -> still needs attention (no new call)
        _monitorService.TrackClusterStatusChange(ClusterStatus.Unavailable);
        _meterService.DidNotReceive().UpdateClusterNeedsAttention(Arg.Any<bool>());

        // Healthy -> attention cleared
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
    }

    [Test]
    public void TrackClusterStatusChange_FlappingStatus_ShouldTrackEachTransition()
    {
        // Healthy
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
        _meterService.ClearReceivedCalls();

        // Healthy again
        _monitorService.TrackClusterStatusChange(ClusterStatus.Healthy);
        _meterService.Received(1).UpdateClusterNeedsAttention(false);
        _meterService.ClearReceivedCalls();

        // Degraded again
        _monitorService.TrackClusterStatusChange(ClusterStatus.Degraded);
        _meterService.Received(1).UpdateClusterNeedsAttention(true);
    }

    #endregion
}

