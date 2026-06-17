namespace Vigilante.Models.Snapshots;

public sealed record SnapshotRecoveryStartResult(
    bool ApiError,
    bool AlreadyInProgress,
    string Message
);
