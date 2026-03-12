namespace Vigilante.Models.Responses;

/// <summary>
/// Response for restore replication factor start request.
/// </summary>
public class V1RestoreReplicationFactorResponse
{
    /// <summary>
    /// Status: "NotRequired" when collection does not need restore, "Started" when background process was started.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable message for UI/toast.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional: remaining replication steps (for future use when ShardReplicator exposes this).
    /// </summary>
    public IReadOnlyList<object>? RemainingSteps { get; set; }
}
