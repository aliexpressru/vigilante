using Microsoft.AspNetCore.Mvc;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/kubernetes")]
public class KubernetesController(
    IKubernetesManager kubernetesManager,
    ILogger<KubernetesController> logger)
    : ControllerBase
{
    [HttpPost("pods/delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeletePodAsync(
        [FromBody] V1DeletePodRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PodName))
        {
            return BadRequest(new { error = "PodName is required" });
        }

        try
        {
            var success = await kubernetesManager.DeletePodAsync(
                request.PodName,
                request.Namespace,
                cancellationToken);

            if (success)
            {
                return Ok(new { message = $"Pod '{request.PodName}' deletion initiated successfully" });
            }

            return StatusCode(500, new { error = $"Failed to delete pod '{request.PodName}'" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting pod {PodName}", request.PodName);
            return StatusCode(500, new { error = "Internal server error during pod deletion", details = ex.Message });
        }
    }

    [HttpPost("statefulsets/manage")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ManageStatefulSetAsync(
        [FromBody] V1ManageStatefulSetRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StatefulSetName))
        {
            return BadRequest(new { error = "StatefulSetName is required" });
        }

        try
        {
            bool success;
            string operationDescription;

            switch (request.OperationType)
            {
                case StatefulSetOperationType.Rollout:
                    success = await kubernetesManager.RolloutRestartStatefulSetAsync(
                        request.StatefulSetName,
                        request.Namespace,
                        cancellationToken);
                    operationDescription = "rollout restart";
                    break;

                case StatefulSetOperationType.Scale:
                    if (!request.Replicas.HasValue)
                    {
                        return BadRequest(new { error = "Replicas is required for Scale operation" });
                    }

                    if (request.Replicas.Value < 0)
                    {
                        return BadRequest(new { error = "Replicas must be non-negative" });
                    }

                    success = await kubernetesManager.ScaleStatefulSetAsync(
                        request.StatefulSetName,
                        request.Replicas.Value,
                        request.Namespace,
                        cancellationToken);
                    operationDescription = $"scale to {request.Replicas.Value} replicas";
                    break;

                default:
                    return BadRequest(new { error = $"Unknown operation type: {request.OperationType}" });
            }

            if (success)
            {
                return Ok(new
                {
                    message = $"StatefulSet '{request.StatefulSetName}' {operationDescription} initiated successfully"
                });
            }

            return StatusCode(500, new
            {
                error = $"Failed to {operationDescription} StatefulSet '{request.StatefulSetName}'"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error managing StatefulSet {StatefulSetName}", request.StatefulSetName);
            return StatusCode(500, new
            {
                error = "Internal server error during StatefulSet management",
                details = ex.Message
            });
        }
    }
}

