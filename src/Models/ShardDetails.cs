using Vigilante.Extensions;

namespace Vigilante.Models;

/// <summary>
/// Detailed information about a shard including ID, state, and size
/// </summary>
public class ShardDetails
{
    public required uint ShardId { get; set; }

    public string? State { get; set; }

    /// <summary>Physical size on disk (from storage).</summary>
    public long? SizeBytes { get; set; }

    public string? PrettySize => SizeBytes?.ToPrettySize();

    /// <summary>Estimated vectors size in bytes (from cluster telemetry).</summary>
    public long? VectorsSizeBytes { get; set; }

    public string? PrettyVectorsSize => VectorsSizeBytes?.ToPrettySize();

    /// <summary>Estimated payloads size in bytes (from cluster telemetry).</summary>
    public long? PayloadsSizeBytes { get; set; }

    public string? PrettyPayloadsSize => PayloadsSizeBytes?.ToPrettySize();
}
