namespace BaaSScheduler;

public class JobRunHistory
{
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string OutputLog { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
    public int ExitCode { get; set; }
}

public class JobStatus
{
    public DateTime? LastRun { get; set; }
    public bool? Success { get; set; }
    public string? Message { get; set; }
    public string? OutputLog { get; set; }
    public TimeSpan? Duration { get; set; }
    public List<JobRunHistory> RunHistory { get; set; } = new();
    
    // Keep only the last 100 runs to prevent memory issues
    public void AddRunHistory(JobRunHistory run)
    {
        RunHistory.Insert(0, run); // Add to the beginning for most recent first
        if (RunHistory.Count > 100)
        {
            RunHistory.RemoveAt(RunHistory.Count - 1);
        }
    }
}
