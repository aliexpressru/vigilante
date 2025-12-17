using Microsoft.AspNetCore.Mvc;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/collections")]
public class CollectionsController(
    IClusterManager clusterManager,
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
            var collectionGroups = result
                .GroupBy(size => size.CollectionName)
                .Select(group => new
                {
                    CollectionName = group.Key,
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
                            Issues = size.Issues
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
            if (request.SingleNode)
            {
                // Delete on specific node
                if (request.DeletionType == Models.Enums.CollectionDeletionType.Api)
                {
                    var success = await clusterManager.DeleteCollectionViaApiAsync(
                        request.NodeUrl!, 
                        request.CollectionName, 
                        cancellationToken);
                    
                    var response = new V1DeleteCollectionResponse
                    {
                        Success = success,
                        Message = success 
                            ? $"Collection '{request.CollectionName}' deleted successfully via API on {request.NodeUrl}"
                            : $"Failed to delete collection '{request.CollectionName}' via API on {request.NodeUrl}",
                        Results = new Dictionary<string, NodeDeletionResult>
                        {
                            [request.NodeUrl!] = new NodeDeletionResult 
                            { 
                                Success = success,
                                Error = success ? null : "Deletion failed"
                            }
                        }
                    };
                    
                    return success ? Ok(response) : StatusCode(500, response);
                }
                else // Disk
                {
                    var success = await clusterManager.DeleteCollectionFromDiskAsync(
                        request.PodName!, 
                        request.PodNamespace!, 
                        request.CollectionName, 
                        cancellationToken);
                    
                    var response = new V1DeleteCollectionResponse
                    {
                        Success = success,
                        Message = success 
                            ? $"Collection '{request.CollectionName}' deleted successfully from disk on pod {request.PodName}"
                            : $"Failed to delete collection '{request.CollectionName}' from disk on pod {request.PodName}",
                        Results = new Dictionary<string, NodeDeletionResult>
                        {
                            [request.PodName!] = new NodeDeletionResult 
                            { 
                                Success = success,
                                Error = success ? null : "Deletion failed"
                            }
                        }
                    };
                    
                    return success ? Ok(response) : StatusCode(500, response);
                }
            }
            else
            {
                // Delete on all nodes
                Dictionary<string, bool> results;
                
                if (request.DeletionType == Models.Enums.CollectionDeletionType.Api)
                {
                    results = await clusterManager.DeleteCollectionViaApiOnAllNodesAsync(
                        request.CollectionName, 
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
                    results = await clusterManager.DeleteCollectionFromDiskOnAllNodesAsync(
                        request.CollectionName, 
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
}

