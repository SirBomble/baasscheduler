# BAAS Scheduler

BAAS Scheduler is a small Windows service that executes scripted jobs according to cron expressions. Jobs and service settings are defined in `appsettings.json` and the application exposes a minimal HTTP API so you can inspect the configured jobs.

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

Uninstall with:

```bash
BAASScheduler.exe --uninstall
```

During development you can run it in the console instead of installing it:

```bash
BAASScheduler.exe --console
```

## Configuration

All runtime settings are stored in `appsettings.json`.
An example configuration is provided in `examples/appsettings.example.json`
along with simple test scripts under `examples/scripts`.  Copy the example
file to `appsettings.json` and adjust the script paths to quickly try the
scheduler.

### Jobs
Each entry in `Jobs` defines a scheduled task:

```json
{
  "Name": "Sample",
  "Schedule": "*/5 * * * *",
  "Script": "C:/scripts/test.ps1",
  "Type": "powershell",
  "Webhooks": {
    "Teams": "",
    "Discord": ""
  }
}
```

`Schedule` uses standard cron notation. `Type` may be `powershell`, `bat` or `exe`.

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
  "Teams": "",
  "Discord": ""
}
```

## HTTP API

* `GET /` – serves a simple web interface
* `GET /api/jobs` – lists all configured jobs
* `POST /api/jobs` – adds a new job at runtime
* `GET /api/status` – reports last run status for each job

Requests under `/api` must include the `X-Password` header with the password from configuration.

Example:

```bash
curl -H "X-Password: changeme" http://localhost:5000/api/jobs
```
