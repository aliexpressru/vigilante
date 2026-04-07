namespace Vigilante.Models.Requests;

/// <summary>
/// Unified request for collection recovery from snapshot name or snapshot URL.
/// </summary>
public class V1RecoverRequest
{
    public string CollectionName { get; set; } = string.Empty;

    public string TargetNodeUrl { get; set; } = string.Empty;

    public string? SnapshotName { get; set; }

    public string? SourceCollectionName { get; set; }

    public string? Source { get; set; }

    public string? SnapshotUrl { get; set; }

    public string? SnapshotChecksum { get; set; }

    public string? SnapshotPriority { get; set; }

    public bool WaitForResult { get; set; } = true;
}