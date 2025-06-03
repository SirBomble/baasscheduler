using System.Diagnostics;
using BaaSScheduler;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.WindowsServices;

if (args.Contains("--install"))
{
    var exe = Process.GetCurrentProcess().MainModule!.FileName!;
    Process.Start("sc.exe", $"create BAASScheduler binPath= \"{exe}\" start= auto");
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

builder.Configuration.AddJsonFile("appsettings.json", optional: true);

builder.Services.Configure<SchedulerConfig>(builder.Configuration);
builder.Services.AddHostedService<SchedulerService>();

var config = builder.Configuration.Get<SchedulerConfig>() ?? new SchedulerConfig();

builder.WebHost.UseUrls($"http://{config.Web.Host}:{config.Web.Port}");

var app = builder.Build();

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

app.MapGet("/", () => "BAAS Scheduler running");
app.MapGet("/api/jobs", (IOptions<SchedulerConfig> cfg) => cfg.Value.Jobs.Select(j => new { j.Name, j.Schedule, j.Script }));

app.Run();
