namespace Vigilante.Configuration;

/// <summary>
/// Configuration for S3-compatible storage for Qdrant snapshots
/// </summary>
public class S3Options
{
    /// <summary>
    /// Whether S3 storage is enabled (feature flag)
    /// </summary>
    public bool Enabled { get; set; } = true;
    
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
    /// Checks if S3 is properly configured and enabled
    /// </summary>
    public bool IsConfigured() =>
        Enabled &&
        !string.IsNullOrEmpty(EndpointUrl) &&
        !string.IsNullOrEmpty(AccessKey) &&
        !string.IsNullOrEmpty(SecretKey) &&
        !string.IsNullOrEmpty(BucketName);
}

