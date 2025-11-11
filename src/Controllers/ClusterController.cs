using Microsoft.AspNetCore.Mvc;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Models.Responses;
using Vigilante.Services;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/cluster")]
public class ClusterController(
    ClusterManager clusterManager,
    ILogger<ClusterController> logger)
    : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(ClusterState), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ClusterState>> GetClusterStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await clusterManager.GetClusterStateAsync(cancellationToken);

            return Ok(state);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get cluster status");

            return StatusCode(500, new { error = "Failed to get cluster status", details = ex.Message });
        }
    }
    
    [HttpGet("collections-info")]
    public async Task<ActionResult<V1GetCollectionsInfoResponse>> GetCollectionsInfo(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusterManager.GetCollectionsInfoAsync(cancellationToken);

            // Collect all issues from collections into a general issues array
            var allIssues = new List<string>();
            
            var collections = result
                .Select(size =>
                {
                    // Add formatted issues for collections with problems
                    if (size.Issues.Count > 0)
                    {
                        foreach (var issue in size.Issues)
                        {
                            allIssues.Add($"[{size.CollectionName}@{size.PodName}] {issue}");
                        }
                    }
                    
                    return new V1GetCollectionsInfoResponse.CollectionInfo
                    {
                        PodName = size.PodName,
                        NodeUrl = size.NodeUrl,
                        PeerId = size.PeerId,
                        CollectionName = size.CollectionName,
                        PodNamespace = size.PodNamespace,
                        Metrics = size.Metrics,
                        Issues = size.Issues
                    };
                })
                .OrderBy(x => x.CollectionName)
                .ToArray();

            return Ok(new V1GetCollectionsInfoResponse
            {
                Collections = collections,
                Issues = allIssues.ToArray()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to get collection info", details = ex.Message });
        }
    }

    [HttpPost("replicate-shards")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ReplicateShards(
        [FromBody] V1ReplicateShardsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await clusterManager.ReplicateShardsAsync(
                request.SourcePeerId!.Value,
                request.TargetPeerId!.Value,
                request.CollectionName,
                request.ShardIdsToReplicate,
                request.IsMoveShards,
                cancellationToken);

            if (success)
            {
                return Ok(new { message = "Shard replication initiated successfully" });
            }

            return StatusCode(500, new { error = "Failed to initiate shard replication" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during shard replication");
            return StatusCode(500, new { error = "Internal server error during shard replication", details = ex.Message });
        }
    }

    [HttpDelete("delete-collection")]
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

    [HttpPost("create-snapshot")]
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
                var snapshotName = await clusterManager.CreateCollectionSnapshotAsync(
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
                var results = await clusterManager.CreateCollectionSnapshotOnAllNodesAsync(
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

    [HttpDelete("delete-snapshot")]
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
                // Delete snapshot on specific node
                var success = await clusterManager.DeleteCollectionSnapshotAsync(
                    request.NodeUrl!,
                    request.CollectionName,
                    request.SnapshotName,
                    cancellationToken);

                var response = new V1DeleteSnapshotResponse
                {
                    Success = success,
                    Message = success
                        ? $"Snapshot '{request.SnapshotName}' deleted successfully for collection '{request.CollectionName}' on {request.NodeUrl}"
                        : $"Failed to delete snapshot '{request.SnapshotName}' for collection '{request.CollectionName}' on {request.NodeUrl}",
                    Results = new Dictionary<string, NodeSnapshotDeletionResult>
                    {
                        [request.NodeUrl!] = new NodeSnapshotDeletionResult
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
                // Delete snapshot on all nodes
                var results = await clusterManager.DeleteCollectionSnapshotOnAllNodesAsync(
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

    [HttpPost("download-snapshot")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DownloadSnapshot(
        [FromBody] V1DownloadSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Starting unified download for snapshot {SnapshotName} of collection {CollectionName}", 
                request.SnapshotName, request.CollectionName);

            Stream? snapshotStream = null;
            var downloadMethod = "unknown";

            // First, check if collection exists in Qdrant
            var collectionExists = await clusterManager.CheckCollectionExistsAsync(
                request.NodeUrl!,
                request.CollectionName,
                cancellationToken);

            if (collectionExists)
            {
                logger.LogInformation("Collection {CollectionName} exists, attempting download via API", request.CollectionName);
                
                // Try downloading via API
                try
                {
                    snapshotStream = await clusterManager.DownloadCollectionSnapshotAsync(
                        request.NodeUrl!,
                        request.CollectionName,
                        request.SnapshotName,
                        cancellationToken);

                    if (snapshotStream != null)
                    {
                        downloadMethod = "API";
                        logger.LogInformation("Successfully downloaded snapshot via API");
                    }
                }
                catch (Exception apiEx)
                {
                    logger.LogWarning(apiEx, "Failed to download via API, will try disk");
                    snapshotStream = null;
                }
            }
            else
            {
                logger.LogInformation("Collection {CollectionName} does not exist in Qdrant, will download from disk", 
                    request.CollectionName);
            }

            // If API download failed or collection doesn't exist, try downloading from disk
            if (snapshotStream == null)
            {
                logger.LogInformation("Attempting download from disk for snapshot {SnapshotName}", request.SnapshotName);
                
                snapshotStream = await clusterManager.DownloadSnapshotFromDiskAsync(
                    request.PodName!,
                    request.PodNamespace!,
                    request.CollectionName,
                    request.SnapshotName,
                    cancellationToken);

                if (snapshotStream != null)
                {
                    downloadMethod = "Disk";
                    logger.LogInformation("Successfully downloaded snapshot from disk");
                }
            }

            if (snapshotStream == null)
            {
                logger.LogError("Failed to download snapshot via both API and Disk");
                return StatusCode(500, new { error = "Failed to download snapshot via both API and Disk" });
            }

            logger.LogInformation("Returning snapshot {SnapshotName} downloaded via {Method}", 
                request.SnapshotName, downloadMethod);

            // Return the stream as a file download
            return File(snapshotStream, "application/octet-stream", request.SnapshotName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during snapshot download");
            return StatusCode(500, new { error = "Internal server error during snapshot download", details = ex.Message });
        }
    }

    [HttpPost("recover-from-snapshot")]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(V1RecoverFromSnapshotResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<V1RecoverFromSnapshotResponse>> RecoverFromSnapshot(
        [FromBody] V1RecoverFromSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var success = await clusterManager.RecoverCollectionFromSnapshotAsync(
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


    [HttpGet("snapshots-info")]
    public async Task<ActionResult<V1GetSnapshotsInfoResponse>> GetSnapshotsInfo(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await clusterManager.GetSnapshotsInfoAsync(cancellationToken);

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
                        PodNamespace = ""
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

    [HttpPost("snapshots/delete-from-disk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteSnapshotFromDisk(
        [FromBody] V1DeleteSnapshotFromDiskRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var success = await clusterManager.DeleteSnapshotFromDiskAsync(
                request.PodName!,
                request.PodNamespace!,
                request.CollectionName!,
                request.SnapshotName!,
                cancellationToken);

            var response = new V1DeleteSnapshotResponse
            {
                Success = success,
                Message = success
                    ? $"Snapshot '{request.SnapshotName}' for collection '{request.CollectionName}' deleted successfully from disk on pod {request.PodName}"
                    : $"Failed to delete snapshot '{request.SnapshotName}' from disk on pod {request.PodName}",
                Results = new Dictionary<string, NodeSnapshotDeletionResult>
                {
                    [request.PodName!] = new NodeSnapshotDeletionResult
                    {
                        Success = success,
                        Error = success ? null : "Deletion failed"
                    }
                }
            };

            return success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting snapshot from disk");
            return StatusCode(500, new V1DeleteSnapshotResponse
            {
                Success = false,
                Message = "Internal server error",
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

    [HttpPost("snapshots/recover-from-disk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RecoverFromDiskSnapshot(
        [FromBody] V1RecoverFromDiskSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Use existing RecoverCollectionFromSnapshotAsync since it works with disk snapshots
            var success = await clusterManager.RecoverCollectionFromSnapshotAsync(
                request.NodeUrl!,
                request.CollectionName!,
                request.SnapshotName!,
                cancellationToken);

            var response = new V1RecoverFromSnapshotResponse
            {
                Success = success,
                Message = success
                    ? $"Collection '{request.CollectionName}' recovery from snapshot '{request.SnapshotName}' initiated successfully on {request.NodeUrl}"
                    : $"Failed to recover collection '{request.CollectionName}' from snapshot '{request.SnapshotName}' on {request.NodeUrl}",
                Error = success ? null : "Recovery failed"
            };

            return success ? Ok(response) : StatusCode(500, response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error recovering from disk snapshot");
            return StatusCode(500, new V1RecoverFromSnapshotResponse
            {
                Success = false,
                Message = "Internal server error",
                Error = ex.Message
            });
        }
    }
}
