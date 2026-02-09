using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Models;
using Vigilante.Services;
using Vigilante.Services.Interfaces;
using k8s.Models;

namespace Aer.Vigilante.Tests.Services;

[TestFixture]
public class DynamicConfigServiceTests
{
    private IKubernetesManager _kubernetesManager = null!;
    private ILogger<DynamicConfigService> _logger = null!;
    private DynamicConfigService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _kubernetesManager = Substitute.For<IKubernetesManager>();
        _logger = Substitute.For<ILogger<DynamicConfigService>>();
        
        // Mock UpdateEndpointsAnnotationsAsync by default to prevent hanging
        _kubernetesManager.UpdateEndpointsAnnotationsAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        
        _service = new DynamicConfigService(_kubernetesManager, _logger);
    }

    [Test]
    [CancelAfter(5000)] // 5 second timeout
    public async Task GetConfigAsync_WhenEndpointsExists_ReturnsConfigFromAnnotation()
    {
        // Arrange
        var expectedConfig = new DynamicConfig { MonitoringIntervalSeconds = 60 };
        var endpoints = CreateEndpointsWithConfig(expectedConfig);

        _kubernetesManager.GetOrCreateEndpointsAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(endpoints);

        // Act
        var result = await _service.GetConfigAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MonitoringIntervalSeconds, Is.EqualTo(60));
    }

    [Test]
    [CancelAfter(5000)] // 5 second timeout
    public async Task GetConfigAsync_WhenAnnotationMissing_InitializesWithDefaults()
    {
        // Arrange
        var endpoints = new V1Endpoints
        {
            Metadata = new V1ObjectMeta
            {
                Name = "vigilante-dynamic-config",
                Annotations = new Dictionary<string, string>() // No config annotation
            }
        };

        _kubernetesManager.GetOrCreateEndpointsAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(endpoints);

        // Act
        var result = await _service.GetConfigAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MonitoringIntervalSeconds, Is.EqualTo(120)); // Default value

        // Verify that UpdateConfigAsync was called to initialize
        await _kubernetesManager.Received(1).UpdateEndpointsAnnotationsAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [CancelAfter(5000)] // 5 second timeout
    public async Task GetConfigAsync_WhenAnnotationIsInvalidJson_ReturnsDefaultAndInitializes()
    {
        // Arrange
        var endpoints = new V1Endpoints
        {
            Metadata = new V1ObjectMeta
            {
                Name = "vigilante-dynamic-config",
                Annotations = new Dictionary<string, string>
                {
                    ["vigilante.io/dynamic-config"] = "invalid json {{{" // Invalid JSON
                }
            }
        };

        _kubernetesManager.GetOrCreateEndpointsAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(endpoints);

        // Act
        var result = await _service.GetConfigAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MonitoringIntervalSeconds, Is.EqualTo(120)); // Default value
    }

    [Test]
    [CancelAfter(5000)] // 5 second timeout
    public async Task UpdateConfigAsync_UpdatesEndpointsAnnotation()
    {
        // Arrange
        var newConfig = new DynamicConfig { MonitoringIntervalSeconds = 180 };

        // Act
        await _service.UpdateConfigAsync(newConfig);

        // Assert
        await _kubernetesManager.Received(1).UpdateEndpointsAnnotationsAsync(
            "vigilante-dynamic-config",
            Arg.Is<Dictionary<string, string>>(d => 
                d.ContainsKey("vigilante.io/dynamic-config") &&
                d["vigilante.io/dynamic-config"].Contains("180")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [CancelAfter(5000)]
    public async Task UpdateConfigAsync_RaisesConfigChangedEvent()
    {
        // Arrange
        var newConfig = new DynamicConfig { MonitoringIntervalSeconds = 90 };
        DynamicConfig? receivedConfig = null;
        var eventRaised = false;

        _service.ConfigChanged += (sender, config) =>
        {
            eventRaised = true;
            receivedConfig = config;
        };

        // Act
        await _service.UpdateConfigAsync(newConfig);

        // Assert
        Assert.That(eventRaised, Is.True, "ConfigChanged event should be raised");
        Assert.That(receivedConfig, Is.Not.Null);
        Assert.That(receivedConfig!.MonitoringIntervalSeconds, Is.EqualTo(90));
    }

    private static V1Endpoints CreateEndpointsWithConfig(DynamicConfig config)
    {
        var configJson = System.Text.Json.JsonSerializer.Serialize(config);
        return new V1Endpoints
        {
            Metadata = new V1ObjectMeta
            {
                Name = "vigilante-dynamic-config",
                Annotations = new Dictionary<string, string>
                {
                    ["vigilante.io/dynamic-config"] = configJson
                }
            },
            Subsets = new List<V1EndpointSubset>()
        };
    }
}
