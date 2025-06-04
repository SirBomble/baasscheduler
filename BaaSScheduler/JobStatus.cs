namespace BaaSScheduler;

public class JobStatus
{
    public DateTime? LastRun { get; set; }
    public bool? Success { get; set; }
    public string? Message { get; set; }
}
