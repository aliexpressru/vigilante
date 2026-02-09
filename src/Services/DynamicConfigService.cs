using System.Text.Json;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Manages dynamic configuration stored in Kubernetes ConfigMap mounted as volume
/// Uses FileSystemWatcher for immediate notification of changes
/// Configuration survives pod restarts and redeployments
/// </summary>
public class DynamicConfigService : IDynamicConfigService
{
    private readonly IKubernetesManager _kubernetesManager;
    private readonly ILogger<DynamicConfigService> _logger;
    private DynamicConfig _cachedConfig;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FileSystemWatcher? _fileWatcher;
    private readonly string _configFilePath;

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
        _configFilePath = KubernetesConstants.DynamicConfigFilePath;
    }

    public async Task<DynamicConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Try to read from mounted ConfigMap file
            if (File.Exists(_configFilePath))
            {
                try
                {
                    var configJson = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
                    var config = JsonSerializer.Deserialize<DynamicConfig>(configJson);
                    if (config != null)
                    {
                        _cachedConfig = config;
                        return config;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize dynamic config from file {FilePath}", _configFilePath);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to read dynamic config file {FilePath}", _configFilePath);
                }
            }
            else
            {
                _logger.LogWarning("Dynamic config file not found at {FilePath}, using cached or default config", _configFilePath);
            }

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
        
        // Try to update ConfigMap in Kubernetes if we're running in cluster
        try
        {
            var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await _kubernetesManager.UpdateConfigMapDataAsync(
                KubernetesConstants.DynamicConfigMapName,
                KubernetesConstants.DynamicConfigMapKey,
                configJson,
                cancellationToken: cancellationToken);
            
            _logger.LogInformation("Dynamic config saved to Kubernetes ConfigMap");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save dynamic config to Kubernetes ConfigMap (not running in cluster or no permissions), config updated in memory only");
        }
    }

    public Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting FileSystemWatcher for dynamic config changes at {FilePath}", _configFilePath);

        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            var fileName = Path.GetFileName(_configFilePath);
            
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                _logger.LogWarning("Config directory not found: {Directory}, file watching disabled", directory);
                return Task.CompletedTask;
            }

            _fileWatcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += async (_, args) =>
            {
                _logger.LogInformation("Detected config file change: {ChangeType}", args.ChangeType);
                
                // Debounce - wait a bit for file write to complete
                await Task.Delay(100);
                
                await _lock.WaitAsync(cancellationToken);
                try
                {
                    var configJson = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
                    var config = JsonSerializer.Deserialize<DynamicConfig>(configJson);
                    
                    if (config != null && !config.Equals(_cachedConfig))
                    {
                        _cachedConfig = config;
                        _logger.LogInformation(
                            "Config reloaded from file: MonitoringIntervalSeconds={Interval}",
                            config.MonitoringIntervalSeconds);
                        
                        // Raise event to notify subscribers
                        ConfigChanged?.Invoke(this, config);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reload config from file");
                }
                finally
                {
                    _lock.Release();
                }
            };

            _logger.LogInformation("FileSystemWatcher started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FileSystemWatcher for config changes");
        }

        return Task.CompletedTask;
    }
}
