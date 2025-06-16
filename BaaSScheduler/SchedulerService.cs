using System.Diagnostics;
using Microsoft.Extensions.Options;
using NCrontab;
using System.Linq;

namespace BaaSScheduler;

public class SchedulerService : BackgroundService
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly IOptionsMonitor<SchedulerConfig> _configMonitor;
    private readonly IConfigurationService _configurationService;
    private readonly Dictionary<JobConfig, CrontabSchedule> _schedules = new();
    private readonly Dictionary<JobConfig, DateTime> _nextRuns = new();
    private readonly Dictionary<JobConfig, JobStatus> _statuses = new();
    private SchedulerConfig _config;

    public SchedulerService(ILogger<SchedulerService> logger, IOptionsMonitor<SchedulerConfig> configMonitor, IConfigurationService configurationService)
    {
        _logger = logger;
        _configMonitor = configMonitor;
        _configurationService = configurationService;
        _config = _configMonitor.CurrentValue;
        
        // Subscribe to configuration changes
        _configMonitor.OnChange(OnConfigurationChanged);
        
        InitializeJobs();
    }private void OnConfigurationChanged(SchedulerConfig newConfig)
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
    }    private async Task RunJobAsync(JobConfig job, CancellationToken token)
    {
        var status = _statuses[job];
        var startTime = DateTime.Now;
        var outputLog = new System.Text.StringBuilder();
        var runHistory = new JobRunHistory
        {
            StartTime = startTime
        };
        
        try
        {
            _logger.LogInformation("Starting job {Job}", job.Name);
            status.LastRun = startTime;
            
            var psi = CreateProcessStartInfo(job);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                // Capture output and error streams
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                
                await proc.WaitForExitAsync(token);
                
                var output = await outputTask;
                var error = await errorTask;
                
                // Build complete output log
                if (!string.IsNullOrEmpty(output))
                {
                    outputLog.AppendLine("=== STDOUT ===");
                    outputLog.AppendLine(output);
                }
                if (!string.IsNullOrEmpty(error))
                {
                    outputLog.AppendLine("=== STDERR ===");
                    outputLog.AppendLine(error);
                }
                
                var endTime = DateTime.Now;
                var duration = endTime - startTime;
                
                status.Duration = duration;
                status.OutputLog = outputLog.ToString();
                
                // Update run history
                runHistory.EndTime = endTime;
                runHistory.Duration = duration;
                runHistory.OutputLog = outputLog.ToString();
                runHistory.ExitCode = proc.ExitCode;
                
                if (proc.ExitCode == 0)
                {
                    _logger.LogInformation("Job {Job} succeeded", job.Name);
                    status.Success = true;
                    status.Message = "Success";
                    runHistory.Success = true;
                    runHistory.Message = "Success";
                }
                else
                {
                    _logger.LogError("Job {Job} failed with exit code {Code}", job.Name, proc.ExitCode);
                    status.Success = false;
                    status.Message = $"Exit code {proc.ExitCode}";
                    runHistory.Success = false;
                    runHistory.Message = $"Exit code {proc.ExitCode}";
                }
                
                status.AddRunHistory(runHistory);
                await SendWebhookAsync(job, status);
            }
        }
        catch (Exception ex)
        {
            var endTime = DateTime.Now;
            var duration = endTime - startTime;
            
            _logger.LogError(ex, "Job {Job} threw exception", job.Name);
            status.Success = false;
            status.Message = ex.Message;
            status.Duration = duration;
            status.OutputLog = outputLog.ToString() + $"\n=== EXCEPTION ===\n{ex}";
            
            // Update run history
            runHistory.EndTime = endTime;
            runHistory.Duration = duration;
            runHistory.Success = false;
            runHistory.Message = ex.Message;
            runHistory.OutputLog = outputLog.ToString() + $"\n=== EXCEPTION ===\n{ex}";
            runHistory.ExitCode = -1;
            
            status.AddRunHistory(runHistory);
            await SendWebhookAsync(job, status);
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
    }    private async Task SendWebhookAsync(JobConfig job, JobStatus status)
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
            var nextRun = _nextRuns.TryGetValue(job, out var next) ? next : (DateTime?)null;
            
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            
            var tasks = new List<Task>();

            // Send Teams webhook
            var teamsUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Teams)
                ? _config.Webhooks.Teams
                : job.Webhooks!.Teams;
            
            if (!string.IsNullOrWhiteSpace(teamsUrl))
            {
                tasks.Add(SendTeamsWebhookAsync(client, teamsUrl, job, status, nextRun));
            }

            // Send Discord webhook
            var discordUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Discord)
                ? _config.Webhooks.Discord
                : job.Webhooks!.Discord;
            
            if (!string.IsNullOrWhiteSpace(discordUrl))
            {
                tasks.Add(SendDiscordWebhookAsync(client, discordUrl, job, status, nextRun));
            }

            if (tasks.Any())
            {
                await Task.WhenAll(tasks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook for job {Job}", job.Name);
        }
    }

    private async Task SendTeamsWebhookAsync(HttpClient client, string webhookUrl, JobConfig job, JobStatus status, DateTime? nextRun)
    {
        try
        {
            var card = CreateTeamsAdaptiveCard(job, status, nextRun);
            var payload = new { type = "message", attachments = new[] { card } };
            
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions 
            { 
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync(webhookUrl, content);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Teams webhook sent successfully for job {Job}", job.Name);
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Teams webhook failed for job {Job}: {StatusCode} - {Response}", 
                    job.Name, response.StatusCode, responseContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams webhook for job {Job}", job.Name);
        }
    }

    private async Task SendDiscordWebhookAsync(HttpClient client, string webhookUrl, JobConfig job, JobStatus status, DateTime? nextRun)
    {
        try
        {
            var embed = CreateDiscordEmbed(job, status, nextRun);
            var payload = new { embeds = new[] { embed } };
            
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions 
            { 
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync(webhookUrl, content);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Discord webhook sent successfully for job {Job}", job.Name);
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Discord webhook failed for job {Job}: {StatusCode} - {Response}", 
                    job.Name, response.StatusCode, responseContent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Discord webhook for job {Job}", job.Name);
        }
    }

    private object CreateTeamsAdaptiveCard(JobConfig job, JobStatus status, DateTime? nextRun)
    {
        var isSuccess = status.Success == true;
        var statusText = isSuccess ? "✅ Success" : "❌ Failed";
        
        var facts = new List<object>
        {
            new { title = "Job Name", value = job.Name },
            new { title = "Status", value = statusText },
            new { title = "Run Time", value = status.LastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown" }
        };

        if (status.Duration.HasValue)
        {
            facts.Add(new { title = "Duration", value = FormatDuration(status.Duration.Value) });
        }

        if (nextRun.HasValue)
        {
            facts.Add(new { title = "Next Run", value = nextRun.Value.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        if (!string.IsNullOrEmpty(status.Message))
        {
            facts.Add(new { title = "Result", value = status.Message });
        }

        var bodyElements = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = "🤖 BaaS Scheduler - Job Execution Report",
                weight = "bolder",
                size = "medium"
            },
            new
            {
                type = "FactSet",
                facts = facts.ToArray()
            }
        };

        // Add output log section if available
        if (!string.IsNullOrWhiteSpace(status.OutputLog))
        {
            // Truncate log if too long for Teams (limit to ~2000 chars for readability)
            var truncatedLog = status.OutputLog.Length > 2000 
                ? status.OutputLog.Substring(0, 2000) + "\n\n... (output truncated)"
                : status.OutputLog;

            bodyElements.Add(new
            {
                type = "TextBlock",
                text = "Output Log:",
                weight = "bolder",
                spacing = "medium"
            });
            
            bodyElements.Add(new
            {
                type = "TextBlock",
                text = truncatedLog,
                fontType = "monospace",
                wrap = true,
                spacing = "small"
            });
        }

        return new
        {
            contentType = "application/vnd.microsoft.card.adaptive",
            content = new
            {
                type = "AdaptiveCard",
                version = "1.4",
                body = bodyElements.ToArray()
            }
        };
    }

    private object CreateDiscordEmbed(JobConfig job, JobStatus status, DateTime? nextRun)
    {
        var isSuccess = status.Success == true;
        var color = isSuccess ? 0x00ff00 : 0xff0000; // Green for success, red for failure
        var statusEmoji = isSuccess ? "✅" : "❌";
        
        var fields = new List<object>
        {
            new { name = "Status", value = $"{statusEmoji} {(isSuccess ? "Success" : "Failed")}", inline = true },
            new { name = "Run Time", value = status.LastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown", inline = true }
        };

        if (status.Duration.HasValue)
        {
            fields.Add(new { name = "Duration", value = FormatDuration(status.Duration.Value), inline = true });
        }

        if (nextRun.HasValue)
        {
            fields.Add(new { name = "Next Run", value = nextRun.Value.ToString("yyyy-MM-dd HH:mm:ss"), inline = true });
        }

        if (!string.IsNullOrEmpty(status.Message))
        {
            fields.Add(new { name = "Result", value = status.Message, inline = false });
        }

        if (!string.IsNullOrWhiteSpace(status.OutputLog))
        {
            // Truncate log if too long for Discord (limit to 1024 chars per field)
            var truncatedLog = status.OutputLog.Length > 1000 
                ? status.OutputLog.Substring(0, 1000) + "\n... (truncated)"
                : status.OutputLog;
            
            fields.Add(new { name = "Output Log", value = $"```\n{truncatedLog}\n```", inline = false });
        }

        return new
        {
            title = $"🤖 BaaS Scheduler - {job.Name}",
            color = color,
            fields = fields.ToArray(),
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            footer = new { text = "BaaS Scheduler" }
        };
    }

    private string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return $"{duration.TotalSeconds:F1}s";
        }
        else if (duration.TotalHours < 1)
        {
            return $"{duration.TotalMinutes:F1}m";
        }
        else
        {
            return $"{duration.TotalHours:F1}h";
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
                kvp.Value.Duration,
                kvp.Value.OutputLog,
                NextRun = _nextRuns.TryGetValue(kvp.Key, out var nextRun) ? nextRun : (DateTime?)null
            }).ToList();
        }
    }    public OperationResult AddJob(JobConfig job)
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
            
            // Save configuration to file
            try
            {
                _configurationService.SaveConfigurationAsync(_config).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration after adding job {Job}", job.Name);
                // Remove the job from memory since we couldn't persist it
                _config.Jobs.Remove(job);
                if (_schedules.ContainsKey(job))
                {
                    _schedules.Remove(job);
                    _nextRuns.Remove(job);
                    _statuses.Remove(job);
                }
                return OperationResult.Error($"Failed to save configuration: {ex.Message}");
            }
            
            _logger.LogInformation("Job '{Job}' added successfully", job.Name);
            return OperationResult.Ok($"Job '{job.Name}' added successfully");
        }
    }    public OperationResult UpdateJob(string jobName, JobConfig updatedJob)
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

            // Save configuration to file
            try
            {
                _configurationService.SaveConfigurationAsync(_config).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration after updating job {Job}", existingJob.Name);
                return OperationResult.Error($"Failed to save configuration: {ex.Message}");
            }

            _logger.LogInformation("Job '{Job}' updated successfully", existingJob.Name);
            return OperationResult.Ok($"Job '{existingJob.Name}' updated successfully");
        }
    }    public OperationResult DeleteJob(string jobName)
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

            // Save configuration to file
            try
            {
                _configurationService.SaveConfigurationAsync(_config).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration after deleting job {Job}", jobName);
                // Add the job back since we couldn't persist the deletion
                _config.Jobs.Add(job);
                return OperationResult.Error($"Failed to save configuration: {ex.Message}");
            }

            _logger.LogInformation("Job '{Job}' deleted successfully", jobName);
            return OperationResult.Ok($"Job '{jobName}' deleted successfully");
        }
    }    public OperationResult ToggleJob(string jobName)
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

            // Save configuration to file
            try
            {
                _configurationService.SaveConfigurationAsync(_config).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration after toggling job {Job}", jobName);
                job.Enabled = !job.Enabled; // Revert the change
                return OperationResult.Error($"Failed to save configuration: {ex.Message}");
            }

            var status = job.Enabled ? "enabled" : "disabled";
            _logger.LogInformation("Job '{Job}' {Status}", jobName, status);
            return OperationResult.Ok($"Job '{jobName}' {status}");
        }
    }public IEnumerable<object> GetJobs()
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

    public List<JobRunHistory>? GetJobHistory(string jobName)
    {
        lock (_schedules)
        {
            var job = _config.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job != null && _statuses.TryGetValue(job, out var status))
            {
                return status.RunHistory;
            }
            return null;
        }
    }
}

