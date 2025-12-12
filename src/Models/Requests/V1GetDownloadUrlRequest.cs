namespace Vigilante.Models.Requests;

/// <summary>
/// Request for getting a presigned download URL for an S3 snapshot
/// </summary>
public class V1GetDownloadUrlRequest
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
    /// URL expiration time in hours (default: 1)
    /// </summary>
    public int ExpirationHours { get; set; } = 1;
}

