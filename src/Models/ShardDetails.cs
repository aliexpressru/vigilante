using Aer.QdrantClient.Http.Models.Shared;
using Vigilante.Extensions;

namespace Vigilante.Models;

/// <summary>
/// Detailed information about a shard including ID, state, and size
/// </summary>
public class ShardDetails
{
    private ShardState? ParsedState =>
        Enum.TryParse<ShardState>(State, ignoreCase: true, out var parsed) ? parsed : null;

    public required uint ShardId { get; set; }

    public string? State { get; set; }

    public bool IsActive => ParsedState is ShardState.Active or ShardState.ActiveRead;

    public bool IsEmpty { get; set; }

    /// <summary>Estimated vectors size in bytes (from cluster telemetry).</summary>
    public long? VectorsSizeBytes { get; set; }

    public string? PrettyVectorsSize => VectorsSizeBytes?.ToPrettySize();

    /// <summary>Estimated payloads size in bytes (from cluster telemetry).</summary>
    public long? PayloadsSizeBytes { get; set; }

    public string? PrettyPayloadsSize => PayloadsSizeBytes?.ToPrettySize();
}
