using Aer.QdrantClient.Http.Models.Shared;
using Microsoft.AspNetCore.Mvc;
using Vigilante.Extensions;
using Vigilante.Models;
using Vigilante.Models.Requests;
using Vigilante.Services.Interfaces;

namespace Vigilante.Controllers;

[ApiController]
[Route("api/v1/cluster")]
public class ClusterController(
    IClusterManager clusterManager,
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
            // Parse ShardTransferMethod if provided using extension method
            var transferMethod = request.ShardTransferMethod.TryParseEnum<Aer.QdrantClient.Http.Models.Shared.ShardTransferMethod>();

            var success = await clusterManager.ReplicateShardsAsync(
                request.SourcePeerId!.Value,
                request.TargetPeerId!.Value,
                request.CollectionName,
                request.ShardIdsToReplicate,
                request.IsMoveShards,
                transferMethod,
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

    [HttpPost("drop-shards")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DropShardsFromPeer(
        [FromBody] V1DropShardsFromPeerRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await clusterManager.DropShardsFromPeerAsync(
                request.CollectionName,
                request.PeerId!.Value,
                request.ShardIds,
                request.IsDryRun,
                cancellationToken);

            if (success)
            {
                return Ok(new { message = "Shard drop operation completed successfully" });
            }

            return StatusCode(500, new { error = "Failed to drop shards from peer" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during shard drop operation");
            return StatusCode(500, new { error = "Internal server error during shard drop", details = ex.Message });
        }
    }

    [HttpPost("start-resharding")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> StartResharding(
        [FromBody] V1StartReshardingRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse ReshardingOperationDirection using extension method
            var direction = request.Direction.TryParseEnum<ReshardingOperationDirection>();

            if (!direction.HasValue)
            {
                return BadRequest(new { error = "Invalid direction value. Must be 'Up' or 'Down'." });
            }

            var success = await clusterManager.StartReshardingAsync(
                request.CollectionName,
                direction.Value,
                request.PeerId,
                cancellationToken);

            if (success)
            {
                return Ok(new { message = "Resharding operation started successfully" });
            }

            return StatusCode(500, new { error = "Failed to start resharding operation" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during resharding operation");
            return StatusCode(500, new { error = "Internal server error during resharding", details = ex.Message });
        }
    }
}
