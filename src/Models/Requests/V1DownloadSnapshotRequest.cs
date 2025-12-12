using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request to download a snapshot (unified endpoint that tries API first, then falls back to Disk)
/// </summary>
public class V1DownloadSnapshotRequest
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
    /// Node URL for download (required for Qdrant API/Disk sources)
    /// </summary>
    public string? NodeUrl { get; set; }
    
    /// <summary>
    /// Pod name for fallback disk download (required for Disk source)
    /// </summary>
    public string? PodName { get; set; }
    
    /// <summary>
    /// Pod namespace for fallback disk download (required for Disk source)
    /// </summary>
    public string? PodNamespace { get; set; }
    
    /// <summary>
    /// Source where the snapshot is stored
    /// </summary>
    public SnapshotSource Source { get; set; }
}

