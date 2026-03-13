namespace Vigilante.Models;

/// <summary>
/// Current state of a background job for API display: key, optional error, optional metadata (e.g. ReplicationPlan).
/// </summary>
public sealed record JobInfoDto(
    string Key,
    string? ErrorMessage,
    DateTime? ErrorRecordedAt,
    IReadOnlyDictionary<string, object?>? Metadata
);
