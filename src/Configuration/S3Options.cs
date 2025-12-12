namespace Vigilante.Configuration;

/// <summary>
/// Configuration for S3-compatible storage for Qdrant snapshots
/// </summary>
public class S3Options
{
    /// <summary>
    /// S3 endpoint URL (e.g., https://s3.amazonaws.com)
    /// </summary>
    public string? EndpointUrl { get; set; }
    
    /// <summary>
    /// S3 access key
    /// </summary>
    public string? AccessKey { get; set; }
    
    /// <summary>
    /// S3 secret key
    /// </summary>
    public string? SecretKey { get; set; }
    
    /// <summary>
    /// S3 bucket name for snapshots
    /// </summary>
    public string? BucketName { get; set; }
    
    /// <summary>
    /// S3 region (optional, depends on provider)
    /// </summary>
    public string? Region { get; set; }
    
    /// <summary>
    /// Whether to use path-style addressing (required for MinIO and some S3-compatible services)
    /// </summary>
    public bool UsePathStyle { get; set; } = true;
    
    /// <summary>
    /// Checks if S3 is properly configured
    /// </summary>
    public bool IsConfigured() =>
        !string.IsNullOrEmpty(EndpointUrl) &&
        !string.IsNullOrEmpty(AccessKey) &&
        !string.IsNullOrEmpty(SecretKey) &&
        !string.IsNullOrEmpty(BucketName);
}

