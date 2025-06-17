using System.Diagnostics;
using BaaSScheduler;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography.X509Certificates;

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

// Handle password generation
if (args.Contains("--generate-password"))
{
    var (password, hash) = PasswordService.GeneratePassword();
    Console.WriteLine($"Generated password: {password}");
    Console.WriteLine($"PBKDF2 hash (use this in config): {hash}");
    Console.WriteLine();
    Console.WriteLine("Copy the PBKDF2 hash to your configuration file's web.password field.");
    return;
}

// Handle help command
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("BaaS Scheduler - Background as a Service Scheduler");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  BaaSScheduler [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --config <path>         Specify custom configuration file path");
    Console.WriteLine("  --config=<path>         Specify custom configuration file path");
    Console.WriteLine("  --console               Run in console mode (not as service)");
    Console.WriteLine("  --install               Install as Windows service");
    Console.WriteLine("  --uninstall             Uninstall Windows service");    Console.WriteLine("  --generate-password     Generate a new secure password hash");
    Console.WriteLine("  --help, -h              Show this help message");
    Console.WriteLine();
    Console.WriteLine("HTTPS Configuration:");
    Console.WriteLine("  The application only serves HTTPS traffic for security.");
    Console.WriteLine("  A self-signed certificate is automatically generated on first run.");
    Console.WriteLine("  Configure web.port to change the HTTPS port (default: 5001).");
    Console.WriteLine("  Set custom certificate with web.certificatePath and web.certificatePassword.");
    Console.WriteLine();    Console.WriteLine("Password Security:");
    Console.WriteLine("  Use --generate-password to create secure PBKDF2 password hashes.");
    Console.WriteLine("  Plain text passwords are supported for backward compatibility.");
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
        // Generate a secure password by default
        var (defaultPassword, passwordHash) = PasswordService.GeneratePassword();
          var defaultConfig = new SchedulerConfig
        {
            Web = new WebConfig
            {
                Host = "localhost",
                Port = 5001, // HTTPS port
                Password = passwordHash // Store the PBKDF2 hash
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
        
        // Log the generated password for first-time setup
        if (!isService)
        {
            Console.WriteLine("=== IMPORTANT: FIRST-TIME SETUP ===");
            Console.WriteLine($"A new configuration file has been created at: {configFile}");
            Console.WriteLine($"Generated login password: {defaultPassword}");
            Console.WriteLine("Please save this password as it won't be shown again!");
            Console.WriteLine("You can generate a new password using: --generate-password");
            Console.WriteLine("=====================================");
        }
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
builder.Services.AddHostedService<CertificateBackgroundService>();
builder.Services.AddSingleton<SessionService>();

var config = builder.Configuration.Get<SchedulerConfig>() ?? new SchedulerConfig();

// Ensure certificate settings are configured using a temporary configuration service
using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var tempLogger = loggerFactory.CreateLogger<ConfigurationService>();
var tempConfigService = new ConfigurationService(tempLogger, builder.Configuration);

var certResult = tempConfigService.EnsureCertificateSettings();

if (!certResult.Success && !isService)
{
    Console.WriteLine($"Certificate setup failed: {certResult.Message}");
}

// Re-read the configuration after ensuring certificate settings
builder.Configuration.Sources.Clear();
if (isCustomConfig)
{
    builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: true);
}
else
{
    builder.Configuration.AddJsonFile(configFile, optional: true, reloadOnChange: true);
}

// Update the config with potentially new certificate settings
config = builder.Configuration.Get<SchedulerConfig>() ?? new SchedulerConfig();

// Configure HTTPS with certificate
X509Certificate2? certificate = null;

if (!string.IsNullOrEmpty(config.Web.CertificatePath) && File.Exists(config.Web.CertificatePath))
{
    // Load existing certificate
    try
    {
        certificate = CertificateService.LoadCertificateFromFile(config.Web.CertificatePath, config.Web.CertificatePassword);
        
        if (!isService)
        {
            Console.WriteLine($"Loaded existing certificate from: {config.Web.CertificatePath}");
        }
    }
    catch (Exception ex)
    {
        if (!isService)
        {
            Console.WriteLine($"Failed to load certificate from {config.Web.CertificatePath}: {ex.Message}");
            Console.WriteLine("Falling back to generate a new self-signed certificate...");
        }
    }
}

if (certificate == null)
{
    // Fallback: Generate self-signed certificate if configuration service failed
    certificate = CertificateService.CreateSelfSignedCertificate(config.Web.Host, config.Web.CertValidityDays);
    
    if (!isService)
    {
        Console.WriteLine("Generated fallback self-signed certificate (not saved to configuration)");
    }
}

// Configure Kestrel to only serve HTTPS
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(config.Web.Port, listenOptions =>
    {
        listenOptions.UseHttps(certificate);
    });
});

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

