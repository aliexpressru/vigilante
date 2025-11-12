namespace Vigilante.Models.Requests;

/// <summary>
/// Request to download a snapshot directly from disk (debug endpoint)
/// </summary>
public class V1DownloadSnapshotFromDiskRequest
{
    /// <summary>
    /// Name of the collection
    /// </summary>
    public required string CollectionName { get; set; }
    
    /// <summary>
    /// Name of the snapshot to download
    /// </summary>
    public required string SnapshotName { get; set; }
    
    /// <summary>
    /// Pod name for disk download
    /// </summary>
    public required string PodName { get; set; }
    
    /// <summary>
    /// Pod namespace for disk download
    /// </summary>
    public required string PodNamespace { get; set; }
}

