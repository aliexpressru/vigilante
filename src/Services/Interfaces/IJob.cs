namespace Vigilante.Services.Interfaces;

/// <summary>
/// Abstraction for a background job that is advanced each tick: wait for ready, then advance to next step.
/// Monitor calls CheckReadyAsync; when true calls OnReady; on next tick calls AdvanceAsync. No domain-specific details at this level.
/// </summary>
public interface IJob : IAsyncDisposable
{
    /// <summary>
    /// Unique key identifying this job (e.g. collection name). Used for removal and failure reporting.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// True: waiting for CheckReadyAsync to return true, do not call AdvanceAsync. False: safe to call AdvanceAsync.
    /// </summary>
    bool IsWaitingForReady { get; }

    /// <summary>
    /// Checks whether the job is ready to advance to the next step.
    /// </summary>
    Task<bool?> CheckReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Call after CheckReadyAsync returned true — clears the waiting flag; the next tick will call AdvanceAsync.
    /// </summary>
    void OnReady();

    /// <summary>
    /// Advance to the next step. Call only when !IsWaitingForReady.
    /// Returns (HasMore, Success, ErrorMessage): HasMore — whether there are more steps; Success — current step success; when Success==false — ErrorMessage.
    /// </summary>
    Task<(bool HasMore, bool Success, string? ErrorMessage)> AdvanceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Optional metadata for API display (e.g. ReplicationPlan for restore replication factor). Null if not supported or no data.
    /// </summary>
    IReadOnlyDictionary<string, object?>? GetMetadata();
}
