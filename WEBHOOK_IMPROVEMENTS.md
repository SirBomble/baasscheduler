# BaaS Scheduler - Modernized Webhooks Implementation

## Overview
The webhook system has been completely modernized to provide richer, more informative notifications with proper formatting for both Microsoft Teams and Discord platforms.

## Major Improvements

### 1. Enhanced Data Capture
- **Complete Output Logging**: All script output (STDOUT and STDERR) is now captured and stored
- **Execution Duration**: Precise timing of job execution is tracked
- **Enhanced Status Tracking**: Improved job status information with detailed metadata

### 2. Modern Teams Webhook Integration
The Teams webhooks now use **Adaptive Cards** instead of simple text messages, providing:

- **Rich Visual Format**: Professional-looking cards with structured information
- **Comprehensive Job Details**:
  - Job name and status (with success/failure indicators)
  - Run time and duration
  - Next scheduled run time
  - Result message
  - Complete output log (truncated for readability)
- **Color-coded Status**: Visual indicators for success/failure
- **Monospace Font**: Output logs displayed in code-friendly formatting

### 3. Enhanced Discord Webhooks
Discord notifications now use **Rich Embeds** featuring:

- **Color-coded Messages**: Green for success, red for failure
- **Structured Fields**: Organized information display
- **Emoji Indicators**: Visual status representation
- **Code Blocks**: Output logs formatted in markdown code blocks
- **Timestamps**: Automatic timestamping of notifications
- **Footer Branding**: BaaS Scheduler identification

### 4. Improved Web Interface
The web dashboard has been updated to display the new information:

- **Duration Column**: Shows job execution time
- **View Log Button**: Opens detailed output log modal
- **Enhanced Status Modal**: Comprehensive job execution details
- **Copy to Clipboard**: Easy log copying functionality
- **Better Formatting**: Improved display of duration and timestamps

### 5. Better Error Handling
- **Async Operations**: Improved webhook sending with better error handling
- **Parallel Execution**: Teams and Discord webhooks sent simultaneously
- **Detailed Logging**: Enhanced logging for webhook success/failure
- **Graceful Degradation**: System continues working even if webhooks fail

## Technical Details

### Updated JobStatus Class
```csharp
public class JobStatus
{
    public DateTime? LastRun { get; set; }
    public bool? Success { get; set; }
    public string? Message { get; set; }
    public string? OutputLog { get; set; }      // NEW
    public TimeSpan? Duration { get; set; }     // NEW
}
```

### Teams Adaptive Card Format
The Teams webhook now sends structured Adaptive Cards with:
- Header with job execution report title
- Fact sets showing key metrics
- Monospace-formatted output logs
- Proper truncation for large outputs

### Discord Rich Embed Format
Discord webhooks use rich embeds with:
- Title indicating job name
- Color-coded status (green/red)
- Structured fields for all job details
- Code-formatted output logs
- Timestamps and footer

### Process Output Capture
The job execution now captures both STDOUT and STDERR:
```csharp
psi.RedirectStandardOutput = true;
psi.RedirectStandardError = true;
```

### Duration Tracking
Precise execution timing:
```csharp
var startTime = DateTime.Now;
// ... job execution ...
status.Duration = DateTime.Now - startTime;
```

## Configuration Example

```json
{
  "Webhooks": {
    "Teams": "https://your-teams-webhook-url",
    "Discord": "https://your-discord-webhook-url", 
    "Enabled": true
  }
}
```

## Testing

Two test scripts have been created for testing:

1. **webhook-test.ps1**: Successful execution with comprehensive output
2. **webhook-test-failure.ps1**: Simulated failure scenario

## Benefits

1. **Better Monitoring**: Rich information helps identify issues quickly
2. **Professional Appearance**: Modern card/embed formats look professional
3. **Complete Visibility**: Full output logs provide complete context
4. **Improved Debugging**: Duration and detailed logs aid troubleshooting
5. **Enhanced User Experience**: Better web interface for job management

## Webhook Format Examples

### Teams Adaptive Card
- Professional card layout with structured facts
- Success/failure indicators with emojis
- Duration, timestamps, and next run information
- Complete output log in monospace format

### Discord Rich Embed
- Color-coded embed (green/red)
- Inline fields for quick information
- Code-formatted output logs
- Timestamp and branding footer

The modernized webhook system provides significantly more value for monitoring and troubleshooting scheduled jobs while maintaining backward compatibility with existing configurations.
