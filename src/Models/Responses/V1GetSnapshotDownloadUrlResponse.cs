namespace Vigilante.Models.Responses;

/// <summary>
/// Response containing a presigned download URL for a snapshot
/// </summary>
public class V1GetSnapshotDownloadUrlResponse
{
    /// <summary>
    /// Presigned URL for downloading the snapshot
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Expiration time of the URL
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// Name of the snapshot
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the collection
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
}

