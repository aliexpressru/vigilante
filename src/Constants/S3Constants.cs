namespace Vigilante.Constants;

/// <summary>
/// Constants for S3 storage paths and configurations
/// </summary>
public static class S3Constants
{
    /// <summary>
    /// Root folder for snapshots in S3 bucket
    /// </summary>
    public const string SnapshotsFolder = "snapshots";
    
    /// <summary>
    /// Secret name in Kubernetes for S3 credentials
    /// </summary>
    public const string SecretName = "qdrant-s3-credentials";
    
    /// <summary>
    /// Secret field name for access key
    /// </summary>
    public const string AccessKeyField = "access-key";
    
    /// <summary>
    /// Secret field name for secret key
    /// </summary>
    public const string SecretKeyField = "secret-key";
    
    /// <summary>
    /// Secret field name for endpoint URL
    /// </summary>
    public const string EndpointUrlField = "endpoint-url";
    
    /// <summary>
    /// Default region for S3-compatible storage when not specified in configuration
    /// </summary>
    public const string DefaultRegion = "default";
    
    /// <summary>
    /// AWS Signature Version 4 algorithm name
    /// </summary>
    public const string SignatureAlgorithm = "AWS4-HMAC-SHA256";
    
    /// <summary>
    /// AWS service name for S3
    /// </summary>
    public const string ServiceName = "s3";
    
    /// <summary>
    /// AWS request type for signature version 4
    /// </summary>
    public const string RequestType = "aws4_request";
    
    /// <summary>
    /// Signed headers for presigned URLs
    /// </summary>
    public const string SignedHeaders = "host";
    
    /// <summary>
    /// Payload hash for presigned URLs (unsigned)
    /// </summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    
    /// <summary>
    /// HTTP method for presigned URLs
    /// </summary>
    public const string HttpMethodGet = "GET";
    
    /// <summary>
    /// AWS credential prefix
    /// </summary>
    public const string AwsSecretPrefix = "AWS4";
}


