using System.Text.Json;
using k8s;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Manages dynamic configuration stored in Kubernetes Endpoints annotations
/// Uses Kubernetes Watch API for immediate notification of changes
/// Configuration survives pod restarts and redeployments
/// </summary>
public class DynamicConfigService : IDynamicConfigService
{
    private readonly IKubernetesManager _kubernetesManager;
    private readonly ILogger<DynamicConfigService> _logger;
    private DynamicConfig _cachedConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Event raised when configuration is updated
    /// </summary>
    public event EventHandler<DynamicConfig>? ConfigChanged;

    public DynamicConfigService(
        IKubernetesManager kubernetesManager,
        ILogger<DynamicConfigService> logger)
    {
        _kubernetesManager = kubernetesManager;
        _logger = logger;
        _cachedConfig = new DynamicConfig(); // Default config
    }

    public async Task<DynamicConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var endpoints = await _kubernetesManager.GetOrCreateEndpointsAsync(
                KubernetesConstants.DynamicConfigEndpointsName,
                labels: new Dictionary<string, string>
                {
                    ["app"] = KubernetesConstants.VigilanteAppLabel,
                    ["managed-by"] = KubernetesConstants.ManagedByVigilanteLabel
                },
                annotations: new Dictionary<string, string>
                {
                    ["description"] = KubernetesConstants.DynamicConfigDescription
                },
                cancellationToken: cancellationToken);
            
            if (endpoints.Metadata?.Annotations?.TryGetValue(KubernetesConstants.DynamicConfigAnnotationKey, out var configJson) == true)
            {
                try
                {
                    var config = JsonSerializer.Deserialize<DynamicConfig>(configJson);
                    if (config != null)
                    {
                        _cachedConfig = config;
                        return config;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize dynamic config from annotation");
                }
            }

            // If no config exists, create default
            _logger.LogInformation("No dynamic config found, initializing with defaults");
            await UpdateConfigInternalAsync(_cachedConfig, cancellationToken);
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read dynamic config from Kubernetes Endpoints");
            return _cachedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateConfigAsync(DynamicConfig config, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await UpdateConfigInternalAsync(config, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task UpdateConfigInternalAsync(DynamicConfig config, CancellationToken cancellationToken)
    {
        // Always update cached config
        _cachedConfig = config;
        
        _logger.LogInformation(
            "Updated dynamic config: MonitoringIntervalSeconds={Interval}",
            config.MonitoringIntervalSeconds);
        
        // Raise event to notify subscribers
        ConfigChanged?.Invoke(this, config);
        
        // Only save to Kubernetes if we're running in cluster
        try
        {
            var configJson = JsonSerializer.Serialize(config);
            
            await _kubernetesManager.UpdateEndpointsAnnotationsAsync(
                KubernetesConstants.DynamicConfigEndpointsName,
                new Dictionary<string, string>
                {
                    [KubernetesConstants.DynamicConfigAnnotationKey] = configJson
                },
                cancellationToken: cancellationToken);
            
            _logger.LogInformation("Dynamic config saved to Kubernetes Endpoints");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save dynamic config to Kubernetes (not running in cluster or no permissions), config updated in memory only");
        }
    }

    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting watch for dynamic config changes in Endpoints");

        await _kubernetesManager.WatchEndpointsAsync(
            KubernetesConstants.DynamicConfigEndpointsName,
            (type, item) =>
            {
                if (type == WatchEventType.Added || type == WatchEventType.Modified)
                {
                    if (item.Metadata?.Annotations?.TryGetValue(KubernetesConstants.DynamicConfigAnnotationKey, out var configJson) == true)
                    {
                        try
                        {
                            var config = JsonSerializer.Deserialize<DynamicConfig>(configJson);
                            if (config != null && !config.Equals(_cachedConfig))
                            {
                                _cachedConfig = config;
                                _logger.LogInformation(
                                    "Detected config change from Kubernetes: MonitoringIntervalSeconds={Interval}",
                                    config.MonitoringIntervalSeconds);
                                
                                // Raise event to notify subscribers
                                ConfigChanged?.Invoke(this, config);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, "Failed to deserialize config change");
                        }
                    }
                }
            },
            cancellationToken: cancellationToken);
    }
}
