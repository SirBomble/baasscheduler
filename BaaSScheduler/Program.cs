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

builder.Configuration.AddJsonFile(configFile, optional: true);

builder.Services.Configure<SchedulerConfig>(builder.Configuration);
builder.Services.AddHostedService<SchedulerService>();

var config = builder.Configuration.Get<SchedulerConfig>() ?? new SchedulerConfig();

builder.WebHost.UseUrls($"http://{config.Web.Host}:{config.Web.Port}");

var app = builder.Build();

var embeddedProvider = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");

app.UseDefaultFiles();
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }
    if (!context.Request.Headers.TryGetValue("X-Password", out var pw) || pw != config.Web.Password)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }
    await next();
});

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/api/jobs", ([FromServices] IOptions<SchedulerConfig> cfg) =>
    cfg.Value.Jobs.Select(j => new { j.Name, j.Schedule, j.Script }));
app.MapGet("/api/status", ([FromServices] SchedulerService svc) => svc.GetStatuses());
app.MapPost("/api/jobs", ([FromBody] JobConfig job, [FromServices] SchedulerService svc) =>
{
    svc.AddJob(job);
    return Results.Ok();
});

app.Run();
