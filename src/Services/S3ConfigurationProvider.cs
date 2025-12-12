using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Service for loading S3 configuration from Kubernetes secrets or appsettings
/// </summary>
public class S3ConfigurationProvider(
    IKubernetes? kubernetes,
    IOptions<QdrantOptions> options,
    ILogger<S3ConfigurationProvider> logger) : IS3ConfigurationProvider
{
    private readonly QdrantOptions _options = options.Value;
    private S3Options? _cachedConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Gets S3 configuration combining Kubernetes secret and appsettings
    /// Secrets (EndpointUrl, AccessKey, SecretKey): K8s Secret > appsettings
    /// Other settings (BucketName, Region, UsePathStyle): appsettings only
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

            // Start with appsettings as base (contains non-secret settings)
            var config = new S3Options
            {
                BucketName = _options.S3?.BucketName,
                Region = _options.S3?.Region,
                UsePathStyle = _options.S3?.UsePathStyle ?? true
            };

            // Try to load secrets from Kubernetes secret
            var secretData = await TryLoadSecretsFromKubernetesAsync(namespaceParameter, cancellationToken);
            if (secretData != null)
            {
                logger.LogInformation("S3 secrets loaded from Kubernetes secret '{SecretName}'", S3Constants.SecretName);
                config.EndpointUrl = secretData.EndpointUrl;
                config.AccessKey = secretData.AccessKey;
                config.SecretKey = secretData.SecretKey;
            }
            else
            {
                // Fallback to appsettings for secrets
                logger.LogInformation("S3 secrets loaded from appsettings");
                // Trim credentials to remove any whitespace
                config.EndpointUrl = _options.S3?.EndpointUrl?.Trim();
                config.AccessKey = _options.S3?.AccessKey?.Trim();
                config.SecretKey = _options.S3?.SecretKey?.Trim();
                
                // Log non-sensitive config info for debugging
                logger.LogInformation("S3 config from appsettings - EndpointUrl: {EndpointUrl}, AccessKey: {AccessKeyPrefix}*** (length: {AccessKeyLength}), SecretKey: {SecretKeyPrefix}*** (length: {SecretKeyLength})", 
                    config.EndpointUrl, 
                    config.AccessKey?.Length > 4 ? config.AccessKey.Substring(0, 4) : "???",
                    config.AccessKey?.Length ?? 0,
                    config.SecretKey?.Length > 4 ? config.SecretKey.Substring(0, 4) : "???",
                    config.SecretKey?.Length ?? 0);
            }

            if (!config.IsConfigured())
            {
                logger.LogWarning("S3 configuration is incomplete");
                return null;
            }

            _cachedConfig = config;
            return _cachedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<S3SecretData?> TryLoadSecretsFromKubernetesAsync(
        string? namespaceParameter,
        CancellationToken cancellationToken)
    {
        if (kubernetes == null)
        {
            logger.LogDebug("Kubernetes client not available, skipping secret lookup");
            return null;
        }

        try
        {
            var ns = namespaceParameter ?? "qdrant";
            
            logger.LogDebug("Attempting to load S3 credentials from secret '{SecretName}' in namespace '{Namespace}'", 
                S3Constants.SecretName, ns);

            var secret = await kubernetes.CoreV1.ReadNamespacedSecretAsync(
                S3Constants.SecretName,
                ns,
                cancellationToken: cancellationToken);

            if (secret?.Data == null)
            {
                logger.LogWarning("Secret '{SecretName}' found but has no data", S3Constants.SecretName);
                return null;
            }

            var endpointUrl = GetSecretValue(secret, S3Constants.EndpointUrlField);
            var accessKey = GetSecretValue(secret, S3Constants.AccessKeyField);
            var secretKey = GetSecretValue(secret, S3Constants.SecretKeyField);

            // All three secret fields must be present
            if (string.IsNullOrEmpty(endpointUrl) || 
                string.IsNullOrEmpty(accessKey) || 
                string.IsNullOrEmpty(secretKey))
            {
                logger.LogWarning("Secret '{SecretName}' is missing required fields (endpoint-url, access-key, or secret-key)", 
                    S3Constants.SecretName);
                return null;
            }

            return new S3SecretData
            {
                EndpointUrl = endpointUrl,
                AccessKey = accessKey,
                SecretKey = secretKey
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load S3 credentials from Kubernetes secret '{SecretName}'", S3Constants.SecretName);
            return null;
        }
    }

    /// <summary>
    /// Internal DTO for secret data loaded from Kubernetes
    /// </summary>
    private class S3SecretData
    {
        public string EndpointUrl { get; init; } = string.Empty;
        public string AccessKey { get; init; } = string.Empty;
        public string SecretKey { get; init; } = string.Empty;
    }

    private static string? GetSecretValue(V1Secret secret, string key)
    {
        if (secret.Data == null || !secret.Data.TryGetValue(key, out var value))
        {
            return null;
        }

        return System.Text.Encoding.UTF8.GetString(value);
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

