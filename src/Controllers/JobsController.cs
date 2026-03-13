using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/jobs")]
public class JobsController(IJobRegistry jobRegistry, ILogger<JobsController> logger) : ControllerBase
{
    /// <summary>
    /// Returns current background jobs with metadata (e.g. ReplicationPlan for restore replication factor) and any errors.
    /// Advances pending jobs (e.g. PendingSnapshotCreationJob) on each call so they progress with frontend auto-refresh.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(IReadOnlyList<JobInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<JobInfoDto>>> GetJobsStatus(CancellationToken cancellationToken)
    {
        try
        {
            await jobRegistry.ProcessPendingJobsAsync(cancellationToken);
            var infos = jobRegistry.GetJobInfos();
            return Ok(infos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get jobs status");
            return StatusCode(500, new { error = "Failed to get jobs status", details = ex.Message });
        }
    }
}
