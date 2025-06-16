using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BaaSScheduler;

public interface IConfigurationService
{
    Task SaveConfigurationAsync(SchedulerConfig config);
    string GetConfigurationFilePath();
}

public class ConfigurationService : IConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly string _configFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ConfigurationService(ILogger<ConfigurationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        
        // Get the configuration file path from the static variable set during startup
        _configFilePath = ConfigurationHelper.ConfigFilePath ?? GetDefaultConfigPath();
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        _logger.LogInformation("Configuration service initialized with file path: {ConfigPath}", _configFilePath);
    }

    public async Task SaveConfigurationAsync(SchedulerConfig config)
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogInformation("Created configuration directory: {Directory}", directory);
            }
            
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            
            // Create backup of existing file
            if (File.Exists(_configFilePath))
            {
                var backupPath = $"{_configFilePath}.backup";
                File.Copy(_configFilePath, backupPath, true);
                _logger.LogDebug("Created backup of configuration file at {BackupPath}", backupPath);
            }
            
            // Write new configuration
            await File.WriteAllTextAsync(_configFilePath, json);
            _logger.LogInformation("Configuration saved to {ConfigPath}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration to {ConfigPath}", _configFilePath);
            throw;
        }
    }

    public string GetConfigurationFilePath()
    {
        return _configFilePath;
    }

    private static string GetDefaultConfigPath()
    {
        return @"C:\BAAS\BaaSScheduler.json";
    }
}

public static class ConfigurationHelper
{
    public static string? ConfigFilePath { get; set; }
}
