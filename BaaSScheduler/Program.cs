using System.Diagnostics;
using BaaSScheduler;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

// optional configuration file parameter
string configFile = @"C:\BAAS\BaaSScheduler.json";
var configArgIndex = Array.IndexOf(args, "--config");
if (configArgIndex >= 0 && args.Length > configArgIndex + 1)
{
    configFile = Path.GetFullPath(args[configArgIndex + 1]);
}
else
{
    const string prefix = "--config=";
    var match = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (match != null)
    {
        configFile = Path.GetFullPath(match.Substring(prefix.Length));
    }
}

// Store the config file path for the configuration service to use
ConfigurationHelper.ConfigFilePath = configFile;

if (args.Contains("--install"))
{
    var exe = Process.GetCurrentProcess().MainModule!.FileName!;
    // include config file argument when installing if specified
    var binPath = $"\"{exe}\"";
    if (configArgIndex >= 0 || args.Any(a => a.StartsWith("--config=")))
    {
        binPath += $" --config \"{configFile}\"";
    }
    Process.Start("sc.exe", $"create BAASScheduler binPath= {binPath} start= auto");
    return;
}
if (args.Contains("--uninstall"))
{
    Process.Start("sc.exe", "delete BAASScheduler");
    return;
}

var isService = !(Debugger.IsAttached || args.Contains("--console"));
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();

// Ensure the configuration directory exists if using the default path
if (configFile == @"C:\BAAS\BaaSScheduler.json")
{
    var configDir = Path.GetDirectoryName(configFile);
    if (!Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir!);
    }
    
    // Create default config file if it doesn't exist
    if (!File.Exists(configFile))
    {
        var defaultConfig = new SchedulerConfig
        {
            Web = new WebConfig
            {
                Host = "localhost",
                Port = 5000,
                Password = "changeme"
            },
            Webhooks = new WebhookConfig
            {
                Enabled = true
            },
            Jobs = new List<JobConfig>()
        };
        
        var json = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        
        File.WriteAllText(configFile, json);
    }
}

// Check if a custom config file was specified
bool isCustomConfig = configFile != @"C:\BAAS\BaaSScheduler.json";

if (isCustomConfig)
{
    // For custom config files, only load that specific file
    builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: true);
}
else
{
    // For default config, load it along with development overrides
    builder.Configuration.AddJsonFile(configFile, optional: true, reloadOnChange: true);
}

builder.Services.Configure<SchedulerConfig>(builder.Configuration);
builder.Services.AddSingleton<IConfigurationService, ConfigurationService>();
builder.Services.AddSingleton<IRunHistoryService, RunHistoryService>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
builder.Services.AddSingleton<SessionService>();

var config = builder.Configuration.Get<SchedulerConfig>() ?? new SchedulerConfig();

builder.WebHost.UseUrls($"http://{config.Web.Host}:{config.Web.Port}");

var app = builder.Build();
var embeddedProvider = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "");

if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    // Skip authentication for login endpoint
    if (context.Request.Path.StartsWithSegments("/api/auth/login"))
    {
        await next();
        return;
    }    var sessionService = app.Services.GetRequiredService<SessionService>();
    var sessionId = context.Request.Headers["X-Session-Id"].FirstOrDefault();
    
    if (!sessionService.IsValidSession(sessionId ?? string.Empty))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    await next();
});

