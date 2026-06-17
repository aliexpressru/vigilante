namespace Vigilante.Services.Snapshots;

/// <summary>
/// Holds orphaned-snapshot state across snapshot-automation job runs (collection name -> first detected missing at).
/// Single instance shared by all SnapshotAutomationJob runs.
/// </summary>
public class SnapshotOrphanedState
{
    internal Dictionary<string, DateTime> OrphanedAt { get; } = [];
}
