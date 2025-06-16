using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaaSScheduler;

public class WebhookService
{
    private readonly ILogger<WebhookService> _logger;
    private readonly HttpClient _httpClient;

    public WebhookService(ILogger<WebhookService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task SendWebhookNotificationAsync(JobConfig job, JobStatus status, DateTime? nextRun, SchedulerConfig config)
    {
        // Check if webhooks are enabled globally or for this specific job
        var globalWebhooksEnabled = config.Webhooks.Enabled;
        var jobWebhooksEnabled = job.Webhooks?.Enabled ?? true;
        
        if (!globalWebhooksEnabled && !jobWebhooksEnabled)
        {
            _logger.LogDebug("Webhooks are disabled, skipping notification for job {Job}", job.Name);
            return;
        }

        var tasks = new List<Task>();

        // Send Teams webhook
        var teamsUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Teams)
            ? config.Webhooks.Teams
            : job.Webhooks!.Teams;
        
        if (!string.IsNullOrWhiteSpace(teamsUrl))
        {
            tasks.Add(SendTeamsWebhookAsync(teamsUrl, job, status, nextRun));
        }

        // Send Discord webhook
        var discordUrl = string.IsNullOrWhiteSpace(job.Webhooks?.Discord)
            ? config.Webhooks.Discord
            : job.Webhooks!.Discord;
        
        if (!string.IsNullOrWhiteSpace(discordUrl))
        {
            tasks.Add(SendDiscordWebhookAsync(discordUrl, job, status, nextRun));
        }

        if (tasks.Any())
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task SendTeamsWebhookAsync(string webhookUrl, JobConfig job, JobStatus status, DateTime? nextRun)
    {
        try
        {
            var card = CreateTeamsAdaptiveCard(job, status, nextRun);
            var payload = new { type = "message", attachments = new[] { card } };
            
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content);
            
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

    private async Task SendDiscordWebhookAsync(string webhookUrl, JobConfig job, JobStatus status, DateTime? nextRun)
    {
        try
        {
            var embed = CreateDiscordEmbed(job, status, nextRun);
            var payload = new { embeds = new[] { embed } };
            
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(webhookUrl, content);
            
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
        var color = isSuccess ? "good" : "attention";
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

        var card = new
        {
            contentType = "application/vnd.microsoft.card.adaptive",
            content = new
            {
                type = "AdaptiveCard",
                version = "1.4",
                body = new object[]
                {
                    new
                    {
                        type = "TextBlock",
                        text = $"BaaS Scheduler - Job Execution Report",
                        weight = "bolder",
                        size = "medium"
                    },
                    new
                    {
                        type = "FactSet",
                        facts = facts.ToArray()
                    }
                }.Concat(CreateOutputLogSection(status.OutputLog)).ToArray()
            }
        };

        return card;
    }

    private object[] CreateOutputLogSection(string? outputLog)
    {
        if (string.IsNullOrWhiteSpace(outputLog))
        {
            return Array.Empty<object>();
        }

        // Truncate log if too long for Teams (limit to ~2000 chars for readability)
        var truncatedLog = outputLog.Length > 2000 
            ? outputLog.Substring(0, 2000) + "\n\n... (output truncated)"
            : outputLog;

        return new object[]
        {
            new
            {
                type = "TextBlock",
                text = "Output Log:",
                weight = "bolder",
                spacing = "medium"
            },
            new
            {
                type = "TextBlock",
                text = truncatedLog,
                fontType = "monospace",
                wrap = true,
                spacing = "small"
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
    }
}
