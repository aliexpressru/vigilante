namespace Vigilante.Models;

public sealed record SnapshotRecoveryStartResult(
    bool ApiError,
    bool AlreadyInProgress,
    string Message
);
