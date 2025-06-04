using System.Diagnostics;
using BaaSScheduler;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Mvc;

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

app.UseDefaultFiles();
app.UseStaticFiles();

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
