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
    private ILogger<ConfigController> _logger = null!;
    private ConfigController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _dynamicConfigService = Substitute.For<IDynamicConfigService>();
        _logger = Substitute.For<ILogger<ConfigController>>();
        _controller = new ConfigController(_dynamicConfigService, _logger);
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
        var request = new UpdateConfigRequest { MonitoringIntervalSeconds = 90 };

        // Act
        var result = await _controller.UpdateConfig(request, CancellationToken.None);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var returnedConfig = okResult.Value as DynamicConfig;
        
        Assert.That(returnedConfig, Is.Not.Null);
        Assert.That(returnedConfig!.MonitoringIntervalSeconds, Is.EqualTo(90));

        await _dynamicConfigService.Received(1).UpdateConfigAsync(
            Arg.Is<DynamicConfig>(c => c.MonitoringIntervalSeconds == 90),
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
}