app.MapPost("/api/auth/generate-password", () =>
{
    var (password, hash) = PasswordService.GeneratePassword();
    return Results.Ok(new { 
        Password = password, 
        Hash = hash,
        Message = "Save the hash in your configuration file's web.password field and use the password to login."
    });
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

// Settings endpoints
app.MapGet("/api/settings", ([FromServices] IConfiguration config) =>
{
    var schedulerConfig = config.Get<SchedulerConfig>() ?? new SchedulerConfig();
    return Results.Ok(new
    {
        TrustSelfSignedCert = schedulerConfig.Web.TrustSelfSignedCert,
        AutoRenewCert = schedulerConfig.Web.AutoRenewCert,
        CertValidityDays = schedulerConfig.Web.CertValidityDays,
        CertificatePath = schedulerConfig.Web.CertificatePath,
        Host = schedulerConfig.Web.Host,
        Port = schedulerConfig.Web.Port
    });
});

app.MapPost("/api/settings", ([FromBody] SettingsUpdateRequest request, [FromServices] IConfiguration config, [FromServices] IConfigurationService configSvc) =>
{
    try
    {
        var result = configSvc.UpdateSettings(request);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.MapGet("/api/settings/certificate/status", ([FromServices] IConfiguration config) =>
{
    try
    {
        var schedulerConfig = config.Get<SchedulerConfig>() ?? new SchedulerConfig();
        
        // Try to load current certificate
        X509Certificate2? certificate = null;
        bool certificateExists = false;
        bool isTrusted = false;
        bool needsRenewal = false;
        DateTime? expiryDate = null;
        string? thumbprint = null;
        
        if (!string.IsNullOrEmpty(schedulerConfig.Web.CertificatePath) && File.Exists(schedulerConfig.Web.CertificatePath))
        {
            try
            {
                certificate = CertificateService.LoadCertificateFromFile(schedulerConfig.Web.CertificatePath, schedulerConfig.Web.CertificatePassword);
                certificateExists = true;
                isTrusted = CertificateService.IsCertificateTrusted(certificate);
                needsRenewal = CertificateService.ShouldRenewCertificate(certificate);
                expiryDate = certificate.NotAfter;
                thumbprint = certificate.Thumbprint;
            }
            catch
            {
                // Certificate file exists but couldn't load
                certificateExists = false;
            }
        }
        
        return Results.Ok(new
        {
            CertificateExists = certificateExists,
            IsTrusted = isTrusted,
            NeedsRenewal = needsRenewal,
            ExpiryDate = expiryDate,
            Thumbprint = thumbprint
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/settings/certificate/trust", ([FromServices] IConfiguration config) =>
{
    try
    {
        var schedulerConfig = config.Get<SchedulerConfig>() ?? new SchedulerConfig();
        
        if (string.IsNullOrEmpty(schedulerConfig.Web.CertificatePath) || !File.Exists(schedulerConfig.Web.CertificatePath))
        {
            return Results.BadRequest(new { Success = false, Message = "Certificate file not found" });
        }
        
        var certificate = CertificateService.LoadCertificateFromFile(schedulerConfig.Web.CertificatePath, schedulerConfig.Web.CertificatePassword);
        var success = CertificateService.TrustCertificate(certificate);
        
        return success 
            ? Results.Ok(new { Success = true, Message = "Certificate has been added to trusted root store" })
            : Results.BadRequest(new { Success = false, Message = "Failed to trust certificate. Run as Administrator." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/settings/certificate/renew", ([FromServices] IConfiguration config, [FromServices] IConfigurationService configSvc) =>
{
    try
    {
        var schedulerConfig = config.Get<SchedulerConfig>() ?? new SchedulerConfig();
        
        // Generate new certificate
        var newCertificate = CertificateService.CreateSelfSignedCertificate(schedulerConfig.Web.Host, schedulerConfig.Web.CertValidityDays);
        
        // Save new certificate
        var certDir = Path.GetDirectoryName(configSvc.GetConfigurationFilePath());
        var certPath = Path.Combine(certDir!, "baasscheduler.pfx");
        var certPassword = Guid.NewGuid().ToString("N")[..16];
        
        CertificateService.SaveCertificateToFile(newCertificate, certPath, certPassword);
        
        // Update configuration with new certificate details
        var updateRequest = new SettingsUpdateRequest
        {
            CertificatePath = certPath,
            CertificatePassword = certPassword
        };
        
        var result = configSvc.UpdateSettings(updateRequest);
        
        if (result.Success && schedulerConfig.Web.TrustSelfSignedCert)
        {
            // Auto-trust the new certificate if setting is enabled
            CertificateService.TrustCertificate(newCertificate);
        }
        
        return Results.Ok(new { 
            Success = true, 
            Message = "Certificate renewed successfully. Restart the application to use the new certificate.",
            ExpiryDate = newCertificate.NotAfter,
            Thumbprint = newCertificate.Thumbprint
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/settings/certificate/generate", ([FromServices] IConfigurationService configSvc) =>
{
    try
    {
        var result = configSvc.GenerateAndSaveCertificateSettings();
        
        if (result.Success)
        {
            return Results.Ok(new { 
                Success = true, 
                Message = "Certificate generated and saved to configuration successfully. Restart the application to use the new certificate."
            });
        }
        else
        {
            return Results.BadRequest(new { Success = false, Message = result.Message });
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.MapPost("/api/settings/certificate/ensure", ([FromServices] IConfigurationService configSvc) =>
{
    try
    {
        var result = configSvc.EnsureCertificateSettings();
        
        if (result.Success)
        {
            return Results.Ok(new { 
                Success = true, 
                Message = "Certificate settings verified or generated successfully."
            });
        }
        else
        {
            return Results.BadRequest(new { Success = false, Message = result.Message });
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Success = false, Message = ex.Message });
    }
});

app.Run();
