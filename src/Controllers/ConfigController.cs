using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly IDynamicConfigService _dynamicConfigService;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(
        IDynamicConfigService dynamicConfigService,
        ILogger<ConfigController> logger)
    {
        _dynamicConfigService = dynamicConfigService;
        _logger = logger;
    }

    /// <summary>
    /// Get current dynamic configuration
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DynamicConfig), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        try
        {
            var config = await _dynamicConfigService.GetConfigAsync(cancellationToken);
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get dynamic config");
            return StatusCode(500, new { error = "Failed to get configuration" });
        }
    }

    /// <summary>
    /// Update dynamic configuration
    /// </summary>
    /// <param name="request">New configuration values</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpPut]
    [ProducesResponseType(typeof(DynamicConfig), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateConfig(
        [FromBody] UpdateConfigRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MonitoringIntervalSeconds < 1)
        {
            return BadRequest(new { error = "MonitoringIntervalSeconds must be at least 1" });
        }

        if (request.MonitoringIntervalSeconds > 3600)
        {
            return BadRequest(new { error = "MonitoringIntervalSeconds cannot exceed 3600 (1 hour)" });
        }

        try
        {
            var config = new DynamicConfig
            {
                MonitoringIntervalSeconds = request.MonitoringIntervalSeconds
            };

            await _dynamicConfigService.UpdateConfigAsync(config, cancellationToken);
            
            _logger.LogInformation(
                "Dynamic configuration updated via API: MonitoringIntervalSeconds={Interval}",
                config.MonitoringIntervalSeconds);

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update dynamic config");
            return StatusCode(500, new { error = "Failed to update configuration" });
        }
    }
}

public record UpdateConfigRequest
{
    public int MonitoringIntervalSeconds { get; init; } = 120;
}
