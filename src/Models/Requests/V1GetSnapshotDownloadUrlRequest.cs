namespace Vigilante.Models.Requests;

/// <summary>
/// Request for getting a download URL for a snapshot from S3
/// </summary>
public class V1GetSnapshotDownloadUrlRequest
{
    /// <summary>
    /// Name of the collection
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the snapshot
    /// </summary>
    public string SnapshotName { get; set; } = string.Empty;
    
    /// <summary>
    /// Expiration time in seconds for the presigned URL (default: 3600)
    /// </summary>
    public int ExpirationSeconds { get; set; } = 3600;
}

