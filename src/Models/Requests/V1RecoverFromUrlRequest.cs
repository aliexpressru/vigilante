namespace Vigilante.Models.Requests;

/// <summary>
/// Request for recovering a collection from a snapshot URL (e.g., S3)
/// </summary>
public class V1RecoverFromUrlRequest
{
    /// <summary>
    /// URL of the node to perform recovery on
    /// </summary>
    public string NodeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Name of the collection to recover
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// URL of the snapshot location (e.g., S3 URL)
    /// </summary>
    public string SnapshotUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional snapshot checksum for verification
    /// </summary>
    public string? SnapshotChecksum { get; set; }

    /// <summary>
    /// Wait for operation to complete (default: true)
    /// </summary>
    public bool WaitForResult { get; set; } = true;
}

