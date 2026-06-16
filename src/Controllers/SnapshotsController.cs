using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.AspNetCore.Mvc;
using Vigilante.Constants;
using Vigilante.Extensions;
using Vigilante.Models.Enums;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;
using Vigilante.Models.Snapshots;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/snapshots")]
public class SnapshotsController(
    ISnapshotService snapshotService,
    IS3SnapshotService s3SnapshotService,
    IClusterManager clusterManager,
    ILogger<SnapshotsController> logger)
    : ControllerBase
{
    [HttpGet("info")]
    [ProducesResponseType(typeof(V1GetSnapshotsInfoPaginatedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1GetSnapshotsInfoPaginatedResponse>> GetSnapshotsInfo(
        [FromQuery] V1GetSnapshotsInfoRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await clusterManager.GetClusterStateAsync(cancellationToken);
            var result = await snapshotService.GetSnapshotsInfoAsync(cancellationToken, request.ClearCache, state.Nodes);

            var snapshotDtos = result
                .Select(snapshot => new V1GetSnapshotsInfoPaginatedResponse.SnapshotInfoDto
                {
                    PodName = snapshot.PodName,
                    NodeUrl = snapshot.NodeUrl,
                    PeerId = snapshot.PeerId.ToString(),
                    CollectionName = snapshot.CollectionName,
                    SnapshotName = snapshot.SnapshotName,
                    SizeBytes = snapshot.SizeBytes,
                    PrettySize = snapshot.PrettySize,
                    PodNamespace = snapshot.PodNamespace,
                    Source = snapshot.Source.ToString()
                })
                .OrderBy(x => x.CollectionName)
                .ThenBy(x => x.SnapshotName)
                .ToList();

            // Group snapshots by collection name
            var collectionGroups = snapshotDtos
                .GroupBy(s => s.CollectionName)
                .Select(g => new V1GetSnapshotsInfoPaginatedResponse.SnapshotCollectionGroup
                {
                    CollectionName = g.Key,
                    TotalSize = g.Sum(s => s.SizeBytes),
                    PrettyTotalSize = g.Sum(s => s.SizeBytes).ToPrettySize(),
                    Snapshots = [.. g]
                })
                .OrderBy(g => g.CollectionName)
                .ToList();

            // Apply name filter if provided
            if (!string.IsNullOrWhiteSpace(request.NameFilter))
            {
                collectionGroups = [.. collectionGroups.Where(g => g.CollectionName.Contains(request.NameFilter, StringComparison.OrdinalIgnoreCase))];
            }

            // Calculate pagination on unique collection groups
            var totalItems = collectionGroups.Count;
            var totalPages = (int)Math.Ceiling(totalItems / (double)request.PageSize);
            var skip = (request.Page - 1) * request.PageSize;

            // Apply pagination to groups
            var pagedGroups = collectionGroups
                .Skip(skip)
                .Take(request.PageSize)
                .ToArray();

            return Ok(new V1GetSnapshotsInfoPaginatedResponse
            {
                GroupedSnapshots = pagedGroups,
                Pagination = new V1GetSnapshotsInfoPaginatedResponse.PaginationInfo
                {
                    CurrentPage = request.Page,
                    PageSize = request.PageSize,
                    TotalItems = totalItems,
                    TotalPages = totalPages
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get paginated snapshots info");
            return StatusCode(500, new { error = "Failed to get paginated snapshots info", details = ex.Message });
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(V1CreateSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1CreateSnapshotResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(V1CreateSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1CreateSnapshotResponse>> CreateSnapshot(
        [FromBody] V1CreateSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate NodeUrls
            if (request.NodeUrls == null || request.NodeUrls.Count == 0)
            {
                return BadRequest(new V1CreateSnapshotResponse
                {
                    Success = false,
                    Message = "NodeUrls list is required and cannot be empty"
                });
            }

            var batch = await snapshotService.CreateCollectionSnapshotAsync(
                request.CollectionName,
                request.NodeUrls,
                cancellationToken);

            if (batch.SkippedDuplicatePending)
            {
                return Conflict(new V1CreateSnapshotResponse
                {
                    Success = false,
                    Message =
                        "Snapshot creation for this collection is already in progress; wait until it finishes before starting another.",
                    Results = batch.Results
                });
            }

            var results = batch.Results;
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
            Dictionary<string, bool> results;

            switch (request.Source)
            {
                case SnapshotSource.QdrantApi:
                    // Validate NodeUrls
                    if (request.NodeUrls == null || request.NodeUrls.Count == 0)
                    {
                        return BadRequest(new V1DeleteSnapshotResponse
                        {
                            Success = false,
                            Message = "NodeUrls list is required for QdrantApi source",
                            Results = []
                        });
                    }

                    results = await snapshotService.DeleteCollectionSnapshotApiAsync(
                        request.CollectionName,
                        request.SnapshotName,
                        request.NodeUrls,
                        cancellationToken);

                    break;

                case SnapshotSource.KubernetesStorage:
                    // Validate Pods
                    if (request.Pods == null || request.Pods.Count == 0)
                    {
                        return BadRequest(new V1DeleteSnapshotResponse
                        {
                            Success = false,
                            Message = "Pods list is required for KubernetesStorage source",
                            Results = []
                        });
                    }

                    var pods = request.Pods.Select(p => (p.PodName, p.PodNamespace));
                    results = await snapshotService.DeleteSnapshotFromDiskAsync(
                        request.CollectionName,
                        request.SnapshotName,
                        pods,
                        cancellationToken);

                    break;

                case SnapshotSource.S3Storage:
                    // S3 deletion - single operation
                    var s3Success = await s3SnapshotService.DeleteSnapshotAsync(
                        request.CollectionName,
                        request.SnapshotName,
                        null, // namespace not needed for direct S3 deletion
                        cancellationToken);

                    results = new Dictionary<string, bool> { ["S3"] = s3Success };
                    break;

                default:
                    return BadRequest(new V1DeleteSnapshotResponse
                    {
                        Success = false,
                        Message = $"Unknown snapshot source: {request.Source}",
                        Results = []
                    });
            }

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
            Stream? snapshotStream;

            if (request.Source == SnapshotSource.S3Storage)
            {
                snapshotStream = await s3SnapshotService.DownloadSnapshotAsync(
                    request.CollectionName,
                    request.SnapshotName,
                    string.Empty,
                    cancellationToken);
            }
            else
            {
                snapshotStream = await snapshotService.DownloadSnapshotWithFallbackAsync(
                    request.NodeUrl!,
                    request.CollectionName,
                    request.SnapshotName,
                    cancellationToken,
                    request.PodName ?? string.Empty,
                    request.PodNamespace ?? string.Empty);
            }

            if (snapshotStream == null)
            {
                return StatusCode(500, new { error = "Failed to download snapshot" });
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
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RecoverFromSnapshotResponse>> RecoverFromSnapshot(
        [FromBody] V1RecoverRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotPriority = Enum.TryParse<SnapshotPriority>(request.SnapshotPriority, out var parsedPriority)
                ? parsedPriority
                : SnapshotPriority.Snapshot;
            var isUrlRecovery = !string.IsNullOrWhiteSpace(request.SnapshotUrl);
            SnapshotSource? source = null;
            if (!isUrlRecovery)
            {
                _ = Enum.TryParse<SnapshotSource>(request.Source, out var parsedSource);
                source = parsedSource;
            }

            var result = await snapshotService.RequestRecoverAsync(
                request.CollectionName,
                request.TargetNodeUrl,
                snapshotPriority,
                request.WaitForResult,
                cancellationToken,
                source: source,
                snapshotName: request.SnapshotName,
                sourceCollectionName: request.SourceCollectionName,
                snapshotUrl: request.SnapshotUrl,
                snapshotChecksum: request.SnapshotChecksum);

            if (result.AlreadyInProgress)
            {
                return Conflict(new V1RecoverFromSnapshotResponse
                {
                    Success = false,
                    Message = result.Message,
                    Error = "Already in progress"
                });
            }

            if (result.ApiError)
            {
                return StatusCode(500, new V1RecoverFromSnapshotResponse
                {
                    Success = false,
                    Message = isUrlRecovery
                        ? $"Failed to recover collection '{request.CollectionName}' from URL '{request.SnapshotUrl}' on {request.TargetNodeUrl}"
                        : result.Message,
                    Error = "Recovery failed"
                });
            }

            if (request.WaitForResult)
            {
                return Ok(new V1RecoverFromSnapshotResponse
                {
                    Success = true,
                    Message = isUrlRecovery
                        ? $"Collection '{request.CollectionName}' {MetricConstants.RecoverySuccessMessage} from URL '{request.SnapshotUrl}' on {request.TargetNodeUrl}"
                        : result.Message
                });
            }

            return Accepted(new V1RecoverFromSnapshotResponse
            {
                Success = true,
                Message = result.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during unified snapshot recovery");
            return StatusCode(500, new V1RecoverFromSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during recovery",
                Error = ex.Message
            });
        }
    }

    [HttpPost("recover-multi")]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RecoverFromSnapshotResponse>> RecoverFromMultipleSnapshots(
        [FromBody] V1MultiRecoverRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotPriority = Enum.TryParse<SnapshotPriority>(request.SnapshotPriority, out var parsedPriority)
                ? parsedPriority
                : SnapshotPriority.Snapshot;

            var result = await snapshotService.RequestMultiRecoverAsync(
                request.TargetCollectionName,
                request.Items,
                snapshotPriority,
                cancellationToken,
                sourceCollectionName: request.SourceCollectionName);

            if (result.AlreadyInProgress)
            {
                return Conflict(new V1RecoverFromSnapshotResponse
                {
                    Success = false,
                    Message = result.Message,
                    Error = "Already in progress"
                });
            }

            if (result.ApiError)
            {
                return StatusCode(500, new V1RecoverFromSnapshotResponse
                {
                    Success = false,
                    Message = result.Message,
                    Error = "Recovery failed"
                });
            }

            return Accepted(new V1RecoverFromSnapshotResponse
            {
                Success = true,
                Message = result.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during multi-snapshot recovery");
            return StatusCode(500, new V1RecoverFromSnapshotResponse
            {
                Success = false,
                Message = "Internal server error during multi-snapshot recovery",
                Error = ex.Message
            });
        }
    }

    [HttpPost("get-download-url")]
    [ProducesResponseType(typeof(V1GetDownloadUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1GetDownloadUrlResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1GetDownloadUrlResponse>> GetDownloadUrl(
        [FromBody] V1GetDownloadUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = await s3SnapshotService.GetPresignedDownloadUrlAsync(
                request.CollectionName,
                request.SnapshotName,
                TimeSpan.FromHours(request.ExpirationHours),
                cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(url))
            {
                return StatusCode(500, new V1GetDownloadUrlResponse
                {
                    Success = false,
                    Message = $"Failed to generate download URL for snapshot '{request.SnapshotName}'",
                    Url = null
                });
            }

            return Ok(new V1GetDownloadUrlResponse
            {
                Success = true,
                Message = $"Download URL generated successfully, expires in {request.ExpirationHours} hour(s)",
                Url = url
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating download URL");
            return StatusCode(500, new V1GetDownloadUrlResponse
            {
                Success = false,
                Message = "Internal server error while generating download URL",
                Url = null
            });
        }
    }
}

