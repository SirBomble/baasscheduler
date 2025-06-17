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
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
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
}
