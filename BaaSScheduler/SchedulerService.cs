using System.Diagnostics;
using Microsoft.Extensions.Options;
using NCrontab;

namespace BaaSScheduler;

public class SchedulerService : BackgroundService
{
    private readonly ILogger<SchedulerService> _logger;
    private readonly SchedulerConfig _config;
    private readonly Dictionary<JobConfig, CrontabSchedule> _schedules = new();
    private readonly Dictionary<JobConfig, DateTime> _nextRuns = new();

    public SchedulerService(ILogger<SchedulerService> logger, IOptions<SchedulerConfig> options)
    {
        _logger = logger;
        _config = options.Value;
        foreach (var job in _config.Jobs)
        {
            var schedule = CrontabSchedule.Parse(job.Schedule);
            _schedules[job] = schedule;
            _nextRuns[job] = schedule.GetNextOccurrence(DateTime.Now);
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
        try
        {
            _logger.LogInformation("Starting job {Job}", job.Name);
            var psi = CreateProcessStartInfo(job);
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(token);
                if (proc.ExitCode == 0)
                {
                    _logger.LogInformation("Job {Job} succeeded", job.Name);
                    await SendWebhookAsync($"Job {job.Name} succeeded");
                }
                else
                {
                    _logger.LogError("Job {Job} failed with exit code {Code}", job.Name, proc.ExitCode);
                    await SendWebhookAsync($"Job {job.Name} failed with exit code {proc.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {Job} threw exception", job.Name);
            await SendWebhookAsync($"Job {job.Name} failed: {ex.Message}");
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

    private async Task SendWebhookAsync(string message)
    {
        using var client = new HttpClient();
        if (!string.IsNullOrWhiteSpace(_config.Webhooks.Teams))
        {
            var content = new StringContent("{\"text\": \"" + message.Replace("\"","\\\"") + "\"}", System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync(_config.Webhooks.Teams, content);
        }
        if (!string.IsNullOrWhiteSpace(_config.Webhooks.Discord))
        {
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("content", message) });
            await client.PostAsync(_config.Webhooks.Discord, content);
        }
    }
}

