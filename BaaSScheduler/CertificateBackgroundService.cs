using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace BaaSScheduler;

public class CertificateBackgroundService : BackgroundService
{
    private readonly ILogger<CertificateBackgroundService> _logger;
    private readonly IOptionsMonitor<SchedulerConfig> _options;
    private readonly IConfigurationService _configService;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Check every 6 hours

    public CertificateBackgroundService(
        ILogger<CertificateBackgroundService> logger,
        IOptionsMonitor<SchedulerConfig> options,
        IConfigurationService configService)
    {
        _logger = logger;
        _options = options;
        _configService = configService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Certificate background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = _options.CurrentValue;
                
                if (config.Web.AutoRenewCert)
                {
                    await CheckAndRenewCertificateAsync(config);
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is being stopped
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during certificate background check");
                // Wait a shorter time before retrying after an error
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }

        _logger.LogInformation("Certificate background service stopped");
    }

    private async Task CheckAndRenewCertificateAsync(SchedulerConfig config)
    {
        try
        {
            if (string.IsNullOrEmpty(config.Web.CertificatePath) || !File.Exists(config.Web.CertificatePath))
            {
                _logger.LogDebug("No certificate file found, skipping renewal check");
                return;
            }

            // Load current certificate
            X509Certificate2? certificate;
            try
            {
                certificate = CertificateService.LoadCertificateFromFile(
                    config.Web.CertificatePath, 
                    config.Web.CertificatePassword);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load certificate for renewal check");
                return;
            }

            // Check if renewal is needed (30 days before expiry)
            if (CertificateService.ShouldRenewCertificate(certificate, 30))
            {
                _logger.LogInformation("Certificate expires on {ExpiryDate}, renewing now", certificate.NotAfter);
                
                // Generate new certificate
                var newCertificate = CertificateService.CreateSelfSignedCertificate(
                    config.Web.Host, 
                    config.Web.CertValidityDays);

                // Save new certificate
                var certDir = Path.GetDirectoryName(config.Web.CertificatePath);
                var certPath = Path.Combine(certDir!, "baasscheduler.pfx");
                var certPassword = Guid.NewGuid().ToString("N")[..16];

                CertificateService.SaveCertificateToFile(newCertificate, certPath, certPassword);

                // Auto-trust the new certificate if setting is enabled
                if (config.Web.TrustSelfSignedCert)
                {
                    var trusted = CertificateService.TrustCertificate(newCertificate);
                    if (trusted)
                    {
                        _logger.LogInformation("New certificate has been trusted in the local machine store");
                    }
                    else
                    {
                        _logger.LogWarning("Could not trust new certificate. Application may need to run as Administrator");
                    }
                }

                // Update configuration with new certificate details
                var updateRequest = new SettingsUpdateRequest
                {
                    CertificatePath = certPath,
                    CertificatePassword = certPassword
                };

                var result = _configService.UpdateSettings(updateRequest);
                
                if (result.Success)
                {
                    _logger.LogInformation("Certificate renewed successfully. New certificate expires on {ExpiryDate}", 
                        newCertificate.NotAfter);
                    _logger.LogWarning("Application restart required to use the new certificate");
                }
                else
                {
                    _logger.LogError("Failed to update configuration with new certificate: {Message}", result.Message);
                }

                certificate.Dispose();
                newCertificate.Dispose();
            }
            else
            {
                _logger.LogDebug("Certificate is still valid until {ExpiryDate}", certificate.NotAfter);
            }

            certificate.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during certificate renewal check");
        }
    }
}
