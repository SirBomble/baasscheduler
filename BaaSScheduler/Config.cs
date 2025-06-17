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
    public bool Enabled { get; set; } = true; // enable/disable job execution
    public WebhookConfig Webhooks { get; set; } = new();
}

public class WebConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5001; // HTTPS port
    public string Password { get; set; } = "changeme";
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public bool TrustSelfSignedCert { get; set; } = false;
    public bool AutoRenewCert { get; set; } = true;
    public int CertValidityDays { get; set; } = 365;
}

public class WebhookConfig
{
    public string? Teams { get; set; }
    public string? Discord { get; set; }
    public bool Enabled { get; set; } = true; // enable/disable webhook notifications
}

public class LoginRequest
{
    public string Password { get; set; } = string.Empty;
}

public class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public static OperationResult Ok(string message = "") => new() { Success = true, Message = message };
    public static OperationResult Error(string message) => new() { Success = false, Message = message };
}

public class SettingsUpdateRequest
{
    public bool? TrustSelfSignedCert { get; set; }
    public bool? AutoRenewCert { get; set; }
    public int? CertValidityDays { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}
