# BAAS Scheduler

BAAS Scheduler is a Windows service for running scheduled scripts using cron expressions. It reads configuration from `appsettings.json` and exposes a simple web API.

## Building

```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The resulting executable in `bin/Release/net8.0/win-x64/publish` can install itself as a service with:

```
BAASScheduler.exe --install
```

Run as a console app for testing:

```
BAASScheduler.exe --console
```
