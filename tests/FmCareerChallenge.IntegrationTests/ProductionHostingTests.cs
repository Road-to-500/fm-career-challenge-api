using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FmCareerChallenge.IntegrationTests;

public sealed class ProductionHostingTests
{
    [Fact]
    public async Task ProductionHostFiltersUnexpectedHostsAndUsesSecureCookies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fmcc-host-{Guid.NewGuid():N}");
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
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("AllowedHosts", "example.test");
                builder.UseSetting("DataProtection:ApplicationName", "fmcc-test");
                builder.UseSetting("DataProtection:KeyRingPath", keys);
                builder.UseSetting("DataProtection:CertificatePath", certificatePath);
                builder.UseSetting("DataProtection:CertificatePassword", "test-password");
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://example.test"),
            });
            var response = await client.GetAsync("/api/auth/csrf");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"),
                value => value.Contains("fmcc.csrf=") && value.Contains("secure", StringComparison.OrdinalIgnoreCase));
            using var requestToUnexpectedHost = new HttpRequestMessage(HttpMethod.Get, "https://unexpected.test/health");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(requestToUnexpectedHost)).StatusCode);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
