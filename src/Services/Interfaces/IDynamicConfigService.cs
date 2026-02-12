using Vigilante.Models;

namespace Vigilante.Services.Interfaces;

/// <summary>
/// Service for managing dynamic configuration stored in Kubernetes Endpoints annotations
/// Configuration changes are applied immediately without pod restart
/// </summary>
public interface IDynamicConfigService
{
    /// <summary>
    /// Event raised when configuration is updated
    /// </summary>
    event EventHandler<DynamicConfig>? ConfigChanged;
    
    /// <summary>
    /// Get current dynamic configuration
    /// </summary>
    Task<DynamicConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Update dynamic configuration
    /// </summary>
    Task UpdateConfigAsync(DynamicConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Start watching for configuration file changes using FileSystemWatcher
    /// Monitors the mounted ConfigMap volume for updates
    /// </summary>
    Task StartWatchingAsync(CancellationToken cancellationToken = default);
}
