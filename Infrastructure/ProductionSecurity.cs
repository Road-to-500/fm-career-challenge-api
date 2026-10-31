using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace FmCareerChallenge.Api.Infrastructure;

public static class ProductionSecurity
{
    public static void Configure(IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        if (isDevelopment) return;

        var hosts = configuration["AllowedHosts"];
        var hostNames = (hosts ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hostNames.Length == 0 || hostNames.Any(host => host is "*" or "+"))
            throw new InvalidOperationException("AllowedHosts must list the expected hosts outside Development.");

        var applicationName = configuration["DataProtection:ApplicationName"];
        var directory = configuration["DataProtection:KeyRingPath"];
        var certificatePath = configuration["DataProtection:CertificatePath"];
        if (string.IsNullOrWhiteSpace(applicationName) || string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException("DataProtection requires ApplicationName, KeyRingPath, and CertificatePath outside Development.");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new InvalidOperationException("DataProtection:KeyRingPath must be an existing absolute directory.");
        var certificate = LoadCertificate(certificatePath, configuration["DataProtection:CertificatePassword"],
            "DataProtection:CertificatePath");
        var previousCertificates = configuration.GetSection("DataProtection:PreviousCertificates").GetChildren()
            .Select(section => LoadCertificate(section["Path"], section["Password"],
                $"DataProtection:PreviousCertificates:{section.Key}:Path"))
            .ToArray();

        services.AddSingleton(certificate);
        foreach (var previousCertificate in previousCertificates) services.AddSingleton(previousCertificate);
        var dataProtection = services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .ProtectKeysWithCertificate(certificate);
        if (previousCertificates.Length > 0)
            dataProtection.UnprotectKeysWithAnyCertificate([certificate, .. previousCertificates]);
    }

    private static X509Certificate2 LoadCertificate(string? path, string? password, string setting)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{setting} must be an absolute path.");

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        using var privateKey = certificate.GetRSAPrivateKey();
        if (privateKey is not null) return certificate;
        certificate.Dispose();
        throw new InvalidOperationException($"{setting} must contain a certificate with an RSA private key.");
    }
}
