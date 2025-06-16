# BAAS Scheduler

BAAS Scheduler is a Windows service that executes scripted jobs according to cron expressions. It features a modern web interface with file browser capabilities, job run history tracking, and comprehensive filtering options.

## Features

- **Job Scheduling**: Execute PowerShell scripts, batch files, and executables using cron expressions
- **Web Interface**: Modern, responsive web UI for managing jobs and monitoring status
- **File Browser**: Built-in file picker for selecting script files
- **Run History**: Track job execution history with detailed logs and filtering
- **Job Filtering**: Filter jobs by name, status, and type
- **Webhook Support**: Teams and Discord webhook notifications
- **Real-time Monitoring**: Live job status updates and execution logs

## Requirements

* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for building
* Windows 10/11 or Windows Server when running as a service

## Build

To create a self-contained executable run:

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

During development you can simply run the project with `dotnet run`.  The
project no longer hard codes a Windows runtime identifier so it executes on
any platform with the .NET SDK installed.

The output will be placed under `bin/Release/net8.0/win-x64/publish`.

## Running

From the publish directory you can install the service:

```bash
BAASScheduler.exe --install
```

You may specify an alternate configuration file:

```bash
BAASScheduler.exe --install --config C:\MyConfigs\BaaSScheduler.json
```

Uninstall with:

```bash
BAASScheduler.exe --uninstall
```

During development you can run it in the console instead of installing it:

```bash
BAASScheduler.exe --console
```

## Configuration

All runtime settings are stored in a JSON configuration file.
By default, the configuration file is located at `C:\BAAS\BaaSScheduler.json`.
You can specify a custom configuration file path with `--config <file>` when running or installing the service.
The configuration file path should be an absolute path (e.g., `C:\MyConfigs\scheduler.json`).
An example configuration is provided in `examples/appsettings.example.json`
along with simple test scripts under `examples/scripts`.  Copy the example
file to your desired location and adjust the script paths to quickly try the
scheduler.

### Jobs
Each entry in `Jobs` defines a scheduled task:

```json
{
  "Name": "Sample",
  "Schedule": "*/5 * * * *",
  "Script": "C:/scripts/test.ps1",
  "Type": "powershell",
  "Enabled": true,
  "Webhooks": {
    "Teams": "https://your-teams-webhook-url",
    "Discord": "https://discord.com/api/webhooks/your-webhook-url",
    "Enabled": true
  }
}
```

`Schedule` uses standard cron notation. `Type` may be `powershell`, `bat` or `exe`.
`Enabled` controls whether the job is active (defaults to true).
Job-specific webhook settings override global ones when provided.

### Web
`Web` controls the HTTP listener:

* `Host` - address to bind to
* `Port` - port number
* `Password` - password required by the API

### Webhooks
Global webhook URLs notified when any job finishes. Individual jobs may override
these values with their own `Webhooks` object.

```json
"Webhooks": {
  "Teams": "https://your-teams-webhook-url",
  "Discord": "https://discord.com/api/webhooks/your-webhook-url",
  "Enabled": true
}
```

`Enabled` controls whether webhooks are sent globally (defaults to true).
Individual jobs can have their own `Enabled` setting to override this.

## Auto-reload Configuration

The service automatically reloads the configuration when `appsettings.json` is modified.
Jobs are updated without requiring a service restart. Existing job execution status
is preserved across configuration reloads.

## Configuration Persistence

When jobs are added, updated, or deleted through the web interface or API, the changes are automatically saved back to the `appsettings.json` file. This ensures that:

- **Configuration Changes Persist**: All modifications made through the web UI are permanently saved
- **Service Restarts Preserve Changes**: Jobs created/modified via the web interface will survive service restarts
- **Backup Protection**: A backup of the configuration file (`appsettings.json.backup`) is created before each save operation
- **Configuration File Location**: The web interface displays the full path of the configuration file being used
- **Error Handling**: If configuration saving fails, the changes are reverted in memory and an error is returned

The configuration persistence feature works with custom configuration file paths specified with the `--config` parameter.

## Web Interface

The BaaS Scheduler includes a modern web interface accessible at `http://localhost:5000` (or your configured host/port).

### Features:
- **Dashboard**: Overview of jobs, execution statistics, and system status
- **Job Management**: Create, edit, enable/disable, and delete jobs
- **File Browser**: Built-in file picker to easily select script files
- **Job Filtering**: Filter jobs by name, status (enabled/disabled), and type
- **Run History**: View detailed execution history for all jobs with filtering by:
  - Specific job
  - Success/failure status
  - Date range
- **Execution Logs**: View detailed output logs for each job run
- **Real-time Updates**: Live status updates every 30 seconds

### Authentication:
The web interface requires password authentication using the password configured in `appsettings.json`. Sessions are valid for 1 hour.

## HTTP API

The service exposes a REST API for integration with other systems:

### Authentication
* `POST /api/auth/login` – authenticate with password
* `POST /api/auth/logout` – invalidate session

### Job Management  
* `GET /api/jobs` – lists all configured jobs
* `POST /api/jobs` – adds a new job at runtime
* `PUT /api/jobs/{jobName}` – updates an existing job
* `DELETE /api/jobs/{jobName}` – removes a job
* `PATCH /api/jobs/{jobName}/toggle` – enables/disables a job

### Job Status & History
* `GET /api/status` – get current status of all jobs
* `GET /api/jobs/{jobName}/history` – get execution history for a specific job

### File Browser (for web interface)
* `GET /api/files/browse?path={path}` – browse files and directories
* `GET /api/files/drives` – list available drives

### Configuration
* `GET /api/config/path` – get the path of the current configuration file

### Authentication
All API requests (except `/api/auth/login`) must include the `X-Session-Id` header with a valid session ID obtained from login.

Example:

```bash
# Login and get session ID
curl -X POST -H "Content-Type: application/json" \
  -d '{"password":"changeme"}' \
  http://localhost:5000/api/auth/login

# Use session ID for subsequent requests
curl -H "X-Session-Id: your-session-id" \
  http://localhost:5000/api/jobs
```
