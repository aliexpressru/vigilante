using Vigilante.Models.Enums;

namespace Vigilante.Models.Requests;

/// <summary>
/// Request to download a snapshot
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
    /// Type of download operation (API or Disk)
    /// </summary>
    public required SnapshotDownloadType DownloadType { get; set; }
    
    /// <summary>
    /// Node URL for API download (required when DownloadType = Api)
    /// </summary>
    public string? NodeUrl { get; set; }
    
    /// <summary>
    /// Pod name for disk download (required when DownloadType = Disk)
    /// </summary>
    public string? PodName { get; set; }
    
    /// <summary>
    /// Pod namespace for disk download (required when DownloadType = Disk)
    /// </summary>
    public string? PodNamespace { get; set; }
}

