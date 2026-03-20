namespace Vigilante.Models;

/// <summary>
/// Snapshot management configuration
/// </summary>
public class SnapshotConfiguration
{
    /// <summary>
    /// Timeout for waiting until async snapshot files become visible (used by snapshot-create-* jobs).
    /// </summary>
    public int PendingCreateTimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Delete snapshots when the collection is deleted via Vigilante. Default: true.
    /// </summary>
    public bool DeleteWithCollection { get; set; } = true;

    /// <summary>
    /// Delete orphaned snapshots (for collections deleted outside Vigilante) after this many minutes.
    /// null = disabled.
    /// </summary>
    public int? DeleteOrphanedAfterMinutes { get; set; }

    /// <summary>
    /// Global snapshot schedule settings.
    /// </summary>
    public Schedule Schedule { get; set; } = new();

    /// <summary>
    /// Per-collection schedule overrides. Key = collection name.
    /// When present for a collection, replaces the global Schedule entirely.
    /// </summary>
    public Dictionary<string, Schedule>? CollectionOverrides { get; set; }

    /// <summary>
    /// Returns the effective schedule for a specific collection.
    /// </summary>
    public Schedule GetEffectiveSchedule(string collectionName)
    {
        if (CollectionOverrides is not null && CollectionOverrides.TryGetValue(collectionName, out var overrides))
        {
            return overrides;
        }

        return Schedule;
    }
}

/// <summary>
/// Snapshot schedule settings (used globally and as per-collection override).
/// Enabled = true, IntervalMinutes = null  → one snapshot when collection first turns Green AND hnsw_config.m > 0.
/// Enabled = true, IntervalMinutes = N     → snapshot every N minutes while collection is Green.
/// </summary>
public class Schedule
{
    /// <summary>
    /// Enable automatic snapshot scheduling. Default: false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Periodic snapshot interval in minutes.
    /// null = take one snapshot when collection first turns Green AND hnsw_config.m > 0.
    /// </summary>
    public int? IntervalMinutes { get; set; }

    /// <summary>
    /// Keep only the last N snapshots per collection per node; delete older ones. null = keep all.
    /// </summary>
    public int? RetainLastN { get; set; }
}
