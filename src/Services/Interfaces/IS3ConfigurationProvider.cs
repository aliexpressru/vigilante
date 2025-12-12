using Vigilante.Configuration;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Interface for S3 configuration provider
/// </summary>
public interface IS3ConfigurationProvider
{
    /// <summary>
    /// Gets S3 configuration combining Kubernetes secret and appsettings
    /// Secrets (EndpointUrl, AccessKey, SecretKey): K8s Secret > appsettings
    /// Other settings (BucketName, Region, UsePathStyle): appsettings only
    /// </summary>
    Task<S3Options?> GetS3ConfigurationAsync(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached configuration, forcing reload on next request
    /// </summary>
    void InvalidateCache();
}

