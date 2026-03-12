namespace Vigilante.Models;

/// <summary>
/// Result of starting restore replication factor (sync part).
/// </summary>
public sealed record RestoreReplicationFactorStartResult(
    bool ApiError,
    bool AlreadyInProgress,
    string Message
);
