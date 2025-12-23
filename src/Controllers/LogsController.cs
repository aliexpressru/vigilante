using Microsoft.AspNetCore.Mvc;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;
using Vigilante.Services.Models;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/logs")]
public class LogsController(ILogReader logReader, ILogger<LogsController> logger) : ControllerBase
{
    [HttpPost("qdrant")]
    [ProducesResponseType(typeof(V1LogsPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetQdrantLogs(
        [FromBody] V1GetQdrantLogsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var podName = request.PodName!;
            var serviceQuery = new LogQuery(request.Namespace, request.Limit, request.Continuation);
            var page = await logReader.GetQdrantPodLogsAsync(podName, serviceQuery, cancellationToken);
            return Ok(ToResponse(page));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch logs for pod {Pod}", request.PodName);
            return StatusCode(500, new { error = "Failed to fetch logs", details = ex.Message });
        }
    }

    [HttpPost("vigilante")]
    [ProducesResponseType(typeof(V1LogsPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetVigilanteLogs(
        [FromBody] V1GetVigilanteLogsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var serviceQuery = new LogQuery(request.Namespace, request.Limit, request.Continuation);
            var page = await logReader.GetServiceLogsAsync(serviceQuery, cancellationToken);
            return Ok(ToResponse(page));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch service logs");
            return StatusCode(500, new { error = "Failed to fetch service logs", details = ex.Message });
        }
    }

    private static V1LogsPageResponse ToResponse(LogPage page)
    {
        return new V1LogsPageResponse
        {
            Success = page.Success,
            Error = page.Error,
            Message = page.Error ?? string.Empty,
            Logs = page.Logs
                .Select(e => new V1LogEntry(e.Timestamp, e.Message, e.Source))
                .ToArray(),
            Continuation = page.Continuation,
            Truncated = page.Truncated
        };
    }
}
