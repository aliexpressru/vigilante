using Microsoft.Extensions.Options;
using Vigilante.Configuration;
using Vigilante.Constants;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services.Snapshots;

/// <summary>
/// Service for loading S3 configuration.
///
/// Secret fields (EndpointUrl, AccessKey, SecretKey) come from environment variables (Kubernetes secrets)
/// with fallback to appsettings. They are cached after first load since they never change without a restart.
///
/// Non-secret fields (Enabled, BucketName, Region) come from DynamicConfig (managed via ConfigMap/API)
/// and are always read fresh so they reflect live configuration changes.
/// </summary>
public class S3ConfigurationProvider(
    IOptions<QdrantOptions> options,
    IDynamicConfigService dynamicConfigService,
    ILogger<S3ConfigurationProvider> logger) : IS3ConfigurationProvider
{
    private readonly QdrantOptions _options = options.Value;

    // Only the secret fields are cached — they never change without a pod restart
    private string? _cachedEndpointUrl;
    private string? _cachedAccessKey;
    private string? _cachedSecretKey;
    private bool _secretsCached;
    private readonly SemaphoreSlim _secretsLock = new(1, 1);

    public async Task<S3Options?> GetS3ConfigurationAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
    {
        // Load secrets once (they require a pod restart to change)
        await EnsureSecretsCachedAsync(cancellationToken);

        // Dynamic fields — always read from DynamicConfig so changes apply immediately
        var dynamicConfig = await dynamicConfigService.GetConfigAsync(cancellationToken);
        var s3Dynamic = dynamicConfig.S3;

        if (!s3Dynamic.Enabled)
        {
            logger.LogInformation("S3 storage is disabled via dynamic configuration");
            return null;
        }

        var bucketName = s3Dynamic.BucketName?.Trim();
        var region = string.IsNullOrWhiteSpace(s3Dynamic.Region) ? "default" : s3Dynamic.Region.Trim();

        var config = new S3Options
        {
            Enabled = true,
            EndpointUrl = _cachedEndpointUrl,
            AccessKey = _cachedAccessKey,
            SecretKey = _cachedSecretKey,
            BucketName = bucketName,
            Region = region
        };

        if (!config.IsConfigured())
        {
            return null;
        }

        return config;
    }

    public void InvalidateCache()
    {
        _secretsCached = false;
        _cachedEndpointUrl = null;
        _cachedAccessKey = null;
        _cachedSecretKey = null;
        logger.LogInformation("S3 configuration secrets cache invalidated");
    }

    private async Task EnsureSecretsCachedAsync(CancellationToken cancellationToken)
    {
        if (_secretsCached)
        {
            return;
        }

        await _secretsLock.WaitAsync(cancellationToken);
        try
        {
            if (_secretsCached)
            {
                return;
            }

            var envEndpoint = Environment.GetEnvironmentVariable(S3Constants.EnvEndpointUrl);
            var envAccessKey = Environment.GetEnvironmentVariable(S3Constants.EnvAccessKey);
            var envSecretKey = Environment.GetEnvironmentVariable(S3Constants.EnvSecretKey);

            _cachedEndpointUrl = !string.IsNullOrWhiteSpace(envEndpoint)
                ? envEndpoint.Trim()
                : _options.S3?.EndpointUrl?.Trim();

            _cachedAccessKey = !string.IsNullOrWhiteSpace(envAccessKey)
                ? envAccessKey.Trim()
                : _options.S3?.AccessKey?.Trim();

            _cachedSecretKey = !string.IsNullOrWhiteSpace(envSecretKey)
                ? envSecretKey.Trim()
                : _options.S3?.SecretKey?.Trim();

            var endpointSource = !string.IsNullOrWhiteSpace(envEndpoint) ? "environment" : "appsettings";
            var credentialsSource = !string.IsNullOrWhiteSpace(envAccessKey) && !string.IsNullOrWhiteSpace(envSecretKey)
                ? "environment"
                : "appsettings";

            logger.LogInformation(
                "S3 secrets loaded — EndpointUrl source: {EndpointSource}, Credentials source: {CredentialsSource}",
                endpointSource, credentialsSource);

            _secretsCached = true;
        }
        finally
        {
            _secretsLock.Release();
        }
    }
}
