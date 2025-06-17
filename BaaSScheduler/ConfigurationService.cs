using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BaaSScheduler;

public interface IConfigurationService
{
    Task SaveConfigurationAsync(SchedulerConfig config);
    string GetConfigurationFilePath();
    OperationResult UpdateSettings(SettingsUpdateRequest request);
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

    public OperationResult UpdateSettings(SettingsUpdateRequest request)
    {
        try
        {
            // Read current configuration
            var currentConfig = GetCurrentConfiguration();
            
            // Update web settings
            if (request.TrustSelfSignedCert.HasValue)
                currentConfig.Web.TrustSelfSignedCert = request.TrustSelfSignedCert.Value;
            
            if (request.AutoRenewCert.HasValue)
                currentConfig.Web.AutoRenewCert = request.AutoRenewCert.Value;
            
            if (request.CertValidityDays.HasValue)
                currentConfig.Web.CertValidityDays = request.CertValidityDays.Value;
            
            if (!string.IsNullOrEmpty(request.CertificatePath))
                currentConfig.Web.CertificatePath = request.CertificatePath;
            
            if (!string.IsNullOrEmpty(request.CertificatePassword))
                currentConfig.Web.CertificatePassword = request.CertificatePassword;
            
            // Save configuration
            SaveConfigurationAsync(currentConfig).Wait();
            
            _logger.LogInformation("Settings updated successfully");
            return OperationResult.Ok("Settings updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update settings");
            return OperationResult.Error($"Failed to update settings: {ex.Message}");
        }
    }

    private SchedulerConfig GetCurrentConfiguration()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                return JsonSerializer.Deserialize<SchedulerConfig>(json, _jsonOptions) ?? new SchedulerConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load current configuration, using defaults");
        }
        
        return new SchedulerConfig();
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
