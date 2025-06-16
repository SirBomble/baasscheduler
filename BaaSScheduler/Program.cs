using System.Diagnostics;
using BaaSScheduler;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;

// optional configuration file parameter
string configFile = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var configArgIndex = Array.IndexOf(args, "--config");
if (configArgIndex >= 0 && args.Length > configArgIndex + 1)
{
    configFile = args[configArgIndex + 1];
}
else
{
    const string prefix = "--config=";
    var match = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (match != null)
    {
        configFile = match.Substring(prefix.Length);
    }
}

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

builder.Configuration.AddJsonFile(configFile, optional: true, reloadOnChange: true);

builder.Services.Configure<SchedulerConfig>(builder.Configuration);
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

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/api/jobs", ([FromServices] SchedulerService svc) => svc.GetJobs());
app.MapGet("/api/status", ([FromServices] SchedulerService svc) => svc.GetStatuses());

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
