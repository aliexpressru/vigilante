using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Vigilante.Controllers;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Aer.Vigilante.Tests.Controllers;

[TestFixture]
public class ConfigControllerTests
{
    private IDynamicConfigService _dynamicConfigService = null!;
    private IWebHostEnvironment _environment = null!;
    private IKubernetesManager _kubernetesManager = null!;
    private ISnapshotService _snapshotService = null!;
    private ILogger<ConfigController> _logger = null!;
    private ConfigController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _environment = Substitute.For<IWebHostEnvironment>();
        _kubernetesManager = Substitute.For<IKubernetesManager>();
        _snapshotService = Substitute.For<ISnapshotService>();
        _logger = Substitute.For<ILogger<ConfigController>>();

        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new DynamicConfig());

        _controller = new ConfigController(_dynamicConfigService, _environment, _kubernetesManager, _snapshotService, _logger);
    }

    [Test]
    public async Task GetConfig_ReturnsCurrentConfig()
    {
        // Arrange
        var expectedConfig = new DynamicConfig { MonitoringIntervalSeconds = 60 };
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(expectedConfig);

        // Act
        var result = await _controller.GetConfig(CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.EqualTo(expectedConfig));
    }

    [Test]
    public async Task GetConfig_WhenExceptionThrown_Returns500()
    {
        // Arrange
        _dynamicConfigService.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DynamicConfig>(new Exception("Test error")));

        // Act
        var result = await _controller.GetConfig(CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task UpdateConfig_WithValidValue_UpdatesAndReturnsConfig()
    {
        // Arrange
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = 90, DiskUsageAlertThresholdPercent = 85m };

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var returnedConfig = okResult.Value as DynamicConfig;
        
        Assert.That(returnedConfig, Is.Not.Null);
        Assert.That(returnedConfig!.MonitoringIntervalSeconds, Is.EqualTo(90));
        Assert.That(returnedConfig.DiskUsageAlertThresholdPercent, Is.EqualTo(85m));

        await _dynamicConfigService.Received(1).UpdateConfigAsync(
            Arg.Is<DynamicConfig>(c => c.MonitoringIntervalSeconds == 90 && c.DiskUsageAlertThresholdPercent == 85m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateConfig_WithValueLessThan1_ReturnsBadRequest()
    {
        // Arrange
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = 0 };

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        
        await _dynamicConfigService.DidNotReceive().UpdateConfigAsync(
            Arg.Any<DynamicConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateConfig_WithValueGreaterThan3600_ReturnsBadRequest()
    {
        // Arrange
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = 3601 };

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        
        await _dynamicConfigService.DidNotReceive().UpdateConfigAsync(
            Arg.Any<DynamicConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateConfig_WithDiskThresholdOutOfRange_ReturnsBadRequest()
    {
        var request = new UpdateConfigRequest
        {
            MonitoringIntervalSeconds = 60,
            DiskUsageAlertThresholdPercent = 101m
        };

        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        await _dynamicConfigService.DidNotReceive().UpdateConfigAsync(
            Arg.Any<DynamicConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateConfig_WithRamThresholdOutOfRange_ReturnsBadRequest()
    {
        var request = new UpdateConfigRequest
        {
            MonitoringIntervalSeconds = 60,
            DiskUsageAlertThresholdPercent = 90m,
            RamUsageAlertThresholdPercent = 0m
        };

        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        await _dynamicConfigService.DidNotReceive().UpdateConfigAsync(
            Arg.Any<DynamicConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [TestCase(1)]
    [TestCase(60)]
    [TestCase(120)]
    [TestCase(300)]
    [TestCase(3600)]
    public async Task UpdateConfig_WithValidBoundaryValues_Succeeds(int seconds)
    {
        // Arrange
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = seconds };

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        
        await _dynamicConfigService.Received(1).UpdateConfigAsync(
            Arg.Is<DynamicConfig>(c => c.MonitoringIntervalSeconds == seconds),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateConfig_WhenServiceThrowsException_Returns500()
    {
        // Arrange
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = 60 };
        
        _dynamicConfigService.UpdateConfigAsync(
            Arg.Any<DynamicConfig>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("Kubernetes error")));

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public void GetEnvironment_ReturnsEnvironmentAndNamespace()
    {
        // Arrange
        _environment.EnvironmentName.Returns("Development");
        _kubernetesManager.GetCurrentNamespace().Returns("my-namespace");

        // Act
        var result = _controller.GetEnvironment();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        
        dynamic? value = okResult.Value;
        Assert.That(value, Is.Not.Null);
        
        var envName = value!.GetType().GetProperty("environment")?.GetValue(value)?.ToString();
        var namespaceName = value.GetType().GetProperty("namespace")?.GetValue(value)?.ToString();
        
        Assert.That(envName, Is.EqualTo("Development"));
        Assert.That(namespaceName, Is.EqualTo("my-namespace"));
    }

    [Test]
    public void GetEnvironment_WhenKubernetesManagerIsNull_ReturnsDefaultNamespace()
    {
        // Arrange
        _environment.EnvironmentName.Returns("Production");
        var controller = new ConfigController(_dynamicConfigService, _environment, null, _snapshotService, _logger);

        // Act
        var result = controller.GetEnvironment();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        
        dynamic? value = okResult.Value;
        Assert.That(value, Is.Not.Null);
        
        var envName = value!.GetType().GetProperty("environment")?.GetValue(value)?.ToString();
        var namespaceName = value.GetType().GetProperty("namespace")?.GetValue(value)?.ToString();
        
        Assert.That(envName, Is.EqualTo("Production"));
        Assert.That(namespaceName, Is.EqualTo("default"));
    }
}
