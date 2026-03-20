namespace Vigilante.Models;

/// <summary>
/// Result of multi-node snapshot create (API + automation).
/// </summary>
public sealed class CreateCollectionSnapshotBatchResult
{
    public required Dictionary<string, string?> Results { get; init; }

    /// <summary>
    /// True when no Qdrant create calls were made: pending <c>snapshot-create-*</c> job or concurrent create for the same collection.
    /// </summary>
    public bool SkippedDuplicatePending { get; init; }

    public static CreateCollectionSnapshotBatchResult SkippedForNodes(IEnumerable<string> nodeUrls)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var u in nodeUrls)
            dict.TryAdd(u, null);
        return new CreateCollectionSnapshotBatchResult { Results = dict, SkippedDuplicatePending = true };
    }
}
