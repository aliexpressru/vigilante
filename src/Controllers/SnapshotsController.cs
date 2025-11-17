using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/snapshots")]
public class SnapshotsController(
    ISnapshotService snapshotService,
    ICollectionService collectionService,
    ILogger<SnapshotsController> logger)
    : ControllerBase
{
    [HttpGet("info")]
    public async Task<ActionResult<V1GetSnapshotsInfoResponse>> GetSnapshotsInfo(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await snapshotService.GetSnapshotsInfoAsync(cancellationToken);

            return Ok(new V1GetSnapshotsInfoResponse
            {
                Snapshots = result
                    .Select(snapshot => new V1GetSnapshotsInfoResponse.SnapshotInfoDto
                    {
                        PodName = snapshot.PodName,
                        NodeUrl = snapshot.NodeUrl,
                        PeerId = snapshot.PeerId,
                        CollectionName = snapshot.CollectionName,
                        SnapshotName = snapshot.SnapshotName,
                        SizeBytes = snapshot.SizeBytes,
                        PrettySize = snapshot.PrettySize,
                        PodNamespace = snapshot.PodNamespace,
                        Source = snapshot.Source.ToString()
                    })
                    .OrderBy(x => x.CollectionName)
                    .ThenBy(x => x.SnapshotName)
                    .ToArray()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to get snapshots info", details = ex.Message });
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(V1CreateSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1CreateSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1CreateSnapshotResponse>> CreateSnapshot(
        [FromBody] V1CreateSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.SingleNode)
            {
                // Create snapshot on specific node
                var snapshotName = await collectionService.CreateCollectionSnapshotAsync(
                    request.NodeUrl!,
                    request.CollectionName,
                    cancellationToken);

                var response = new V1CreateSnapshotResponse
                {
                    Success = snapshotName != null,
                    SnapshotName = snapshotName,
                    Message = snapshotName != null
                        ? $"Snapshot '{snapshotName}' created successfully for collection '{request.CollectionName}' on {request.NodeUrl}"
                        : $"Failed to create snapshot for collection '{request.CollectionName}' on {request.NodeUrl}"
                };

                return snapshotName != null ? Ok(response) : StatusCode(500, response);
            }
            else
            {
                // Create snapshot on all nodes
                var results = await snapshotService.CreateCollectionSnapshotOnAllNodesAsync(
                    request.CollectionName,
                    cancellationToken);

                var successCount = results.Values.Count(s => s != null);
                var response = new V1CreateSnapshotResponse
                {
                    Success = successCount > 0,
                    Message = successCount == 0
                        ? "Failed to create snapshot on any node"
                        : $"Snapshot created for collection '{request.CollectionName}' on {successCount}/{results.Count} nodes",
                    Results = results
                };

                return successCount > 0 ? Ok(response) : StatusCode(500, response);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot creation");
            return StatusCode(500, new V1CreateSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during snapshot creation",
            });
        }
    }

    [HttpDelete]
    [ProducesResponseType(typeof(V1DeleteSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1DeleteSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1DeleteSnapshotResponse>> DeleteSnapshot(
        [FromBody] V1DeleteSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.SingleNode)
            {
                // Parse source from request
                if (!Enum.TryParse<Models.Enums.SnapshotSource>(request.Source, out var source))
                {
                    return BadRequest(new V1DeleteSnapshotResponse
                    {
                        Success = false,
                        Message = "Invalid Source value. Must be 'KubernetesStorage' or 'QdrantApi'",
                        Results = new Dictionary<string, NodeSnapshotDeletionResult>()
                    });
                }

                // Use universal deletion method from service
                var success = await snapshotService.DeleteSnapshotAsync(
                    request.CollectionName,
                    request.SnapshotName,
                    source,
                    request.NodeUrl,
                    request.PodName,
                    request.PodNamespace,
                    cancellationToken);

                var identifier = source == Models.Enums.SnapshotSource.KubernetesStorage 
                    ? request.PodName 
                    : request.NodeUrl;

                var response = new V1DeleteSnapshotResponse
                {
                    Success = success,
                    Message = success
                        ? $"Snapshot '{request.SnapshotName}' deleted successfully for collection '{request.CollectionName}'"
                        : $"Failed to delete snapshot '{request.SnapshotName}' for collection '{request.CollectionName}'",
                    Results = new Dictionary<string, NodeSnapshotDeletionResult>
                    {
                        [identifier!] = new NodeSnapshotDeletionResult
                        {
                            Success = success,
                            Error = success ? null : "Deletion failed"
                        }
                    }
                };

                return success ? Ok(response) : StatusCode(500, response);
            }
            else
            {
                // Delete snapshot on all nodes via API (default behavior for backward compatibility)
                var results = await snapshotService.DeleteCollectionSnapshotOnAllNodesAsync(
                    request.CollectionName,
                    request.SnapshotName,
                    cancellationToken);

                var successCount = results.Values.Count(s => s);
                var nodeResults = results.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new NodeSnapshotDeletionResult
                    {
                        Success = kvp.Value,
                        Error = kvp.Value ? null : "Deletion failed"
                    });

                var response = new V1DeleteSnapshotResponse
                {
                    Success = successCount > 0,
                    Message = successCount == 0
                        ? "Failed to delete snapshot on any node"
                        : $"Snapshot '{request.SnapshotName}' deleted for collection '{request.CollectionName}' on {successCount}/{results.Count} nodes",
                    Results = nodeResults
                };

                return successCount > 0 ? Ok(response) : StatusCode(500, response);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot deletion");
            return StatusCode(500, new V1DeleteSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during snapshot deletion",
                Results = new Dictionary<string, NodeSnapshotDeletionResult>
                {
                    ["error"] = new NodeSnapshotDeletionResult
                    {
                        Success = false,
                        Error = ex.Message
                    }
                }
            });
        }
    }

    [HttpPost("download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DownloadSnapshot(
        [FromBody] V1DownloadSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotStream = await snapshotService.DownloadSnapshotWithFallbackAsync(
                request.NodeUrl!,
                request.CollectionName,
                request.SnapshotName,
                request.PodName,
                request.PodNamespace,
                cancellationToken);

            if (snapshotStream == null)
            {
                return StatusCode(500, new { error = "Failed to download snapshot via both API and disk" });
            }

            return File(snapshotStream, "application/octet-stream", request.SnapshotName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot download");
            return StatusCode(500, new { error = "Internal server error during snapshot download", details = ex.Message });
        }
    }

    [HttpPost("recover")]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RecoverFromSnapshotResponse>> RecoverFromSnapshot(
        [FromBody] V1RecoverFromSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await collectionService.RecoverCollectionFromSnapshotAsync(
                request.NodeUrl,
                request.CollectionName,
                request.SnapshotName,
                cancellationToken);

            var response = new V1RecoverFromSnapshotResponse
            {
                Success = success,
                Message = success
                    ? $"Collection '{request.CollectionName}' recovered successfully from snapshot '{request.SnapshotName}' on {request.NodeUrl}"
                    : $"Failed to recover collection '{request.CollectionName}' from snapshot '{request.SnapshotName}' on {request.NodeUrl}",
                Error = success ? null : "Recovery failed"
            };

            return success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot recovery");
            return StatusCode(500, new V1RecoverFromSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during snapshot recovery",
                Error = ex.Message
            });
        }
    }

    [HttpPost("recover-from-url")]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RecoverFromSnapshotResponse>> RecoverFromUrl(
        [FromBody] V1RecoverFromUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await collectionService.RecoverCollectionFromUrlAsync(
                request.NodeUrl,
                request.CollectionName,
                request.SnapshotUrl,
                request.SnapshotChecksum,
                request.WaitForResult,
                cancellationToken);

            var response = new V1RecoverFromSnapshotResponse
            {
                Success = success,
                Message = success
                    ? $"Collection '{request.CollectionName}' recovered successfully from URL '{request.SnapshotUrl}' on {request.NodeUrl}"
                    : $"Failed to recover collection '{request.CollectionName}' from URL '{request.SnapshotUrl}' on {request.NodeUrl}",
                Error = success ? null : "Recovery failed"
            };

            return success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot recovery from URL");
            return StatusCode(500, new V1RecoverFromSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during snapshot recovery from URL",
                Error = ex.Message
            });
        }
    }
}

