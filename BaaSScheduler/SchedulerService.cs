using System.Diagnostics;
using Microsoft.Extensions.Options;
using NCrontab;
using System.Linq;

namespace BaaSScheduler;

public class SchedulerService : BackgroundService
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly SchedulerConfig _config;
    private readonly Dictionary<JobConfig, CrontabSchedule> _schedules = new();
    private readonly Dictionary<JobConfig, DateTime> _nextRuns = new();
    private readonly Dictionary<JobConfig, JobStatus> _statuses = new();

    public SchedulerService(ILogger<SchedulerService> logger, IOptions<SchedulerConfig> options)
    {
        _logger = logger;
        _config = options.Value;
        foreach (var job in _config.Jobs)
        {
            var schedule = CrontabSchedule.Parse(job.Schedule);
            _schedules[job] = schedule;
            _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
            _statuses[job] = new JobStatus();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            foreach (var kvp in _schedules)
            {
                var job = kvp.Key;
                if (_nextRuns[job] <= now)
                {
                    _ = Task.Run(() => RunJobAsync(job, stoppingToken));
                    _nextRuns[job] = kvp.Value.GetNextOccurrence(now);
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
    }

    private async Task SendWebhookAsync(JobConfig job, string message)
    {
        using var client = new HttpClient();
        var teamsUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Teams)
            ? _config.Webhooks.Teams
            : job.Webhooks!.Teams;
        if (!string.IsNullOrWhiteSpace(teamsUrl))
        {
            var content = new StringContent("{\"text\": \"" + message.Replace("\"", "\\\"") + "\"}", System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync(teamsUrl, content);
        }

        var discordUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Discord)
            ? _config.Webhooks.Discord
            : job.Webhooks!.Discord;
        if (!string.IsNullOrWhiteSpace(discordUrl))
        {
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("content", message) });
            await client.PostAsync(discordUrl, content);
        }
    }

    public IEnumerable<object> GetStatuses()
    {
        return _statuses.Select(kvp => new
        {
            kvp.Key.Name,
            kvp.Value.LastRun,
            kvp.Value.Success,
            kvp.Value.Message
        });
    }

    public void AddJob(JobConfig job)
    {
        _config.Jobs.Add(job);
        var schedule = CrontabSchedule.Parse(job.Schedule);
        _schedules[job] = schedule;
        _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
        _statuses[job] = new JobStatus();
    }
}

