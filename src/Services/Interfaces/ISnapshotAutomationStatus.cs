namespace Vigilante.Services.Interfaces;

/// <summary>
/// Tracks snapshot-automation runs for UI: the job is removed from <see cref="IJobRegistry"/> when idle,
/// so this singleton keeps last state visible on the dashboard.
/// </summary>
public interface ISnapshotAutomationStatus
{
    void BeginRun();

    void EndRun(bool success);

    /// <summary>Short note for the last run summary (e.g. snapshot created, orphan deleted).</summary>
    void AppendRunNote(string note);

    void SetCurrentAction(string? action);

    IReadOnlyDictionary<string, object?> GetDisplayMetadata();
}
