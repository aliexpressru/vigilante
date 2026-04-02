using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Jobs;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/jobs")]
public class JobsController(
    IJobRegistry jobRegistry,
    IDynamicConfigService dynamicConfigService,
    ISnapshotAutomationStatus snapshotAutomationStatus,
    ILogger<JobsController> logger) : ControllerBase
{
    /// <summary>Do not drain snapshot-automation on status poll — otherwise the HTTP call blocks until the run ends and the UI only ever sees idle.</summary>
    private static readonly IReadOnlySet<string> ExcludeSnapshotAutomationOnStatusPoll =
        new HashSet<string> { SnapshotAutomationJob.JobKey };

    /// <summary>
    /// Returns current background jobs with metadata (e.g. ReplicationPlan for restore replication factor) and any errors.
    /// Advances pending jobs (e.g. PendingSnapshotCreationJob) on each call so they progress with frontend auto-refresh.
    /// <see cref="SnapshotAutomationJob"/> is removed from the registry when idle; when snapshot automation is enabled,
    /// a synthetic row is always returned so the dashboard shows last run / idle state.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(IReadOnlyList<JobInfoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<JobInfoDto>>> GetJobsStatus(CancellationToken cancellationToken)
    {
        try
        {
            await jobRegistry.ProcessPendingJobsAsync(cancellationToken, ExcludeSnapshotAutomationOnStatusPoll);
            var config = await dynamicConfigService.GetConfigAsync(cancellationToken);
            var infos = jobRegistry.GetJobInfos();
            if (IsSnapshotAutomationConfigured(config))
                infos = MergeSnapshotAutomationRow(infos, snapshotAutomationStatus);
            return Ok(infos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get jobs status");
            return StatusCode(500, new { error = "Failed to get jobs status", details = ex.Message });
        }
    }

    /// <summary>
    /// Cancels a running job by key and removes it from registry.
    /// If the job is already completed but has an Error row, removes that row.
    /// </summary>
    [HttpPost("cancel")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CancelJob([FromBody] V1CancelJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
            return BadRequest(new { error = "Job key is required" });

        try
        {
            var cancelled = await jobRegistry.CancelJobAsync(request.Key, cancellationToken);
            if (!cancelled)
                return NotFound(new { error = $"Job with key '{request.Key}' was not found" });

            return Ok(new { success = true, message = $"Job '{request.Key}' was removed" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel job with key {Key}", request.Key);
            return StatusCode(500, new { error = "Failed to cancel job", details = ex.Message });
        }
    }

    private static bool IsSnapshotAutomationConfigured(DynamicConfig config)
    {
        var s = config.Snapshot;
        if (s.DeleteOrphanedAfterMinutes.HasValue)
            return true;
        if (s.Schedule.Enabled)
            return true;
        return s.CollectionOverrides?.Values.Any(o => o.Enabled) == true;
    }

    private static List<JobInfoDto> MergeSnapshotAutomationRow(
        IReadOnlyList<JobInfoDto> infos,
        ISnapshotAutomationStatus status)
    {
        const string key = SnapshotAutomationJob.JobKey;
        var sameKey = infos.Where(i => i.Key == key).ToList();
        var rest = infos.Where(i => i.Key != key).ToList();
        var err = sameKey.Select(i => i.ErrorMessage).FirstOrDefault(e => !string.IsNullOrEmpty(e));
        var errAt = sameKey.Select(i => i.ErrorRecordedAt).FirstOrDefault(a => a != null);
        var fromRegistry = sameKey.Select(i => i.Metadata).FirstOrDefault(m => m is { Count: > 0 });

        var merged = new Dictionary<string, object?>(status.GetDisplayMetadata());
        if (fromRegistry is not null)
        {
            foreach (var kv in fromRegistry)
            {
                if (kv.Value is null && merged.ContainsKey(kv.Key))
                    continue;
                merged[kv.Key] = kv.Value;
            }
        }

        rest.Add(new JobInfoDto(key, err, errAt, merged));
        return rest;
    }
}
