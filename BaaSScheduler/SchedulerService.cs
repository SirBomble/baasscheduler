using System.Diagnostics;
using Microsoft.Extensions.Options;
using NCrontab;
using System.Linq;

namespace BaaSScheduler;

public class SchedulerService : BackgroundService
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly IOptionsMonitor<SchedulerConfig> _configMonitor;
    private readonly Dictionary<JobConfig, CrontabSchedule> _schedules = new();
    private readonly Dictionary<JobConfig, DateTime> _nextRuns = new();
    private readonly Dictionary<JobConfig, JobStatus> _statuses = new();
    private SchedulerConfig _config;

    public SchedulerService(ILogger<SchedulerService> logger, IOptionsMonitor<SchedulerConfig> configMonitor)
    {
        _logger = logger;
        _configMonitor = configMonitor;
        _config = _configMonitor.CurrentValue;
        
        // Subscribe to configuration changes
        _configMonitor.OnChange(OnConfigurationChanged);
        
        InitializeJobs();
    }    private void OnConfigurationChanged(SchedulerConfig newConfig)
    {
        _logger.LogInformation("Configuration changed, reloading jobs...");
        _config = newConfig;
        lock (_schedules)
        {
            _schedules.Clear();
            _nextRuns.Clear();
            // Preserve existing statuses for jobs with the same name
            var existingStatuses = _statuses.ToDictionary(kvp => kvp.Key.Name, kvp => kvp.Value);
            _statuses.Clear();
            
            InitializeJobs();
            
            // Restore statuses for existing jobs
            foreach (var job in _config.Jobs)
            {
                if (existingStatuses.TryGetValue(job.Name, out var status))
                {
                    _statuses[job] = status;
                }
            }
            
            _logger.LogInformation("Loaded {Count} jobs, {EnabledCount} enabled", 
                _config.Jobs.Count, _config.Jobs.Count(j => j.Enabled));
        }
    }

    private void InitializeJobs()
    {
        foreach (var job in _config.Jobs)
        {
            if (!job.Enabled)
            {
                _logger.LogInformation("Job {Job} is disabled, skipping", job.Name);
                continue;
            }
            
            try
            {
                var schedule = CrontabSchedule.Parse(job.Schedule);
                _schedules[job] = schedule;
                _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
                if (!_statuses.ContainsKey(job))
                {
                    _statuses[job] = new JobStatus();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse schedule '{Schedule}' for job {Job}", job.Schedule, job.Name);
            }
        }
    }    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            lock (_schedules)
            {
                foreach (var kvp in _schedules)
                {
                    var job = kvp.Key;
                    if (job.Enabled && _nextRuns[job] <= now)
                    {
                        _ = Task.Run(() => RunJobAsync(job, stoppingToken));
                        _nextRuns[job] = kvp.Value.GetNextOccurrence(now);
                    }
                }
            }
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task RunJobAsync(JobConfig job, CancellationToken token)
    {
        var status = _statuses[job];
        try
        {
            _logger.LogInformation("Starting job {Job}", job.Name);
            status.LastRun = DateTime.Now;
            var psi = CreateProcessStartInfo(job);
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(token);
                if (proc.ExitCode == 0)
                {
                    _logger.LogInformation("Job {Job} succeeded", job.Name);
                    status.Success = true;
                    status.Message = "Success";
                    await SendWebhookAsync(job, $"Job {job.Name} succeeded");
                }
                else
                {
                    _logger.LogError("Job {Job} failed with exit code {Code}", job.Name, proc.ExitCode);
                    status.Success = false;
                    status.Message = $"Exit code {proc.ExitCode}";
                    await SendWebhookAsync(job, $"Job {job.Name} failed with exit code {proc.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Job} threw exception", job.Name);
            status.Success = false;
            status.Message = ex.Message;
            await SendWebhookAsync(job, $"Job {job.Name} failed: {ex.Message}");
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(JobConfig job)
    {
        var psi = new ProcessStartInfo();
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        switch (job.Type.ToLowerInvariant())
        {
            case "powershell":
            case "ps1":
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(job.Script);
                break;
            case "bat":
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(job.Script);
                break;
            case "exe":
            default:
                psi.FileName = job.Script;
                break;
        }
        return psi;
    }    private async Task SendWebhookAsync(JobConfig job, string message)
    {
        // Check if webhooks are enabled globally or for this specific job
        var globalWebhooksEnabled = _config.Webhooks.Enabled;
        var jobWebhooksEnabled = job.Webhooks?.Enabled ?? true;
        
        if (!globalWebhooksEnabled && !jobWebhooksEnabled)
        {
            _logger.LogDebug("Webhooks are disabled, skipping notification for job {Job}", job.Name);
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var teamsUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Teams)
                ? _config.Webhooks.Teams
                : job.Webhooks!.Teams;
            if (!string.IsNullOrWhiteSpace(teamsUrl))
            {
                try
                {
                    var teamsPayload = new { text = message };
                    var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(teamsPayload), System.Text.Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(teamsUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Teams webhook sent successfully for job {Job}", job.Name);
                    }
                    else
                    {
                        _logger.LogWarning("Teams webhook failed for job {Job}: {StatusCode}", job.Name, response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send Teams webhook for job {Job}", job.Name);
                }
            }

            var discordUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Discord)
                ? _config.Webhooks.Discord
                : job.Webhooks!.Discord;
            if (!string.IsNullOrWhiteSpace(discordUrl))
            {
                try
                {
                    var discordPayload = new { content = message };
                    var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(discordPayload), System.Text.Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(discordUrl, content);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Discord webhook sent successfully for job {Job}", job.Name);
                    }
                    else
                    {
                        _logger.LogWarning("Discord webhook failed for job {Job}: {StatusCode}", job.Name, response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send Discord webhook for job {Job}", job.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook for job {Job}", job.Name);
        }
    }    public IEnumerable<object> GetStatuses()
    {
        lock (_schedules)
        {
            return _statuses.Select(kvp => new
            {
                kvp.Key.Name,
                kvp.Key.Enabled,
                kvp.Value.LastRun,
                kvp.Value.Success,
                kvp.Value.Message,
                NextRun = _nextRuns.TryGetValue(kvp.Key, out var nextRun) ? nextRun : (DateTime?)null
            }).ToList();
        }
    }

    public OperationResult AddJob(JobConfig job)
    {
        lock (_schedules)
        {
            // Check if job with same name already exists
            if (_config.Jobs.Any(j => j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Error($"Job with name '{job.Name}' already exists");
            }

            _config.Jobs.Add(job);
            if (job.Enabled)
            {
                try
                {
                    var schedule = CrontabSchedule.Parse(job.Schedule);
                    _schedules[job] = schedule;
                    _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
                    _statuses[job] = new JobStatus();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse schedule '{Schedule}' for job {Job}", job.Schedule, job.Name);
                    return OperationResult.Error($"Invalid schedule format: {ex.Message}");
                }
            }
            
            _logger.LogInformation("Job '{Job}' added successfully", job.Name);
            return OperationResult.Ok($"Job '{job.Name}' added successfully");
        }
    }

    public OperationResult UpdateJob(string jobName, JobConfig updatedJob)
    {
        lock (_schedules)
        {
            var existingJob = _config.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (existingJob == null)
            {
                return OperationResult.Error($"Job '{jobName}' not found");
            }

            // Remove from schedules if it was scheduled
            if (_schedules.ContainsKey(existingJob))
            {
                _schedules.Remove(existingJob);
                _nextRuns.Remove(existingJob);
            }

            // Update job properties
            existingJob.Name = updatedJob.Name;
            existingJob.Schedule = updatedJob.Schedule;
            existingJob.Script = updatedJob.Script;
            existingJob.Type = updatedJob.Type;
            existingJob.Enabled = updatedJob.Enabled;
            existingJob.Webhooks = updatedJob.Webhooks;

            // Add to schedules if enabled
            if (existingJob.Enabled)
            {
                try
                {
                    var schedule = CrontabSchedule.Parse(existingJob.Schedule);
                    _schedules[existingJob] = schedule;
                    _nextRuns[existingJob] = schedule.GetNextOccurrence(DateTime.Now);
                    
                    if (!_statuses.ContainsKey(existingJob))
                    {
                        _statuses[existingJob] = new JobStatus();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse schedule '{Schedule}' for job {Job}", existingJob.Schedule, existingJob.Name);
                    return OperationResult.Error($"Invalid schedule format: {ex.Message}");
                }
            }

            _logger.LogInformation("Job '{Job}' updated successfully", existingJob.Name);
            return OperationResult.Ok($"Job '{existingJob.Name}' updated successfully");
        }
    }

    public OperationResult DeleteJob(string jobName)
    {
        lock (_schedules)
        {
            var job = _config.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                return OperationResult.Error($"Job '{jobName}' not found");
            }

            // Remove from schedules
            if (_schedules.ContainsKey(job))
            {
                _schedules.Remove(job);
                _nextRuns.Remove(job);
                _statuses.Remove(job);
            }

            // Remove from config
            _config.Jobs.Remove(job);

            _logger.LogInformation("Job '{Job}' deleted successfully", jobName);
            return OperationResult.Ok($"Job '{jobName}' deleted successfully");
        }
    }

    public OperationResult ToggleJob(string jobName)
    {
        lock (_schedules)
        {
            var job = _config.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job == null)
            {
                return OperationResult.Error($"Job '{jobName}' not found");
            }

            job.Enabled = !job.Enabled;

            if (job.Enabled)
            {
                try
                {
                    var schedule = CrontabSchedule.Parse(job.Schedule);
                    _schedules[job] = schedule;
                    _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
                    
                    if (!_statuses.ContainsKey(job))
                    {
                        _statuses[job] = new JobStatus();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse schedule '{Schedule}' for job {Job}", job.Schedule, job.Name);
                    job.Enabled = false; // Revert the change
                    return OperationResult.Error($"Invalid schedule format: {ex.Message}");
                }
            }
            else
            {
                // Remove from schedules
                if (_schedules.ContainsKey(job))
                {
                    _schedules.Remove(job);
                    _nextRuns.Remove(job);
                }
            }

            var status = job.Enabled ? "enabled" : "disabled";
            _logger.LogInformation("Job '{Job}' {Status}", jobName, status);
            return OperationResult.Ok($"Job '{jobName}' {status}");
        }
    }    public IEnumerable<object> GetJobs()
    {
        return _config.Jobs.Select(j => new { 
            j.Name, 
            j.Schedule, 
            j.Script, 
            j.Type, 
            j.Enabled,
            j.Webhooks
        });
    }
}

