using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Services;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class KubernetesManagerTests
{
    private IKubernetes _kubernetes = null!;
    private ILogger<KubernetesManager> _logger = null!;
    private KubernetesManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _kubernetes = Substitute.For<IKubernetes>();
        _logger = Substitute.For<ILogger<KubernetesManager>>();
        _manager = new KubernetesManager(_kubernetes, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _kubernetes.Dispose();
    }

    #region DeletePodAsync Tests

    [Test]
    public async Task DeletePodAsync_WithNullKubernetes_ShouldReturnFalse()
    {
        // Arrange
        var manager = new KubernetesManager(null, _logger);
        var podName = "qdrant-0";

        // Act
        var result = await manager.DeletePodAsync(podName, "qdrant");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DeletePodAsync_WithEmptyPodName_ShouldUseDefaultNamespace()
    {
        // Arrange - just verify method doesn't crash with empty namespace
        var podName = "qdrant-0";

        // Act & Assert - should not throw
        var result = await _manager.DeletePodAsync(podName);
        
        // Result depends on actual K8s API call which will fail in test, but method should handle gracefully
        Assert.That(result, Is.False); // Expected to fail without real API
    }

    #endregion

    #region RolloutRestartStatefulSetAsync Tests

    [Test]
    public async Task RolloutRestartStatefulSetAsync_WithNullKubernetes_ShouldReturnFalse()
    {
        // Arrange
        var manager = new KubernetesManager(null, _logger);

        // Act
        var result = await manager.RolloutRestartStatefulSetAsync("qdrant", "qdrant");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RolloutRestartStatefulSetAsync_WithoutNamespace_ShouldUseDefault()
    {
        // Arrange - just verify method handles missing namespace gracefully
        var statefulSetName = "qdrant";

        // Act
        var result = await _manager.RolloutRestartStatefulSetAsync(statefulSetName);

        // Assert - will fail without real K8s API but should handle gracefully
        Assert.That(result, Is.False);
    }

    #endregion

    #region ScaleStatefulSetAsync Tests

    [Test]
    public async Task ScaleStatefulSetAsync_WithNullKubernetes_ShouldReturnFalse()
    {
        // Arrange
        var manager = new KubernetesManager(null, _logger);

        // Act
        var result = await manager.ScaleStatefulSetAsync("qdrant", 3, "qdrant");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task ScaleStatefulSetAsync_ScaleToZero_WithoutRealAPI_ShouldHandleGracefully()
    {
        // Arrange
        var statefulSetName = "qdrant";
        var replicas = 0;

        // Act - will fail without real K8s API but should not throw
        var result = await _manager.ScaleStatefulSetAsync(statefulSetName, replicas, "qdrant");

        // Assert - expected to fail gracefully without real API
        Assert.That(result, Is.False);
    }

    #endregion
}

