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
    private bool _initialized;

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

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
        _initialized = false;
    }

    public async Task<DynamicConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Load from file only on first access
            if (!_initialized)
            {
                await LoadConfigFromFileAsync(cancellationToken);
                _initialized = true;
            }

            return _cachedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadConfigFromFileAsync(CancellationToken cancellationToken)
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
                    _logger.LogInformation(
                        "Loaded dynamic config from file: MonitoringIntervalSeconds={Interval}",
                        config.MonitoringIntervalSeconds);
                    return;
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
            _logger.LogWarning("Dynamic config file not found at {FilePath}, using default config", _configFilePath);
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
            var configJson = JsonSerializer.Serialize(config, _serializerOptions);

            await _kubernetesManager.UpdateConfigMapDataAsync(
                KubernetesConstants.DynamicConfigMapName,
                KubernetesConstants.DynamicConfigMapKey,
                configJson,
                cancellationToken: cancellationToken);
            // Success is logged by KubernetesManager when the patch runs; when not in-cluster it returns without throwing.
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

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                _logger.LogWarning("Config directory not found: {Directory}, file watching disabled", directory);
                return Task.CompletedTask;
            }

            // Kubernetes ConfigMap updates work via symlinks. Watch for the ..data symlink changes
            // which is how Kubernetes atomically updates mounted ConfigMaps
            _fileWatcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            // Handle all types of file system events because Kubernetes uses atomic symlink swaps
            void ReloadConfig(object sender, FileSystemEventArgs args)
            {
                // Only react to ..data changes (Kubernetes atomic update mechanism) or direct file changes
                if (!args.Name?.Contains("..data") == true && args.Name != Path.GetFileName(_configFilePath))
                {
                    return;
                }

                _logger.LogInformation("Detected config directory change: {ChangeType} - {Name}", args.ChangeType, args.Name);

                // Fire and forget async reload
                _ = Task.Run(async () =>
                {
                    // Wait for the atomic update to complete
                    await Task.Delay(500, cancellationToken);

                    await _lock.WaitAsync(cancellationToken);
                    try
                    {
                        if (!File.Exists(_configFilePath))
                        {
                            _logger.LogWarning("Config file disappeared during reload");
                            return;
                        }

                        var configJson = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
                        var config = JsonSerializer.Deserialize<DynamicConfig>(configJson, _serializerOptions);

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
                }, cancellationToken);
            }

            _fileWatcher.Changed += ReloadConfig;
            _fileWatcher.Created += ReloadConfig;
            _fileWatcher.Deleted += ReloadConfig;

            _logger.LogInformation("FileSystemWatcher started successfully for directory {Directory}", directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start FileSystemWatcher for config changes");
        }

        return Task.CompletedTask;
    }
}
