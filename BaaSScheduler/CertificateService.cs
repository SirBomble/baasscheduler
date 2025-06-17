using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BaaSScheduler;

public static class CertificateService
{
    /// <summary>
    /// Creates a self-signed certificate for HTTPS
    /// </summary>
    /// <param name="subjectName">Subject name for the certificate</param>
    /// <param name="validDays">Number of days the certificate is valid</param>
    /// <returns>X509Certificate2 with private key</returns>
    public static X509Certificate2 CreateSelfSignedCertificate(string subjectName = "localhost", int validDays = 365)
    {
        using var rsa = RSA.Create(2048);
        
        var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
          // Add Subject Alternative Names
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(subjectName);
        sanBuilder.AddDnsName("localhost");
        
        // Add the actual computer name
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddDnsName($"{Environment.MachineName}.local");
        
        // Add IP addresses
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        
        // Add local network IP addresses
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    sanBuilder.AddIpAddress(ip);
                }
            }
        }
        catch
        {
            // Ignore errors when getting network IPs
        }
        
        request.CertificateExtensions.Add(sanBuilder.Build());
        
        // Add Enhanced Key Usage for server authentication
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                false));
        
        // Add Key Usage
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                false));

        var certificate = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(validDays));
        
        // Return certificate with exportable private key
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Saves a certificate to a PFX file
    /// </summary>
    /// <param name="certificate">Certificate to save</param>
    /// <param name="filePath">Path to save the certificate</param>
    /// <param name="password">Password for the PFX file</param>
    public static void SaveCertificateToFile(X509Certificate2 certificate, string filePath, string password)
    {
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(filePath, pfxBytes);
    }

    /// <summary>
    /// Loads a certificate from a PFX file
    /// </summary>
    /// <param name="filePath">Path to the PFX file</param>
    /// <param name="password">Password for the PFX file</param>
    /// <returns>X509Certificate2</returns>
    public static X509Certificate2 LoadCertificateFromFile(string filePath, string password)
    {
        return new X509Certificate2(filePath, password, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Installs a certificate to the Local Machine's Trusted Root Certification Authorities store
    /// </summary>
    /// <param name="certificate">Certificate to trust</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool TrustCertificate(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            
            // Check if certificate is already trusted
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            if (existing.Count > 0)
            {
                return true; // Already trusted
            }
            
            store.Add(certificate);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a certificate from the Local Machine's Trusted Root Certification Authorities store
    /// </summary>
    /// <param name="certificate">Certificate to remove</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool RemoveTrustedCertificate(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            if (existing.Count > 0)
            {
                store.Remove(existing[0]);
            }
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a certificate is trusted in the Local Machine's Trusted Root Certification Authorities store
    /// </summary>
    /// <param name="certificate">Certificate to check</param>
    /// <returns>True if trusted, false otherwise</returns>
    public static bool IsCertificateTrusted(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            return existing.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a certificate needs renewal based on expiration date
    /// </summary>
    /// <param name="certificate">Certificate to check</param>
    /// <param name="daysBeforeExpiry">Days before expiry to consider renewal needed</param>
    /// <returns>True if renewal is needed, false otherwise</returns>
    public static bool ShouldRenewCertificate(X509Certificate2 certificate, int daysBeforeExpiry = 30)
    {
        return certificate.NotAfter <= DateTime.Now.AddDays(daysBeforeExpiry);
    }
}
