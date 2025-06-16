using System.Text.Json;

namespace BaaSScheduler;

public interface IRunHistoryService
{
    Task SaveRunHistoryAsync(string jobName, JobRunHistory runHistory);
    Task<List<JobRunHistory>> LoadRunHistoryAsync(string jobName);
    Task<Dictionary<string, List<JobRunHistory>>> LoadAllRunHistoriesAsync();
    string GetRunHistoryFilePath();
}

public class RunHistoryService : IRunHistoryService
{
    private readonly ILogger<RunHistoryService> _logger;
    private readonly IConfigurationService _configurationService;
    private readonly string _runHistoryFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public RunHistoryService(ILogger<RunHistoryService> logger, IConfigurationService configurationService)
    {
        _logger = logger;
        _configurationService = configurationService;
        
        // Create the run history file path next to the config file
        var configFilePath = _configurationService.GetConfigurationFilePath();
        var configDirectory = Path.GetDirectoryName(configFilePath) ?? string.Empty;
        var configFileName = Path.GetFileNameWithoutExtension(configFilePath);
        _runHistoryFilePath = Path.Combine(configDirectory, $"{configFileName}.runhistory.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        _logger.LogInformation("Run history service initialized with file path: {RunHistoryPath}", _runHistoryFilePath);
    }

    public async Task SaveRunHistoryAsync(string jobName, JobRunHistory runHistory)
    {
        await _fileLock.WaitAsync();
        try
        {
            // Load existing run histories
            var allHistories = await LoadAllRunHistoriesInternalAsync();
            
            // Get or create the history list for this job
            if (!allHistories.ContainsKey(jobName))
            {
                allHistories[jobName] = new List<JobRunHistory>();
            }
            
            var jobHistory = allHistories[jobName];
            
            // Add new run to the beginning (most recent first)
            jobHistory.Insert(0, runHistory);
            
            // Keep only the last 100 runs to prevent file from growing too large
            if (jobHistory.Count > 100)
            {
                jobHistory.RemoveRange(100, jobHistory.Count - 100);
            }
            
            // Save back to file
            await SaveAllRunHistoriesInternalAsync(allHistories);
            
            _logger.LogDebug("Saved run history for job {JobName}", jobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save run history for job {JobName}", jobName);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<List<JobRunHistory>> LoadRunHistoryAsync(string jobName)
    {
        await _fileLock.WaitAsync();
        try
        {
            var allHistories = await LoadAllRunHistoriesInternalAsync();
            return allHistories.TryGetValue(jobName, out var history) ? history : new List<JobRunHistory>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load run history for job {JobName}", jobName);
            return new List<JobRunHistory>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<Dictionary<string, List<JobRunHistory>>> LoadAllRunHistoriesAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            return await LoadAllRunHistoriesInternalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all run histories");
            return new Dictionary<string, List<JobRunHistory>>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public string GetRunHistoryFilePath()
    {
        return _runHistoryFilePath;
    }

    private async Task<Dictionary<string, List<JobRunHistory>>> LoadAllRunHistoriesInternalAsync()
    {
        if (!File.Exists(_runHistoryFilePath))
        {
            _logger.LogDebug("Run history file does not exist: {FilePath}", _runHistoryFilePath);
            return new Dictionary<string, List<JobRunHistory>>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_runHistoryFilePath);
            var histories = JsonSerializer.Deserialize<Dictionary<string, List<JobRunHistory>>>(json, _jsonOptions);
            return histories ?? new Dictionary<string, List<JobRunHistory>>();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse run history file: {FilePath}", _runHistoryFilePath);
            return new Dictionary<string, List<JobRunHistory>>();
        }
    }

    private async Task SaveAllRunHistoriesInternalAsync(Dictionary<string, List<JobRunHistory>> allHistories)
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_runHistoryFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogDebug("Created run history directory: {Directory}", directory);
            }

            var json = JsonSerializer.Serialize(allHistories, _jsonOptions);
            await File.WriteAllTextAsync(_runHistoryFilePath, json);
            
            _logger.LogDebug("Saved all run histories to {FilePath}", _runHistoryFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save run histories to {FilePath}", _runHistoryFilePath);
            throw;
        }
    }
}
