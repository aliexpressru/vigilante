using Microsoft.AspNetCore.Mvc;
using Vigilante.Extensions;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;
using Aer.QdrantClient.Http.Models.Shared;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/collections")]
public class CollectionsController(
    IClusterManager clusterManager,
    IRestoreReplicationFactorJobService restoreReplicationFactorJobService,
    ILogger<CollectionsController> logger)
    : ControllerBase
{
    [HttpGet("info")]
    [ProducesResponseType(typeof(V1GetCollectionsInfoPaginatedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1GetCollectionsInfoPaginatedResponse>> GetCollectionsInfo(
        [FromQuery] V1GetCollectionsInfoRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusterManager.GetCollectionsInfoAsync(request.ClearCache, cancellationToken);

            // Collect all issues from collections into a general issues array
            var allIssues = new List<string>();
            
            // Group by collection name first
            // Note: result is already sorted by node (PodName/PeerId) from the service layer
            var collectionGroups = result
                .GroupBy(size => size.CollectionName, (key, group) => new
                {
                    CollectionName = key,
                    // Preserve the original order from service (sorted by PodName/PeerId)
                    Infos = group.Select(size =>
                    {
                        // Add formatted issues for collections with problems
                        if (size.Issues.Count > 0)
                        {
                            foreach (var issue in size.Issues)
                            {
                                allIssues.Add($"[{size.CollectionName}@{size.PodName}] {issue}");
                            }
                        }
                        
                        return new V1GetCollectionsInfoPaginatedResponse.CollectionInfo
                        {
                            PodName = size.PodName,
                            NodeUrl = size.NodeUrl,
                            PeerId = size.PeerId,
                            CollectionName = size.CollectionName,
                            PodNamespace = size.PodNamespace,
                            Metrics = size.Metrics,
                            Issues = size.Issues,
                            Warnings = size.Warnings,
                            RunningOptimizations = size.RunningOptimizations,
                            Aliases = size.Aliases,
                            Status = size.Status?.ToString()
                        };
                    }).ToList()
                })
                .OrderBy(x => x.CollectionName)
                .ToList();

            // Apply name filter if provided
            if (!string.IsNullOrWhiteSpace(request.NameFilter))
            {
                collectionGroups = collectionGroups
                    .Where(g => g.CollectionName.Contains(request.NameFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Calculate pagination on unique collections
            var totalItems = collectionGroups.Count;
            var totalPages = (int)Math.Ceiling(totalItems / (double)request.PageSize);
            var skip = (request.Page - 1) * request.PageSize;

            // Apply pagination to groups and flatten
            var pagedCollections = collectionGroups
                .Skip(skip)
                .Take(request.PageSize)
                .SelectMany(g => g.Infos)
                .ToArray();

            return Ok(new V1GetCollectionsInfoPaginatedResponse
            {
                Collections = pagedCollections,
                Issues = allIssues.ToArray(),
                Pagination = new V1GetCollectionsInfoPaginatedResponse.PaginationInfo
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
            logger.LogError(ex, "Failed to get paginated collection info");
            return StatusCode(500, new { error = "Failed to get paginated collection info", details = ex.Message });
        }
    }

    [HttpDelete]
    [ProducesResponseType(typeof(V1DeleteCollectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1DeleteCollectionResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1DeleteCollectionResponse>> DeleteCollection(
        [FromBody] V1DeleteCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Dictionary<string, bool> results;

            if (request.DeletionType == Models.Enums.CollectionDeletionType.Api)
            {
                // Validate NodeUrls
                if (request.NodeUrls == null || request.NodeUrls.Count == 0)
                {
                    return BadRequest(new V1DeleteCollectionResponse
                    {
                        Success = false,
                        Message = "NodeUrls list is required for API deletion",
                        Results = new Dictionary<string, NodeDeletionResult>()
                    });
                }

                results = await clusterManager.DeleteCollectionViaApiAsync(
                    request.CollectionName,
                    request.NodeUrls,
                    request.DeleteSnapshots,
                    cancellationToken);

                var successCount = results.Values.Count(s => s);
                var nodeResults = results.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new NodeDeletionResult
                    {
                        Success = kvp.Value,
                        Error = kvp.Value ? null : "Deletion failed"
                    });

                var response = new V1DeleteCollectionResponse
                {
                    Success = successCount > 0,
                    Message = successCount == 0
                        ? "Failed to delete collection via API on any node"
                        : $"Collection '{request.CollectionName}' deleted via API on {successCount}/{results.Count} nodes",
                    Results = nodeResults
                };

                return successCount > 0 ? Ok(response) : StatusCode(500, response);
            }
            else // Disk
            {
                // Validate Pods
                if (request.Pods == null || request.Pods.Count == 0)
                {
                    return BadRequest(new V1DeleteCollectionResponse
                    {
                        Success = false,
                        Message = "Pods list is required for disk deletion",
                        Results = new Dictionary<string, NodeDeletionResult>()
                    });
                }

                var pods = request.Pods.Select(p => (p.PodName, p.PodNamespace));
                results = await clusterManager.DeleteCollectionFromDiskAsync(
                    request.CollectionName,
                    pods,
                    request.DeleteSnapshots,
                    cancellationToken);

                var successCount = results.Values.Count(s => s);
                var nodeResults = results.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new NodeDeletionResult
                    {
                        Success = kvp.Value,
                        Error = kvp.Value ? null : "Deletion failed"
                    });

                var response = new V1DeleteCollectionResponse
                {
                    Success = successCount > 0,
                    Message = successCount == 0
                        ? "Failed to delete collection from disk on any pod"
                        : $"Collection '{request.CollectionName}' deleted from disk on {successCount}/{results.Count} pods",
                    Results = nodeResults
                };

                return successCount > 0 ? Ok(response) : StatusCode(500, response);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during collection deletion");
            return StatusCode(500, new V1DeleteCollectionResponse
            {
                Success = false,
                Message = "Internal server error during collection deletion",
                Results = new Dictionary<string, NodeDeletionResult>
                {
                    ["error"] = new NodeDeletionResult
                    {
                        Success = false,
                        Error = ex.Message
                    }
                }
            });
        }
    }

    [HttpPost("alias")]
    [ProducesResponseType(typeof(V1SetCollectionAliasResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1SetCollectionAliasResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1SetCollectionAliasResponse>> SetCollectionAlias(
        [FromBody] V1SetCollectionAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await clusterManager.SetCollectionAliasAsync(
                request.CollectionName,
                request.AliasName,
                cancellationToken);

            if (success)
            {
                return Ok(new V1SetCollectionAliasResponse
                {
                    Success = true,
                    Message = $"Alias '{request.AliasName}' set for collection '{request.CollectionName}'"
                });
            }

            return StatusCode(500, new V1SetCollectionAliasResponse
            {
                Success = false,
                Message = "Failed to set alias (no healthy node or Qdrant API error)"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting alias {AliasName} for collection {CollectionName}", request.AliasName, request.CollectionName);
            return StatusCode(500, new V1SetCollectionAliasResponse
            {
                Success = false,
                Message = "Internal server error during set alias",
                Error = ex.Message
            });
        }
    }

    [HttpPost("alias/rename")]
    [ProducesResponseType(typeof(V1RenameCollectionAliasResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RenameCollectionAliasResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RenameCollectionAliasResponse>> RenameCollectionAlias(
        [FromBody] V1RenameCollectionAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await clusterManager.RenameCollectionAliasAsync(
                request.OldAliasName,
                request.NewAliasName,
                cancellationToken);

            if (success)
            {
                return Ok(new V1RenameCollectionAliasResponse
                {
                    Success = true,
                    Message = $"Alias renamed from '{request.OldAliasName}' to '{request.NewAliasName}'"
                });
            }

            return StatusCode(500, new V1RenameCollectionAliasResponse
            {
                Success = false,
                Message = "Failed to rename alias (no healthy node or Qdrant API error)"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error renaming alias {OldAliasName} to {NewAliasName}", request.OldAliasName, request.NewAliasName);
            return StatusCode(500, new V1RenameCollectionAliasResponse
            {
                Success = false,
                Message = "Internal server error during rename alias",
                Error = ex.Message
            });
        }
    }

    [HttpPost("alias/delete")]
    [ProducesResponseType(typeof(V1DeleteCollectionAliasResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1DeleteCollectionAliasResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1DeleteCollectionAliasResponse>> DeleteCollectionAlias(
        [FromBody] V1DeleteCollectionAliasRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await clusterManager.DeleteCollectionAliasAsync(
                request.AliasName,
                cancellationToken);

            if (success)
            {
                return Ok(new V1DeleteCollectionAliasResponse
                {
                    Success = true,
                    Message = $"Alias '{request.AliasName}' deleted"
                });
            }

            return StatusCode(500, new V1DeleteCollectionAliasResponse
            {
                Success = false,
                Message = "Failed to delete alias (no healthy node or Qdrant API error)"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting alias {AliasName}", request.AliasName);
            return StatusCode(500, new V1DeleteCollectionAliasResponse
            {
                Success = false,
                Message = "Internal server error during delete alias",
                Error = ex.Message
            });
        }
    }

    [HttpPost("restore-replication-factor")]
    [ProducesResponseType(typeof(V1RestoreReplicationFactorResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(V1RestoreReplicationFactorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(V1RestoreReplicationFactorResponse), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<V1RestoreReplicationFactorResponse>> RestoreReplicationFactor(
        [FromBody] V1RestoreReplicationFactorRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var transferMethod = request.ShardTransferMethod.TryParseEnum<ShardTransferMethod>();
            var result = await restoreReplicationFactorJobService.RequestRestoreReplicationFactorAsync(
                request.CollectionName,
                transferMethod,
                timeout: null,
                cancellationToken);

            if (result.AlreadyInProgress)
            {
                return Conflict(new V1RestoreReplicationFactorResponse
                {
                    Status = "AlreadyInProgress",
                    Message = result.Message
                });
            }

            if (result.ApiError)
            {
                return StatusCode(500, new V1RestoreReplicationFactorResponse
                {
                    Status = "Error",
                    Message = result.Message
                });
            }

            return Accepted(new V1RestoreReplicationFactorResponse
            {
                Status = "Started",
                Message = result.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting restore replication factor for collection {CollectionName}", request.CollectionName);
            return StatusCode(500, new V1RestoreReplicationFactorResponse
            {
                Status = "Error",
                Message = "Internal server error",
                RemainingSteps = null
            });
        }
    }

    [HttpPost("restore-replication-factor/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelRestoreReplicationFactor(
        [FromBody] V1RestoreReplicationFactorCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cancelled = await restoreReplicationFactorJobService.CancelJobAsync(request.CollectionName, cancellationToken);
            return Ok(new { message = cancelled ? "Cancellation requested for " + request.CollectionName : "No running operation found for " + request.CollectionName });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling restore replication factor for collection {CollectionName}", request.CollectionName);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

