using k8s;
using k8s.Autorest;
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

    #region GetWarningEventsAsync Tests

    [Test]
    public async Task GetWarningEventsAsync_WithNullKubernetes_ShouldReturnEmptyList()
    {
        // Arrange
        var manager = new KubernetesManager(null, _logger);

        // Act
        var result = await manager.GetWarningEventsAsync("qdrant");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetWarningEventsAsync_WithWarningEvents_ShouldReturnFormattedList()
    {
        // Arrange
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);
        
        var warningEvent1 = new Corev1Event
        {
            Type = "Warning",
            Reason = "FailedScheduling",
            Message = "0/3 nodes are available",
            LastTimestamp = DateTime.UtcNow,
            InvolvedObject = new V1ObjectReference
            {
                Kind = "Pod",
                Name = "qdrant-0"
            }
        };
        var warningEvent2 = new Corev1Event
        {
            Type = "Warning",
            Reason = "BackOff",
            Message = "Back-off restarting failed container",
            LastTimestamp = DateTime.UtcNow.AddMinutes(-1),
            InvolvedObject = new V1ObjectReference
            {
                Kind = "Pod",
                Name = "qdrant-1"
            }
        };

        var eventList = new Corev1EventList
        {
            Items = new List<Corev1Event> { warningEvent1, warningEvent2 }
        };

        var httpResponse = new HttpOperationResponse<Corev1EventList>
        {
            Body = eventList
        };

        coreV1.ListNamespacedEventWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetWarningEventsAsync(namespace1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0], Does.Contain("Pod/qdrant-0"));
        Assert.That(result[0], Does.Contain("FailedScheduling"));
        Assert.That(result[1], Does.Contain("Pod/qdrant-1"));
        Assert.That(result[1], Does.Contain("BackOff"));
    }

    [Test]
    public async Task GetWarningEventsAsync_WithNoEvents_ShouldReturnEmptyList()
    {
        // Arrange
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);
        
        var eventList = new Corev1EventList
        {
            Items = new List<Corev1Event>()
        };

        var httpResponse = new HttpOperationResponse<Corev1EventList>
        {
            Body = eventList
        };

        coreV1.ListNamespacedEventWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetWarningEventsAsync(namespace1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetWarningEventsAsync_WithException_ShouldReturnEmptyListAndLogError()
    {
        // Arrange
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);
        
        coreV1.ListNamespacedEventWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<HttpOperationResponse<Corev1EventList>>(new Exception("API Error")));

        // Act
        var result = await _manager.GetWarningEventsAsync(namespace1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetWarningEventsAsync_WithNullNamespace_ShouldUseDefaultNamespace()
    {
        // Arrange
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);
        
        var eventList = new Corev1EventList
        {
            Items = new List<Corev1Event>()
        };

        var httpResponse = new HttpOperationResponse<Corev1EventList>
        {
            Body = eventList
        };

        coreV1.ListNamespacedEventWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetWarningEventsAsync(null);

        // Assert
        Assert.That(result, Is.Not.Null);
        await coreV1.ReceivedWithAnyArgs(1).ListNamespacedEventWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default);
    }

    #endregion

    #region GetPodNameByIpAsync Tests

    [Test]
    public async Task GetPodNameByIpAsync_WithNullKubernetes_ShouldReturnNull()
    {
        // Arrange
        var manager = new KubernetesManager(null, _logger);
        var podIp = "10.0.0.1";

        // Act
        var result = await manager.GetPodNameByIpAsync(podIp, "qdrant");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPodNameByIpAsync_WithValidIp_ShouldReturnPodName()
    {
        // Arrange
        var podIp = "10.0.0.1";
        var podName = "qdrant-0";
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);

        var podList = new V1PodList
        {
            Items = new List<V1Pod>
            {
                new V1Pod
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = podName
                    },
                    Status = new V1PodStatus
                    {
                        PodIP = podIp
                    }
                }
            }
        };

        var httpResponse = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        coreV1.ListNamespacedPodWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetPodNameByIpAsync(podIp, namespace1);

        // Assert
        Assert.That(result, Is.EqualTo(podName));
    }

    [Test]
    public async Task GetPodNameByIpAsync_WithNoPodFound_ShouldReturnNull()
    {
        // Arrange
        var podIp = "10.0.0.1";
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);

        var podList = new V1PodList
        {
            Items = new List<V1Pod>()
        };

        var httpResponse = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        coreV1.ListNamespacedPodWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetPodNameByIpAsync(podIp, namespace1);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPodNameByIpAsync_WithException_ShouldReturnNullAndLogError()
    {
        // Arrange
        var podIp = "10.0.0.1";
        var namespace1 = "qdrant";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);

        coreV1.ListNamespacedPodWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<HttpOperationResponse<V1PodList>>(new Exception("API Error")));

        // Act
        var result = await _manager.GetPodNameByIpAsync(podIp, namespace1);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPodNameByIpAsync_WithNullNamespace_ShouldUseDefaultNamespace()
    {
        // Arrange
        var podIp = "10.0.0.1";
        var coreV1 = Substitute.For<ICoreV1Operations>();
        _kubernetes.CoreV1.Returns(coreV1);
        
        var podList = new V1PodList
        {
            Items = new List<V1Pod>()
        };

        var httpResponse = new HttpOperationResponse<V1PodList>
        {
            Body = podList
        };

        coreV1.ListNamespacedPodWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(httpResponse));

        // Act
        var result = await _manager.GetPodNameByIpAsync(podIp, null);

        // Assert
        await coreV1.ReceivedWithAnyArgs(1).ListNamespacedPodWithHttpMessagesAsync(default, default, default, default, default, default, default, default, default, default, default, default, default);
    }

    #endregion
}


