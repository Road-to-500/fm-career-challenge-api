using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FmCareerChallenge.Api.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FmCareerChallenge.UnitTests;

public sealed class ProductionSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";")]
    [InlineData("*")]
    [InlineData("example.com;*")]
    [InlineData("+")]
    public void NonDevelopmentRejectsUnrestrictedHosts(string? hosts)
    {
        var configuration = Configuration(new() { ["AllowedHosts"] = hosts });
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurity.Configure(new ServiceCollection(), configuration, false));
        Assert.Contains("AllowedHosts", error.Message);
    }

    [Fact]
    public void NonDevelopmentRequiresExplicitKeyStorage()
    {
        var configuration = Configuration(new() { ["AllowedHosts"] = "example.com" });
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurity.Configure(new ServiceCollection(), configuration, false));
        Assert.Contains("DataProtection", error.Message);
    }

    [Fact]
    public void DevelopmentDoesNotRequireProductionConfiguration()
    {
        ProductionSecurity.Configure(new ServiceCollection(), Configuration(new()), true);
    }

    [Fact]
    public void ProtectedKeysSurviveRestartAndAreSharedByInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fmcc-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var keys = Path.Combine(root, "keys");
            Directory.CreateDirectory(keys);
            var certificatePath = Path.Combine(root, "certificate.pfx");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=FMCC test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-password"));
            var configuration = Configuration(new()
            {
                ["AllowedHosts"] = "example.com",
                ["DataProtection:ApplicationName"] = "fmcc-test",
                ["DataProtection:KeyRingPath"] = keys,
                ["DataProtection:CertificatePath"] = certificatePath,
                ["DataProtection:CertificatePassword"] = "test-password",
            });
            string protectedValue;
            using (var first = Provider(configuration))
                protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("session");
            var keyFile = Assert.Single(Directory.GetFiles(keys, "*.xml"));
            Assert.Contains("encryptedSecret", File.ReadAllText(keyFile));
            using var second = Provider(configuration);
            Assert.Equal("session", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PreviousEncryptionCertificateCanDecryptTheExistingKeyRingDuringRotation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fmcc-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var keys = Path.Combine(root, "keys");
            Directory.CreateDirectory(keys);
            var oldCertificatePath = Path.Combine(root, "old-certificate.pfx");
            var newCertificatePath = Path.Combine(root, "new-certificate.pfx");
            WriteCertificate(oldCertificatePath, "old-password");
            WriteCertificate(newCertificatePath, "new-password");
            var oldConfiguration = Configuration(new()
            {
                ["AllowedHosts"] = "example.com",
                ["DataProtection:ApplicationName"] = "fmcc-rotation-test",
                ["DataProtection:KeyRingPath"] = keys,
                ["DataProtection:CertificatePath"] = oldCertificatePath,
                ["DataProtection:CertificatePassword"] = "old-password",
            });
            string protectedByOldCertificate;
            using (var oldProvider = Provider(oldConfiguration))
                protectedByOldCertificate = oldProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("test").Protect("existing session");

            var rotatedConfiguration = Configuration(new()
            {
                ["AllowedHosts"] = "example.com",
                ["DataProtection:ApplicationName"] = "fmcc-rotation-test",
                ["DataProtection:KeyRingPath"] = keys,
                ["DataProtection:CertificatePath"] = newCertificatePath,
                ["DataProtection:CertificatePassword"] = "new-password",
                ["DataProtection:PreviousCertificates:0:Path"] = oldCertificatePath,
                ["DataProtection:PreviousCertificates:0:Password"] = "old-password",
            });
            using var rotatedProvider = Provider(rotatedConfiguration);
            var protector = rotatedProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            Assert.Equal("existing session", protector.Unprotect(protectedByOldCertificate));
            var protectedByNewCertificate = protector.Protect("new session");
            using var restartedProvider = Provider(rotatedConfiguration);
            Assert.Equal("new session", restartedProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("test").Unprotect(protectedByNewCertificate));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Provider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        ProductionSecurity.Configure(services, configuration, false);
        return services.BuildServiceProvider();
    }

    private static void WriteCertificate(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=FMCC test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
    }
}
