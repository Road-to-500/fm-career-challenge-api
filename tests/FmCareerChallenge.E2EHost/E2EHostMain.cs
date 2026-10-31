using System.Security.Claims;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using FmCareerChallenge.Api.Domain;
using FmCareerChallenge.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

public static class E2EHostMain
{
    public static async Task Main()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        var connectionString = Environment.GetEnvironmentVariable("FMCC_E2E_POSTGRES")
            ?? throw new InvalidOperationException("Set FMCC_E2E_POSTGRES to a dedicated test database.");
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Database is null || !connection.Database.StartsWith("fmcc_e2e_", StringComparison.Ordinal))
            throw new InvalidOperationException("The E2E database name must start with fmcc_e2e_.");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseUrls("http://127.0.0.1:5196");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] = connectionString,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ChallengeDbContext>>();
                services.AddDbContext<ChallengeDbContext>(options => options.UseNpgsql(connectionString));
                services.AddSingleton<E2EIdentityRegistry>();
                services.AddTransient<IStartupFilter, E2EEndpoints>();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = E2EAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = E2EAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, E2EAuthenticationHandler>(E2EAuthenticationHandler.SchemeName, _ => { });
            });
        });
        factory.UseKestrel(5196);
        factory.StartServer();
        Console.WriteLine("E2E API ready at http://127.0.0.1:5196");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    public static ApplicationUser MakeUser(Guid id, string name)
    {
        var email = $"{id:N}@example.test";
        return new ApplicationUser
        {
            Id = id, UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = name,
            HasCompletedOnboarding = true, SecurityStamp = Guid.NewGuid().ToString(),
        };
    }
}

public sealed record E2EIdentities(Guid Owner, Guid Rival);

public sealed class E2EIdentityRegistry
{
    public ConcurrentDictionary<Guid, byte> Users { get; } = new();
}

public sealed class E2EEndpoints : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, proceed) =>
        {
            if (context.Request.Method == "POST" && context.Request.Path == "/api/_e2e/identities")
            {
                var identities = new E2EIdentities(Guid.NewGuid(), Guid.NewGuid());
                var database = context.RequestServices.GetRequiredService<ChallengeDbContext>();
                database.Users.AddRange(
                    E2EHostMain.MakeUser(identities.Owner, "Owner"),
                    E2EHostMain.MakeUser(identities.Rival, "Rival"));
                await database.SaveChangesAsync();
                var users = context.RequestServices.GetRequiredService<E2EIdentityRegistry>().Users;
                users.TryAdd(identities.Owner, 0);
                users.TryAdd(identities.Rival, 0);
                await context.Response.WriteAsJsonAsync(identities);
                return;
            }
            await proceed();
        });
        next(app);
    };
}

public sealed class E2EAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    E2EIdentityRegistry identities) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "E2EOnly";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Cookies["fmcc.e2e"], out var id) || !identities.Users.ContainsKey(id))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
