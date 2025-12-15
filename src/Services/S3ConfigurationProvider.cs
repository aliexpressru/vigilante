using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Service for loading S3 configuration with explicit environment variable support
/// 
/// Configuration sources (in priority order):
/// 1. Environment variables (Kubernetes secrets) - see S3Constants.Env* constants:
///    - S3Constants.EnvEndpointUrl (S3__EndpointUrl)
///    - S3Constants.EnvAccessKey (S3__AccessKey)
///    - S3Constants.EnvSecretKey (S3__SecretKey)
/// 2. appsettings.json / appsettings.{Environment}.json:
///    - All S3 settings including BucketName and Region
///    - Can also contain secrets for local development
/// 
/// In Kubernetes deployment.yaml, secrets are mounted as environment variables:
/// env:
/// - name: S3__EndpointUrl
///   valueFrom:
///     secretKeyRef:
///       name: qdrant-s3-credentials
///       key: endpoint-url
/// </summary>
public class S3ConfigurationProvider(
    IOptions<QdrantOptions> options,
    ILogger<S3ConfigurationProvider> logger) : IS3ConfigurationProvider
{
    private readonly QdrantOptions _options = options.Value;
    private S3Options? _cachedConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Gets S3 configuration from appsettings
    /// In Kubernetes, secrets are automatically injected via environment variables
    /// </summary>
    public async Task<S3Options?> GetS3ConfigurationAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        // Return cached config if available
        if (_cachedConfig != null)
        {
            return _cachedConfig;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            // Load secrets from environment variables (Kubernetes) with fallback to appsettings
            // Priority for secrets (EndpointUrl, AccessKey, SecretKey): Environment variables > appsettings
            // Priority for non-secrets (BucketName, Region): ConfigMap (appsettings) ONLY
            
            var envEndpoint = Environment.GetEnvironmentVariable(S3Constants.EnvEndpointUrl);
            var envAccessKey = Environment.GetEnvironmentVariable(S3Constants.EnvAccessKey);
            var envSecretKey = Environment.GetEnvironmentVariable(S3Constants.EnvSecretKey);
            
            // Use environment variables for secrets if available, otherwise fall back to appsettings
            var endpointUrl = !string.IsNullOrWhiteSpace(envEndpoint) 
                ? envEndpoint.Trim() 
                : _options.S3?.EndpointUrl?.Trim();
            
            var accessKey = !string.IsNullOrWhiteSpace(envAccessKey) 
                ? envAccessKey.Trim() 
                : _options.S3?.AccessKey?.Trim();
            
            var secretKey = !string.IsNullOrWhiteSpace(envSecretKey) 
                ? envSecretKey.Trim() 
                : _options.S3?.SecretKey?.Trim();
            
            // BucketName, Region, and Enabled ALWAYS come from ConfigMap (appsettings), never from environment
            var enabled = _options.S3?.Enabled ?? true; // Default to true for backward compatibility
            var bucketName = _options.S3?.BucketName?.Trim();
            var region = _options.S3?.Region?.Trim();
            
            // Check if S3 is disabled via feature flag
            if (!enabled)
            {
                logger.LogInformation("S3 storage is disabled via configuration (Enabled=false)");
                return null;
            }
            
            // Log where each parameter came from
            var endpointSource = !string.IsNullOrWhiteSpace(envEndpoint) ? "environment" : "appsettings";
            var credentialsSource = !string.IsNullOrWhiteSpace(envAccessKey) && !string.IsNullOrWhiteSpace(envSecretKey) 
                ? "environment" 
                : "appsettings";
            
            logger.LogInformation("Loading S3 configuration - Enabled: true, EndpointUrl: {EndpointSource}, Credentials: {CredentialsSource}, BucketName: configmap, Region: configmap",
                endpointSource, credentialsSource);
            
            var config = new S3Options
            {
                Enabled = enabled,
                EndpointUrl = endpointUrl,
                AccessKey = accessKey,
                SecretKey = secretKey,
                BucketName = bucketName,
                Region = region
            };

            // Log configuration status (without exposing secrets)
            if (!config.IsConfigured())
            {
                logger.LogWarning("S3 configuration is incomplete - Enabled: {Enabled}, EndpointUrl: {HasEndpoint}, AccessKey: {HasAccessKey}, SecretKey: {HasSecretKey}, BucketName: {HasBucket}",
                    config.Enabled,
                    !string.IsNullOrEmpty(config.EndpointUrl),
                    !string.IsNullOrEmpty(config.AccessKey),
                    !string.IsNullOrEmpty(config.SecretKey),
                    !string.IsNullOrEmpty(config.BucketName));
                return null;
            }

            logger.LogInformation("S3 configuration loaded successfully - EndpointUrl: {EndpointUrl}, BucketName: {BucketName}, Region: {Region}",
                config.EndpointUrl,
                config.BucketName,
                config.Region ?? "default");

            _cachedConfig = config;
            return _cachedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }


    /// <summary>
    /// Invalidates cached configuration, forcing reload on next request
    /// </summary>
    public void InvalidateCache()
    {
        _cachedConfig = null;
        logger.LogInformation("S3 configuration cache invalidated");
    }
}