app.MapPost("/api/auth/login", ([FromBody] LoginRequest request, [FromServices] IConfiguration config, [FromServices] SessionService sessionService) =>
{
    try
    {
        var schedulerConfig = config.Get<SchedulerConfig>() ?? new SchedulerConfig();
        var sessionId = sessionService.CreateSession(request.Password, schedulerConfig.Web.Password);
        return Results.Ok(new { SessionId = sessionId });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
});

app.MapPost("/api/auth/logout", ([FromServices] SessionService sessionService, HttpContext context) =>
{
    var sessionId = context.Request.Headers["X-Session-Id"].FirstOrDefault();
    if (!string.IsNullOrEmpty(sessionId))
    {
        sessionService.InvalidateSession(sessionId);
    }
    return Results.Ok();
});

// File browser endpoints
app.MapGet("/api/files/browse", (string? path) =>
{
    try
    {
        var targetPath = string.IsNullOrEmpty(path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : path;
        
        if (!Directory.Exists(targetPath))
        {
            return Results.NotFound("Directory not found");
        }
        
        var directories = Directory.GetDirectories(targetPath)
            .Select(d => new
            {
                Name = Path.GetFileName(d),
                Path = d,
                Type = "directory",
                Extension = (string?)null
            }).ToList();
            
        var files = Directory.GetFiles(targetPath, "*")
            .Where(f => {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".ps1" || ext == ".bat" || ext == ".exe" || ext == ".cmd";
            })
            .Select(f => new
            {
                Name = Path.GetFileName(f),
                Path = f,
                Type = "file",
                Extension = (string?)Path.GetExtension(f).ToLowerInvariant()
            }).ToList();
            
        var parentPath = targetPath != Path.GetPathRoot(targetPath) ? Directory.GetParent(targetPath)?.FullName : null;
        
        var allItems = new List<object>();
        allItems.AddRange(directories);
        allItems.AddRange(files);
        
        return Results.Ok(new
        {
            CurrentPath = targetPath,
            ParentPath = parentPath,
            Items = allItems.OrderBy(i => ((dynamic)i).Type).ThenBy(i => ((dynamic)i).Name)
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/files/drives", () =>
{
    try
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new
            {
                Name = d.Name,
                Path = d.Name,
                Type = "drive",
                Label = d.VolumeLabel
            });
            
        return Results.Ok(drives);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// Job run history endpoint
app.MapGet("/api/jobs/{jobName}/history", ([FromRoute] string jobName, [FromServices] SchedulerService svc) =>
{
    var history = svc.GetJobHistory(jobName);
    return history != null ? Results.Ok(history) : Results.NotFound();
});

// Historical statistics endpoint
app.MapGet("/api/stats/historical", async ([FromServices] SchedulerService svc) =>
{
    var stats = await svc.GetHistoricalStatsAsync();
    return Results.Ok(stats);
});

// All job histories endpoint (for the run history modal)
app.MapGet("/api/runhistory/all", async ([FromServices] SchedulerService svc) =>
{
    var histories = await svc.GetAllJobHistoriesAsync();
    var flatHistory = new List<object>();
    
    foreach (var kvp in histories)
    {
        var jobName = kvp.Key;
        var runs = kvp.Value;
        
        foreach (var run in runs)
        {
            flatHistory.Add(new
            {
                JobName = jobName,
                StartTime = run.StartTime,
                EndTime = run.EndTime,
                Success = run.Success,
                Message = run.Message,
                OutputLog = run.OutputLog,
                Duration = run.Duration,
                ExitCode = run.ExitCode
            });
        }
    }
    
    return Results.Ok(flatHistory.OrderByDescending(r => ((dynamic)r).StartTime));
});

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/api/jobs", ([FromServices] SchedulerService svc) => svc.GetJobs());
app.MapGet("/api/status", ([FromServices] SchedulerService svc) => svc.GetStatuses());
app.MapGet("/api/config/path", ([FromServices] IConfigurationService configSvc) => 
    Results.Ok(new { ConfigurationFilePath = configSvc.GetConfigurationFilePath() }));

app.MapGet("/api/runhistory/path", ([FromServices] IRunHistoryService runHistorySvc) => 
    Results.Ok(new { RunHistoryFilePath = runHistorySvc.GetRunHistoryFilePath() }));

app.MapPost("/api/jobs", ([FromBody] JobConfig job, [FromServices] SchedulerService svc) =>
{
    var result = svc.AddJob(job);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPut("/api/jobs/{jobName}", ([FromRoute] string jobName, [FromBody] JobConfig job, [FromServices] SchedulerService svc) =>
{
    var result = svc.UpdateJob(jobName, job);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapDelete("/api/jobs/{jobName}", ([FromRoute] string jobName, [FromServices] SchedulerService svc) =>
{
    var result = svc.DeleteJob(jobName);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPatch("/api/jobs/{jobName}/toggle", ([FromRoute] string jobName, [FromServices] SchedulerService svc) =>
{
    var result = svc.ToggleJob(jobName);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.Run();
