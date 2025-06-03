namespace BaaSScheduler;

public class SchedulerConfig
{
    public List<JobConfig> Jobs { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public WebhookConfig Webhooks { get; set; } = new();
}

public class JobConfig
{
    public string Name { get; set; } = string.Empty;
    public string Schedule { get; set; } = string.Empty; // cron expression
    public string Script { get; set; } = string.Empty; // path to script
    public string Type { get; set; } = string.Empty; // powershell, bat, exe
}

public class WebConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5000;
    public string Password { get; set; } = "changeme";
}

public class WebhookConfig
{
    public string? Teams { get; set; }
    public string? Discord { get; set; }
}
