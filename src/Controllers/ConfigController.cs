using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly IDynamicConfigService _dynamicConfigService;
    private readonly IWebHostEnvironment _environment;
    private readonly IKubernetesManager? _kubernetesManager;
    private readonly ISnapshotService _snapshotService;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(
        IDynamicConfigService dynamicConfigService,
        IWebHostEnvironment environment,
        IKubernetesManager? kubernetesManager,
        ISnapshotService snapshotService,
        ILogger<ConfigController> logger)
    {
        _dynamicConfigService = dynamicConfigService;
        _environment = environment;
        _kubernetesManager = kubernetesManager;
        _snapshotService = snapshotService;
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
    /// Get current environment name and namespace
    /// </summary>
    [HttpGet("environment")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetEnvironment()
    {
        var currentNamespace = _kubernetesManager?.GetCurrentNamespace() ?? "default";
        return Ok(new 
        { 
            environment = _environment.EnvironmentName,
            @namespace = currentNamespace
        });
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
            var previousConfig = await _dynamicConfigService.GetConfigAsync(cancellationToken);

            var config = new DynamicConfig
            {
                MonitoringIntervalSeconds = request.MonitoringIntervalSeconds,
                Snapshot = request.Snapshot ?? new SnapshotConfiguration(),
                S3 = request.S3 ?? new S3DynamicConfig()
            };

            await _dynamicConfigService.UpdateConfigAsync(config, cancellationToken);

            if (previousConfig.S3.BucketName != config.S3.BucketName ||
                previousConfig.S3.Enabled != config.S3.Enabled)
            {
                _snapshotService.InvalidateCache();
                _logger.LogInformation("S3 configuration changed, snapshot cache invalidated");
            }

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
    public SnapshotConfiguration? Snapshot { get; init; }
    public S3DynamicConfig? S3 { get; init; }
}
