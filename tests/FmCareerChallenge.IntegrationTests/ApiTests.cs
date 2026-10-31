using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FmCareerChallenge.Api.Domain;
using FmCareerChallenge.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace FmCareerChallenge.IntegrationTests;

public sealed class ApiTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private WebApplicationFactory<Program> factory = null!;
    private string connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("FMCC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("fmcc_api_test")
                .WithUsername("fmcc_test")
                .WithPassword(Guid.NewGuid().ToString("N"))
                .Build();
            await container.StartAsync();
            connectionString = container.GetConnectionString();
        }
        else connectionString = configuredConnection;

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PostgreSql"] = connectionString,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<ChallengeDbContext>>();
                services.AddDbContext<ChallengeDbContext>(options => options.UseNpgsql(connectionString));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task GoogleSignInRequiresCredentialsAndDoesNotExposeOtherProviders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var googleResponse = await client.GetAsync("/api/auth/google");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, googleResponse.StatusCode);
        Assert.Contains("Google sign-in is not configured", await googleResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/microsoft")).StatusCode);
    }

    [Fact]
    public async Task GoogleSignInRedirectsToGoogleAndUsesTheConfiguredCallback()
    {
        await using var googleFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Google:ClientId"] = "integration-test-client",
                    ["Authentication:Google:ClientSecret"] = "integration-test-secret",
                }));
            builder.ConfigureTestServices(services => services.AddAuthentication()
                .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
                {
                    options.ClientId = "integration-test-client";
                    options.ClientSecret = "integration-test-secret";
                }));
        });
        using var client = googleFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/google");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("accounts.google.com", response.Headers.Location!.Host);
        Assert.Contains("/signin-google", Uri.UnescapeDataString(response.Headers.Location.Query));
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location.Query)["state"].ToString();
        var options = googleFactory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(GoogleDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(state);
        Assert.NotNull(properties);
        Assert.Equal(GoogleDefaults.AuthenticationScheme, properties!.Items["LoginProvider"]);
        Assert.Equal("/api/auth/external-callback", properties.RedirectUri);
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        if (container is not null) await container.DisposeAsync();
    }

    [Fact]
    public async Task RateLimitsAuthenticatedUsersSeparatelyAndRetainsAnonymousIpLimit()
    {
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        using var anonymous = factory.CreateClient();
        first.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        second.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());

        for (var index = 0; index < 125; index++)
        {
            Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/health")).StatusCode);
        }
        // InitializeAsync already made one anonymous health request.
        for (var index = 0; index < 119; index++)
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task ForwardedHttpsIsHonoredOnlyFromConfiguredProxy()
    {
        await using var proxyFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = "192.0.2.10",
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestProxyAddressFilter>());
        });
        using var trusted = proxyFactory.CreateClient();
        trusted.DefaultRequestHeaders.Add("X-Test-Proxy", "192.0.2.10");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");
        var trustedResponse = await trusted.GetAsync("/api/auth/csrf");
        Assert.Contains(trustedResponse.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("fmcc.csrf=") && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("203.0.113.1", trustedResponse.Headers.GetValues("X-Test-Observed-IP").Single());
        Assert.Equal("localhost", trustedResponse.Headers.GetValues("X-Test-Observed-Host").Single());
        using var untrusted = proxyFactory.CreateClient();
        untrusted.DefaultRequestHeaders.Add("X-Test-Proxy", "192.0.2.11");
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        var untrustedResponse = await untrusted.GetAsync("/api/auth/csrf");
        Assert.Contains(untrustedResponse.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("fmcc.csrf=") && !cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("192.0.2.11", untrustedResponse.Headers.GetValues("X-Test-Observed-IP").Single());
        await using var noProxyFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestProxyAddressFilter>()));
        using var noProxy = noProxyFactory.CreateClient();
        noProxy.DefaultRequestHeaders.Add("X-Test-Proxy", "127.0.0.1");
        noProxy.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var noProxyResponse = await noProxy.GetAsync("/api/auth/csrf");
        Assert.Contains(noProxyResponse.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("fmcc.csrf=") && !cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeographyCatalogRequiresAuthenticationAndReturnsCountryContinents()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/geography")).StatusCode);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(user);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(user.Id);
        var catalog = (await client.GetFromJsonAsync<TestGeography>("/api/geography"))!;
        Assert.Contains(catalog.Continents, item => item.Code == "EU" && item.Name == "Europe");
        Assert.Contains(catalog.Countries, item => item.Code == "PT" && item.Name == "Portugal" && item.ContinentCode == "EU");
        Assert.Contains(catalog.Countries, item => item.Code == "GB-ENG" && item.Name == "England");
    }

    [Fact]
    public async Task ClubCatalogSupportsSelectionAndPreservesLegacyStints()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/clubs")).StatusCode);
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var unique = $"Catalog Club {Guid.NewGuid():N}";
        var createdClub = await client.PostAsJsonAsync("/api/clubs", new { name = unique, countryCode = "PT" });
        Assert.Equal(HttpStatusCode.Created, createdClub.StatusCode);
        var catalogClub = (await createdClub.Content.ReadFromJsonAsync<TestCatalogClub>())!;
        Assert.Equal("Portugal", catalogClub.CountryName);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/clubs",
            new { name = $" {unique.ToLowerInvariant()} ", countryCode = "pt" })).StatusCode);
        var distinctClubResponse = await client.PostAsJsonAsync("/api/clubs",
            new { name = unique, countryCode = "PT", identityLabel = "Women's team" });
        Assert.Equal(HttpStatusCode.Created, distinctClubResponse.StatusCode);
        var distinctClub = (await distinctClubResponse.Content.ReadFromJsonAsync<TestCatalogClub>())!;
        Assert.Equal("Women's team", distinctClub.IdentityLabel);
        Assert.NotEqual(catalogClub.Id, distinctClub.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/clubs",
            new { name = unique, countryCode = "PT", identityLabel = " women's TEAM " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/clubs",
            new { name = unique, countryCode = "UNKNOWN" })).StatusCode);
        var found = (await client.GetFromJsonAsync<TestCatalogClub[]>($"/api/clubs?search={unique[^8..]}&countryCode=PT"))!;
        Assert.Contains(found, item => item.Id == catalogClub.Id);
        Assert.Contains(found, item => item.Id == distinctClub.Id && item.IdentityLabel == "Women's team");
        Assert.Equal(catalogClub.Id, (await client.GetFromJsonAsync<TestCatalogClub>($"/api/clubs/{catalogClub.Id}"))!.Id);
        Assert.Equal("Women's team", (await client.GetFromJsonAsync<TestCatalogClub>($"/api/clubs/{distinctClub.Id}"))!.IdentityLabel);

        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Club catalog", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var legacyClaimResponse = await client.PostAsJsonAsync(claimsUrl, new
        {
            code = "G040", seasonIndex = 0, clubName = unique, countryCode = "PT", notes = "",
            firstMajorClubTrophy = true, trophyNotSuperCup = true,
        });
        Assert.Equal(HttpStatusCode.Created, legacyClaimResponse.StatusCode);
        var legacyClaim = (await legacyClaimResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        var distinctClaim = new
        {
            code = "G040", seasonIndex = 0, clubName = unique, countryCode = "PT", notes = "",
            firstMajorClubTrophy = true, trophyNotSuperCup = true, clubId = distinctClub.Id,
        };
        var ambiguousConflict = await client.PostAsJsonAsync(claimsUrl, distinctClaim);
        Assert.Equal(HttpStatusCode.Conflict, ambiguousConflict.StatusCode);
        Assert.Contains("link the earlier claim", await ambiguousConflict.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            var input = await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>()
                .GetInputAsync(challenge.Id, CancellationToken.None);
            Assert.Contains(input.Claims, item => item.Id == legacyClaim.Id && item.ClubId is null && item.ClubIdentityAmbiguous);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{legacyClaim.Id}", new
        {
            seasonIndex = 0, clubName = unique, countryCode = "PT", notes = "", version = 0L,
            firstMajorClubTrophy = true, trophyNotSuperCup = true, clubId = catalogClub.Id,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, distinctClaim)).StatusCode);
        var recordedClaims = (await client.GetFromJsonAsync<TestAchievementClaims[]>($"/api/challenges/{challenge.Id}/achievements"))!
            .Single(item => item.Code == "G040").Claims;
        Assert.Contains(recordedClaims, item => item.ClubId == catalogClub.Id);
        Assert.Contains(recordedClaims, item => item.ClubId == distinctClub.Id);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var stint = new
        {
            clubName = unique, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = (long?)null, clubId = (Guid?)catalogClub.Id, clearClubId = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{careerUrl}/stints",
            stint with { clubName = "Wrong club" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{careerUrl}/stints",
            stint with { clubId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{careerUrl}/stints", stint)).StatusCode);
        var saved = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs);
        Assert.Equal(catalogClub.Id, saved.ClubId);
        var legacyEdit = new
        {
            clubName = unique, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = 0L,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{careerUrl}/stints/{saved.Id}", legacyEdit)).StatusCode);
        Assert.Equal(catalogClub.Id, Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs).ClubId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{careerUrl}/stints/{saved.Id}",
            stint with { version = 1L, clearClubId = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{careerUrl}/stints/{saved.Id}", new
        {
            clubName = unique, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = 1L, clearClubId = true,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{careerUrl}/stints/{saved.Id}", new
        {
            clubName = "Corrected name", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = 1L, clearClubId = true,
        })).StatusCode);
        Assert.Null(Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs).ClubId);
        var unresolvedDuplicate = await client.PostAsJsonAsync($"{careerUrl}/stints", new
        {
            clubName = unique, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 1,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = (long?)null,
        });
        Assert.Equal(HttpStatusCode.Conflict, unresolvedDuplicate.StatusCode);
        Assert.Contains("Select the intended club identity", await unresolvedDuplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{careerUrl}/stints", new
        {
            clubName = unique, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 1,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = (long?)null, clubId = catalogClub.Id,
        })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var recalculator = scope.ServiceProvider.GetRequiredService<ScoreRecalculator>();
            var scoringInput = await recalculator.GetInputAsync(challenge.Id, CancellationToken.None);
            Assert.Contains(scoringInput.Clubs, item => item.ClubName == unique && item.ClubId == catalogClub.Id);
            Assert.Contains(scoringInput.Claims, item => item.ClubName == unique && item.ClubId == catalogClub.Id);
            Assert.Contains(scoringInput.Claims, item => item.ClubName == unique && item.ClubId == distinctClub.Id);
            var claimIds = recordedClaims.Select(item => item.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scoredClaims = RaceScoringEngine.Calculate(scoringInput).Events
                .Where(item => item.SourceType == "Achievement" && claimIds.Contains(item.SourceKey)).ToArray();
            Assert.Equal(2, scoredClaims.Length);
            Assert.All(scoredClaims, item => Assert.Equal(5, item.Points));
        }
    }

    [Fact]
    public async Task CatalogIdentityCombinesLinkedAndMatchingLegacyCareerStints()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var name = $"Return Club {Guid.NewGuid():N}";
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Club identity", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/career";
        var legacy = new
        {
            clubName = name, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 12, eligibleSeasonIndex = (int?)null,
            clubId = (Guid?)null,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{url}/stints", legacy)).StatusCode);
        var club = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{url}/stints", legacy with
        {
            startSeasonIndex = 1, endSeasonIndex = (int?)1, officialMatches = 8, eligibleSeasonIndex = (int?)1, clubId = club.Id,
        })).StatusCode);
        var stints = (await client.GetFromJsonAsync<TestCareer>(url))!.Clubs;
        Assert.Equal(2, stints.Count);
        Assert.All(stints, item =>
        {
            Assert.Equal(20, item.ClubOfficialMatches);
            Assert.Equal(1, item.ClubEligibleSeasonIndex);
        });
        Assert.Contains(stints, item => item.ClubId == null);
        Assert.Contains(stints, item => item.ClubId == club.Id);
        RaceScoringResult before;
        using (var scope = factory.Services.CreateScope())
        {
            var input = await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>()
                .GetInputAsync(challenge.Id, CancellationToken.None);
            before = RaceScoringEngine.Calculate(input);
        }
        var duplicate = await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT", identityLabel = "Reserves" });
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        var afterStints = (await client.GetFromJsonAsync<TestCareer>(url))!.Clubs;
        Assert.Equal(2, afterStints.Count);
        Assert.All(afterStints, item =>
        {
            Assert.Equal(20, item.ClubOfficialMatches);
            Assert.Equal(1, item.ClubEligibleSeasonIndex);
        });
        Assert.Contains(afterStints, item => item.ClubId is null);
        Assert.Contains(afterStints, item => item.ClubId == club.Id);
        using (var scope = factory.Services.CreateScope())
        {
            var input = await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>()
                .GetInputAsync(challenge.Id, CancellationToken.None);
            var after = RaceScoringEngine.Calculate(input);
            Assert.Equal(before.Participants.ToArray(), after.Participants.ToArray());
            Assert.Equal(before.Events.OrderBy(item => item.SourceKey), after.Events.OrderBy(item => item.SourceKey));
        }
    }

    [Fact]
    public async Task CareerIdentityDisambiguationRequiresLinkingLegacyStintsBeforeAddingDuplicate()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var name = $"Shared Career Club {Guid.NewGuid():N}";
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Career identity disambiguation", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/career";
        var legacy = new
        {
            clubName = name, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 12, eligibleSeasonIndex = (int?)null,
            clubId = (Guid?)null,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{url}/stints", legacy)).StatusCode);
        var firstClub = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{url}/stints", legacy with
        {
            startSeasonIndex = 1, endSeasonIndex = (int?)null, officialMatches = 8, eligibleSeasonIndex = (int?)1,
            clubId = firstClub.Id,
        })).StatusCode);
        var duplicate = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT", identityLabel = "Reserves" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        var secondStint = new
        {
            clubName = name, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 2,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 0,
            eligibleSeasonIndex = (int?)null, version = (long?)null, clubId = (Guid?)duplicate.Id, clearClubId = false,
        };
        var blocked = await client.PostAsJsonAsync($"{url}/stints", secondStint);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Link existing text-only stints", await blocked.Content.ReadAsStringAsync());
        var before = (await client.GetFromJsonAsync<TestCareer>(url))!;
        Assert.All(before.Clubs, item =>
        {
            Assert.Equal(20, item.ClubOfficialMatches);
            Assert.Equal(1, item.ClubEligibleSeasonIndex);
        });
        RaceScoringResult scoreBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var input = await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>()
                .GetInputAsync(challenge.Id, CancellationToken.None);
            scoreBefore = RaceScoringEngine.Calculate(input);
        }
        var oldStint = before.Clubs.Single(item => item.ClubId is null);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/stints/{oldStint.Id}", new
        {
            clubName = name, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 12,
            eligibleSeasonIndex = (int?)null, version = oldStint.Version, clubId = firstClub.Id, clearClubId = false,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{url}/stints", secondStint)).StatusCode);
        var after = (await client.GetFromJsonAsync<TestCareer>(url))!;
        Assert.All(after.Clubs.Where(item => item.ClubId == firstClub.Id), item =>
        {
            Assert.Equal(20, item.ClubOfficialMatches);
            Assert.Equal(1, item.ClubEligibleSeasonIndex);
        });
        Assert.Equal(3, after.Clubs.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var input = await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>()
                .GetInputAsync(challenge.Id, CancellationToken.None);
            var scoreAfter = RaceScoringEngine.Calculate(input);
            Assert.Equal(scoreBefore.Participants.ToArray(), scoreAfter.Participants.ToArray());
            Assert.Equal(scoreBefore.Events.OrderBy(item => item.SourceKey), scoreAfter.Events.OrderBy(item => item.SourceKey));
        }
    }

    [Fact]
    public async Task AchievementClaimsCanLinkCatalogClubsWithoutBreakingLegacyEdits()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var name = $"Trophy Club {Guid.NewGuid():N}";
        var club = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Claim identity", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var claim = new
        {
            code = "G040", seasonIndex = 0, clubName = name, countryCode = "PT", notes = "",
            firstMajorClubTrophy = true, trophyNotSuperCup = true, clubId = (Guid?)club.Id,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl,
            claim with { clubName = "Wrong club" })).StatusCode);
        var response = await client.PostAsJsonAsync(claimsUrl, claim);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(claimsUrl,
            claim with { clubId = null, seasonIndex = 1 })).StatusCode);
        var achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>($"/api/challenges/{challenge.Id}/achievements"))!;
        Assert.Equal(club.Id, Assert.Single(achievements.Single(item => item.Code == "G040").Claims).ClubId);
        var update = new
        {
            seasonIndex = 0, clubName = name, countryCode = "PT", notes = "", version = 0L,
            firstMajorClubTrophy = true, trophyNotSuperCup = true,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{created.Id}", update)).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>($"/api/challenges/{challenge.Id}/achievements"))!;
        Assert.Equal(club.Id, Assert.Single(achievements.Single(item => item.Code == "G040").Claims).ClubId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{created.Id}", new
        {
            seasonIndex = 0, clubName = name, countryCode = "PT", notes = "", version = 1L,
            firstMajorClubTrophy = true, trophyNotSuperCup = true, clearClubId = true,
        })).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>($"/api/challenges/{challenge.Id}/achievements"))!;
        Assert.Null(Assert.Single(achievements.Single(item => item.Code == "G040").Claims).ClubId);
    }

    [Fact]
    public async Task ContinentalTitleFirstClubLinkSupportsLegacyEditsAndExplicitUnlink()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var firstName = $"First title club {Guid.NewGuid():N}";
        var secondName = $"Second title club {Guid.NewGuid():N}";
        var first = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name = firstName, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        var second = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name = secondName, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Title club identity", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/achievement-claims";
        var request = new
        {
            code = "G032", seasonIndex = 2, clubName = secondName, countryCode = "PT", clubId = (Guid?)second.Id, notes = "",
            firstContinentalTitleClub = firstName, firstContinentalTitleCountry = "PT",
            firstContinentalTitleSeason = 1, wonContinentalTitlesWithBothClubs = true,
            firstContinentalTitleClubId = (Guid?)first.Id, clearFirstContinentalTitleClubId = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { firstContinentalTitleClubId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { firstContinentalTitleClubId = second.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { clearFirstContinentalTitleClubId = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with
        {
            firstContinentalTitleClub = secondName, firstContinentalTitleClubId = second.Id,
        })).StatusCode);
        var response = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var achievementsUrl = $"/api/challenges/{challenge.Id}/achievements";
        var achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementsUrl))!;
        Assert.Equal(first.Id, Assert.Single(achievements.Single(item => item.Code == "G032").Claims).FirstContinentalTitleClubId);
        var oldClientUpdate = new
        {
            seasonIndex = 2, clubName = secondName, countryCode = "PT", notes = "", version = 0L,
            firstContinentalTitleClub = firstName, firstContinentalTitleCountry = "PT",
            firstContinentalTitleSeason = 1, wonContinentalTitlesWithBothClubs = true,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{id}", oldClientUpdate)).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementsUrl))!;
        Assert.Equal(first.Id, Assert.Single(achievements.Single(item => item.Code == "G032").Claims).FirstContinentalTitleClubId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{id}", new
        {
            seasonIndex = 2, clubName = secondName, countryCode = "PT", notes = "", version = 1L,
            firstContinentalTitleClub = firstName, firstContinentalTitleCountry = "PT",
            firstContinentalTitleSeason = 1, wonContinentalTitlesWithBothClubs = true, clearFirstContinentalTitleClubId = true,
        })).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementsUrl))!;
        Assert.Null(Assert.Single(achievements.Single(item => item.Code == "G032").Claims).FirstContinentalTitleClubId);
    }

    [Fact]
    public async Task CareerMoveStartingClubLinkIsValidatedAndPreservedForLegacyEdits()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var name = $"Starting club {Guid.NewGuid():N}";
        var catalog = (await (await client.PostAsJsonAsync("/api/clubs",
            new { name, countryCode = "PT" })).Content.ReadFromJsonAsync<TestCatalogClub>())!;
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Starting identity", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var stint = new
        {
            clubName = name, countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 20,
            eligibleSeasonIndex = (int?)0, clubId = (Guid?)catalog.Id,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{careerUrl}/stints", stint)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{careerUrl}/stints", stint with
        {
            clubName = "Next club", countryCode = "ES", countryName = "Spain", startSeasonIndex = 1,
            endSeasonIndex = null, eligibleSeasonIndex = 1, clubId = null,
        })).StatusCode);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var request = new
        {
            code = "G037", seasonIndex = 1, clubName = "Next club", countryCode = "ES", notes = "",
            startingCareerClub = name, startingCareerCountry = "PT", startingCareerClubId = (Guid?)catalog.Id,
            clearStartingCareerClubId = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, request with { startingCareerClubId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, request with { startingCareerClub = "Wrong club" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, request with { clearStartingCareerClubId = true })).StatusCode);
        var createdResponse = await client.PostAsJsonAsync(claimsUrl, request);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        var achievementUrl = $"/api/challenges/{challenge.Id}/achievements";
        var achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementUrl))!;
        Assert.Equal(catalog.Id, Assert.Single(achievements.Single(item => item.Code == "G037").Claims).StartingCareerClubId);
        var legacyUpdate = new
        {
            seasonIndex = 1, clubName = "Next club", countryCode = "ES", notes = "", version = 0L,
            startingCareerClub = name, startingCareerCountry = "PT",
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{created.Id}", legacyUpdate)).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementUrl))!;
        Assert.Equal(catalog.Id, Assert.Single(achievements.Single(item => item.Code == "G037").Claims).StartingCareerClubId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{created.Id}", new
        {
            seasonIndex = 1, clubName = "Next club", countryCode = "ES", notes = "", version = 1L,
            startingCareerClub = name, startingCareerCountry = "PT", clearStartingCareerClubId = true,
        })).StatusCode);
        achievements = (await client.GetFromJsonAsync<TestAchievementClaims[]>(achievementUrl))!;
        Assert.Null(Assert.Single(achievements.Single(item => item.Code == "G037").Claims).StartingCareerClubId);
        var score = (await client.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
    }

    [Fact]
    public async Task DraftCanBeConfiguredPublishedAndJoinedButNotPlayedOrInvitedEarly()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var email = $"{Guid.NewGuid():N}@example.test";
        var rival = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival",
            Email = email, NormalizedEmail = email.ToUpperInvariant(),
        };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var rivalClient = await CreateAuthenticatedClient(rival.Id);
        var created = await ownerClient.PostAsJsonAsync("/api/challenges", new
        {
            name = "Draft challenge", startSeasonYear = 2026, saveAsDraft = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var challenge = (await created.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        var baseUrl = $"/api/challenges/{challenge.Id}";
        var cards = await ownerClient.GetFromJsonAsync<List<TestChallengeCard>>("/api/me/challenges");
        Assert.Equal("Draft", Assert.Single(cards!, item => item.Id == challenge.Id).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/invites", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rivalClient.PostAsJsonAsync($"{baseUrl}/publish", new { version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/achievement-claims",
            new { code = "G008", seasonIndex = 0, managedCompetitionMatches = 2, managedDecidingMatch = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/career/stints",
            new { clubName = "Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0, initialDivisionLevel = 1, officialMatches = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/national-teams",
            new { teamName = "Portugal", startSeasonIndex = 0, officialMatches = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/transfers",
            new { seasonIndex = 0, clubName = "Club", playerName = "Player", direction = "Outgoing", fixedAmount = 10, currency = "EUR" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"{baseUrl}/archive", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"{baseUrl}/restore", new { confirm = true })).StatusCode);
        cards = await ownerClient.GetFromJsonAsync<List<TestChallengeCard>>("/api/me/challenges");
        Assert.Equal("Draft", Assert.Single(cards!, item => item.Id == challenge.Id).Status);
        var rulesUrl = $"{baseUrl}/rules";
        var book = (await ownerClient.GetFromJsonAsync<TestRuleBook>(rulesUrl))!;
        var ruleChange = new
        {
            code = "G008", points = 20, isEnabled = true, policy = "FromSeason", effectiveSeasonIndex = 0,
            expectedVersion = 1, expectedChallengeVersion = book.ChallengeVersion, confirm = true, reason = "Draft setup",
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(rulesUrl, ruleChange)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/publish",
            new { version = book.ChallengeVersion })).StatusCode);
        var updatedBook = (await ownerClient.GetFromJsonAsync<TestRuleBook>(rulesUrl))!;
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"{baseUrl}/publish",
            new { version = updatedBook.ChallengeVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"{baseUrl}/publish",
            new { version = updatedBook.ChallengeVersion })).StatusCode);
        var invite = await ownerClient.PostAsJsonAsync($"{baseUrl}/invites", new { email });
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        var invitation = (await invite.Content.ReadFromJsonAsync<TestInvite>())!;
        Assert.Equal(HttpStatusCode.OK, (await rivalClient.PostAsync($"/api/me/invitations/{invitation.Id}/accept", null)).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        Assert.Equal(ChallengeStatus.Active, (await verify.Challenges.SingleAsync(item => item.Id == challenge.Id)).Status);
        Assert.True(await verify.AuditLogs.AnyAsync(item => item.ChallengeId == challenge.Id && item.Action == "Published"));
        Assert.True(await verify.ChallengeRules.AnyAsync(item => item.ChallengeId == challenge.Id
            && item.AchievementCode == "G008" && item.VersionNumber == 2 && item.Points == 20));
    }

    [Fact]
    public async Task AchievementNotifiesOpponentAndOtherPlayersCannotEditAClub()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "API test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var opponentCareer = new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" };
        var stint = new CareerStint
        {
            CareerId = ownerCareer.Id, ClubName = "Test Club", CountryCode = "PT", CountryName = "Portugal", InitialDivisionLevel = 2,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant);
            database.Careers.AddRange(ownerCareer, opponentCareer);
            database.CareerStints.Add(stint);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed },
                new CareerSeason { CareerId = opponentCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
        }

        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var claimResponse = await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/achievement-claims",
            new { code = "G008", seasonIndex = 0, clubName = "Test Club", countryCode = "PT", notes = "", managedCompetitionMatches = 2, managedDecidingMatch = true });
        Assert.Equal(HttpStatusCode.Created, claimResponse.StatusCode);
        var claim = await claimResponse.Content.ReadFromJsonAsync<TestCreatedClaim>();
        Assert.NotNull(claim);

        var notifications = await opponentClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.NotNull(notifications);
        Assert.Contains(notifications, item => item.Type == "achievement.created" && item.ChallengeId == challenge.Id);
        Assert.Contains(notifications, item => item.Type == "bonus.confirmed" && item.ChallengeId == challenge.Id);

        foreach (var code in new[] { "G001", "G020" })
        {
            var response = await ownerClient.PostAsJsonAsync(
                $"/api/challenges/{challenge.Id}/achievement-claims",
                new { code, seasonIndex = 0, clubName = "Test Club", countryCode = "PT", notes = "", managedCompetitionMatches = 13, totalCompetitionMatches = 38, inChargeAtCompetitionEnd = true,
                    competitionResultKind = "ClubContinentalTop", competitionResultStage = "Winner" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        var opponentNotifications = await opponentClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        var ownerNotifications = await ownerClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.NotNull(opponentNotifications);
        Assert.NotNull(ownerNotifications);
        Assert.Contains(opponentNotifications, item => item.Type == "checkpoint.lost" && item.ChallengeId == challenge.Id);
        Assert.Contains(ownerNotifications, item => item.Type == "checkpoint.won" && item.ChallengeId == challenge.Id);

        var editResponse = await opponentClient.PatchAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}/matches",
            new { officialMatches = 20, eligibleSeasonIndex = 0, version = 0L });
        Assert.Equal(HttpStatusCode.NotFound, editResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}",
            new
            {
                clubName = "Test Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
                endSeasonIndex = (int?)null, initialDivisionLevel = 2, officialMatches = 0,
                eligibleSeasonIndex = (int?)null, version = 0L,
            })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.DeleteAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}?version=0")).StatusCode);

        var noMilestoneResponse = await ownerClient.PatchAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}/matches",
            new { officialMatches = 20, eligibleSeasonIndex = (int?)null, version = 0L });
        Assert.Equal(HttpStatusCode.OK, noMilestoneResponse.StatusCode);
        var pendingCareer = (await ownerClient.GetFromJsonAsync<TestCareer>($"/api/challenges/{challenge.Id}/career"))!;
        Assert.Null(Assert.Single(pendingCareer.Clubs).ClubEligibleSeasonIndex);

        var ownerResponse = await ownerClient.PatchAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}/matches",
            new { officialMatches = 20, eligibleSeasonIndex = 2, version = 1L });
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PatchAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{stint.Id}/matches",
            new { officialMatches = 21, eligibleSeasonIndex = 2, version = 0L })).StatusCode);
        var career = await ownerClient.GetFromJsonAsync<TestCareer>($"/api/challenges/{challenge.Id}/career");
        Assert.NotNull(career);
        var savedStint = Assert.Single(career.Clubs);
        Assert.Equal(20, savedStint.OfficialMatches);
        Assert.Equal(2, savedStint.EligibleSeasonIndex);
        var notificationsAfterRecalculation = await ownerClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.NotNull(notificationsAfterRecalculation);
        Assert.Equal(
            ownerNotifications.Count(item => item.Type == "checkpoint.won" && item.ChallengeId == challenge.Id),
            notificationsAfterRecalculation.Count(item => item.Type == "checkpoint.won" && item.ChallengeId == challenge.Id));

        var seasonUrl = $"/api/challenges/{challenge.Id}/seasons/0";
        var reopen = new
        {
            leagueMatches = 2, leagueWins = 1, leagueDraws = 1, leagueLosses = 0, leaguePoints = 4,
            goalsFor = 3, goalsAgainst = 1, leaguePosition = 2, largestOutgoingTransfer = 100m,
            currency = "EUR", notes = "Correction", isCompleted = false, version = 0,
        };
        var invalidSeason = await ownerClient.PutAsJsonAsync(seasonUrl, reopen with { leagueMatches = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSeason.StatusCode);
        var reopened = await ownerClient.PutAsJsonAsync(seasonUrl, reopen);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var stale = await ownerClient.PutAsJsonAsync(seasonUrl, reopen with { leaguePoints = 99 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var seasons = await ownerClient.GetFromJsonAsync<List<TestSeason>>($"/api/challenges/{challenge.Id}/seasons");
        var savedSeason = Assert.Single(seasons!);
        Assert.Equal("InProgress", savedSeason.Status);
        Assert.Equal(4, savedSeason.LeaguePoints);
        Assert.Equal(1, savedSeason.Version);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var audit = await verificationDatabase.AuditLogs.SingleAsync(item =>
            item.ChallengeId == challenge.Id && item.EntityType == "CareerSeason");
        Assert.Equal("season.reopened", audit.Action);
        Assert.NotNull(audit.OldData);
        Assert.NotNull(audit.NewData);
        Assert.Equal(SeasonStatus.Completed, (await verificationDatabase.CareerSeasons.SingleAsync(item =>
            item.CareerId == opponentCareer.Id && item.SeasonIndex == 0)).Status);

        var updates = await Task.WhenAll(
            ownerClient.PutAsJsonAsync(seasonUrl, reopen with { leaguePoints = 5, version = 1 }),
            ownerClient.PutAsJsonAsync(seasonUrl, reopen with { leaguePoints = 6, version = 1 }));
        Assert.Single(updates, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(updates, response => response.StatusCode == HttpStatusCode.Conflict);

        var newSeasonUrl = $"/api/challenges/{challenge.Id}/seasons/1";
        var newSeason = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = false, version = (long?)null,
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(newSeasonUrl, newSeason)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(newSeasonUrl, newSeason)).StatusCode);

        var claimUrl = $"/api/challenges/{challenge.Id}/achievement-claims/{claim.Id}";
        var correction = new { seasonIndex = 1, clubName = "Test Club", countryCode = "PT", notes = "Correction", version = 0L, managedCompetitionMatches = 2, managedDecidingMatch = true };
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.PutAsJsonAsync(claimUrl, correction)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(claimUrl, correction)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(claimUrl, correction)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.DeleteAsync($"{claimUrl}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.DeleteAsync($"{claimUrl}?version=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync($"{claimUrl}?version=1")).StatusCode);
        using var finalScope = factory.Services.CreateScope();
        var finalDatabase = finalScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        Assert.False(await finalDatabase.AchievementClaims.AnyAsync(item => item.Id == claim.Id));
        Assert.False(await finalDatabase.ScoreEvents.AnyAsync(item => item.SourceKey == claim.Id.ToString("N")));
        var claimAudit = await finalDatabase.AuditLogs.Where(item => item.EntityId == claim.Id.ToString())
            .Select(item => item.Action).ToListAsync();
        Assert.Contains("achievement.updated", claimAudit);
        Assert.Contains("achievement.deleted", claimAudit);
        var updatedNotifications = await opponentClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.Contains(updatedNotifications!, item => item.Type == "achievement.updated");
        Assert.Contains(updatedNotifications!, item => item.Type == "achievement.deleted");
    }

    [Fact]
    public async Task AdministratorCanReopenCompletedChallengeUntilCorrectionsAreFinished()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Correction test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active, TargetPoints = 10 };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var opponentCareer = new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant);
            database.Careers.AddRange(ownerCareer, opponentCareer);
            database.CareerStints.AddRange(Enumerable.Range(1, 3).Select(index => new CareerStint
            {
                CareerId = ownerCareer.Id, ClubName = $"Club {index}", CountryCode = "PT", CountryName = "Portugal",
                OfficialMatches = 20, EligibleSeasonIndex = 0,
            }));
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed },
                new CareerSeason { CareerId = opponentCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed });
            database.AchievementClaims.Add(new AchievementClaim
            {
                CareerId = ownerCareer.Id, ParticipantId = ownerParticipant.Id, AchievementCode = "G008",
                OccurrenceKey = "season:0", SeasonIndex = 0,
            });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
            Assert.Equal(ChallengeStatus.Completed, challenge.Status);
            Assert.NotNull(challenge.FinalScoreSnapshot);
            Assert.Equal(1, challenge.FinalRulesVersion);
        }

        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var reopenUrl = $"/api/challenges/{challenge.Id}/reopen";
        var initialNotifications = await ownerClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.Single(initialNotifications!, item => item.Type == "achievement.automatic");
        Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PostAsJsonAsync(reopenUrl, new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(reopenUrl, new { confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(reopenUrl, new { confirm = true })).StatusCode);
        var seasonUpdate = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "Corrected", isCompleted = true, version = 0L,
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/seasons/0", seasonUpdate)).StatusCode);
        var repeatNotifications = await ownerClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.Single(repeatNotifications!, item => item.Type == "achievement.automatic");
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var reopened = await database.Challenges.SingleAsync(item => item.Id == challenge.Id);
            Assert.Equal(ChallengeStatus.Active, reopened.Status);
            Assert.True(reopened.IsCorrectionMode);
            Assert.Null(reopened.WinningParticipantId);
            Assert.NotNull(reopened.FinalScoreSnapshot);
        }
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/finish-corrections", new { confirm = true })).StatusCode);
        using var finalScope = factory.Services.CreateScope();
        var finalDatabase = finalScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var finalChallenge = await finalDatabase.Challenges.SingleAsync(item => item.Id == challenge.Id);
        Assert.Equal(ChallengeStatus.Completed, finalChallenge.Status);
        Assert.False(finalChallenge.IsCorrectionMode);
        Assert.Equal(ownerParticipant.Id, finalChallenge.WinningParticipantId);
        var savedSnapshot = finalChallenge.FinalScoreSnapshot;
        Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/archive", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/archive", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/restore", new { confirm = true })).StatusCode);
        await finalDatabase.Entry(finalChallenge).ReloadAsync();
        Assert.Equal(ChallengeStatus.Completed, finalChallenge.Status);
        Assert.Equal(ownerParticipant.Id, finalChallenge.WinningParticipantId);
        Assert.Equal(savedSnapshot, finalChallenge.FinalScoreSnapshot);
        var actions = await finalDatabase.AuditLogs.Where(item => item.ChallengeId == challenge.Id)
            .Select(item => item.Action).ToListAsync();
        Assert.Contains("challenge.reopened", actions);
        Assert.Contains("challenge.correctionsFinished", actions);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(reopenUrl, new { confirm = true })).StatusCode);
        var claimId = await finalDatabase.AchievementClaims.Where(item => item.ParticipantId == ownerParticipant.Id)
            .Select(item => item.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync(
            $"/api/challenges/{challenge.Id}/achievement-claims/{claimId}?version=0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/finish-corrections", new { confirm = true })).StatusCode);
        await finalDatabase.Entry(finalChallenge).ReloadAsync();
        Assert.Equal(ChallengeStatus.Active, finalChallenge.Status);
        Assert.False(finalChallenge.IsCorrectionMode);
        Assert.Null(finalChallenge.WinningParticipantId);
    }

    [Fact]
    public async Task CompetitionParticipationIsValidatedAndPersistedOnCreateAndCorrection()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var createdResponse = await client.PostAsJsonAsync("/api/challenges", new { name = "Participation", startSeasonYear = 2026 });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<TestCreatedChallenge>();
        await ConfirmUnemployedStart(client, created!.Id);
        var url = $"/api/challenges/{created!.Id}/achievement-claims";
        var league = new
        {
            code = "G001", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 11, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, league)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, league with { managedCompetitionMatches = 12, inChargeAtCompetitionEnd = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, league with { managedCompetitionMatches = 37 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, league with { totalCompetitionMatches = 0 })).StatusCode);
        var leagueCreated = await client.PostAsJsonAsync(url, league with { managedCompetitionMatches = 12 });
        Assert.Equal(HttpStatusCode.Created, leagueCreated.StatusCode);
        var leagueId = (await leagueCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var correction = new
        {
            seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            managedCompetitionMatches = 12, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{leagueId}", correction)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{leagueId}", correction with { inChargeAtCompetitionEnd = true })).StatusCode);
        foreach (var code in new[] { "G008", "G009" })
        {
            var cup = new { code, seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", managedCompetitionMatches = 1, managedDecidingMatch = true };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, cup)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, cup with { managedCompetitionMatches = 2, managedDecidingMatch = false })).StatusCode);
            var cupCreated = await client.PostAsJsonAsync(url, cup with { managedCompetitionMatches = 2 });
            Assert.Equal(HttpStatusCode.Created, cupCreated.StatusCode);
            var cupId = (await cupCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
            var cupCorrection = new
            {
                seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
                managedCompetitionMatches = 1, managedDecidingMatch = true,
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{cupId}", cupCorrection)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{cupId}",
                cupCorrection with { managedCompetitionMatches = 2, managedDecidingMatch = false })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{cupId}",
                cupCorrection with { managedCompetitionMatches = 3 })).StatusCode);
        }
        foreach (var code in new[] { "G010", "G023" })
        {
            var superCup = new { code, seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", managedDecidingMatch = false };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, superCup)).StatusCode);
            var superCupCreated = await client.PostAsJsonAsync(url, superCup with { managedDecidingMatch = true });
            Assert.Equal(HttpStatusCode.Created, superCupCreated.StatusCode);
            var claimId = (await superCupCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
            var superCupCorrection = new
            {
                seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
                managedDecidingMatch = false,
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{claimId}", superCupCorrection)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{claimId}",
                superCupCorrection with { managedDecidingMatch = true })).StatusCode);
        }
        var survival = new
        {
            code = "G011", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 17, totalCompetitionMatches = 36, firstSeasonAfterPromotion = true, avoidedRelegation = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, survival)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, survival with { managedCompetitionMatches = 18, firstSeasonAfterPromotion = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, survival with { managedCompetitionMatches = 18, avoidedRelegation = false })).StatusCode);
        var survivalCreated = await client.PostAsJsonAsync(url, survival with { managedCompetitionMatches = 18 });
        Assert.Equal(HttpStatusCode.Created, survivalCreated.StatusCode);
        var survivalId = (await survivalCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var survivalCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            managedCompetitionMatches = 18, totalCompetitionMatches = 36,
            firstSeasonAfterPromotion = true, avoidedRelegation = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{survivalId}",
            survivalCorrection with { managedCompetitionMatches = 17 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{survivalId}",
            survivalCorrection with { firstSeasonAfterPromotion = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{survivalId}",
            survivalCorrection with { avoidedRelegation = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{survivalId}", survivalCorrection)).StatusCode);
        var runnerUp = new { code = "G012", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", competitionLeaguePosition = 1 };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, runnerUp)).StatusCode);
        var runnerUpCreated = await client.PostAsJsonAsync(url, runnerUp with { competitionLeaguePosition = 2 });
        Assert.Equal(HttpStatusCode.Created, runnerUpCreated.StatusCode);
        var runnerUpId = (await runnerUpCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var runnerUpCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            competitionLeaguePosition = 1,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{runnerUpId}", runnerUpCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{runnerUpId}",
            runnerUpCorrection with { competitionLeaguePosition = 2 })).StatusCode);
        var topFour = new { code = "G016", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", competitionLeaguePosition = 1, firstSeasonAfterPromotion = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, topFour)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, topFour with { competitionLeaguePosition = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, topFour with { competitionLeaguePosition = 4, firstSeasonAfterPromotion = false })).StatusCode);
        var topFourCreated = await client.PostAsJsonAsync(url, topFour with { competitionLeaguePosition = 4 });
        Assert.Equal(HttpStatusCode.Created, topFourCreated.StatusCode);
        var topFourId = (await topFourCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var topFourCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            competitionLeaguePosition = 5, firstSeasonAfterPromotion = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{topFourId}", topFourCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{topFourId}",
            topFourCorrection with { competitionLeaguePosition = 4, firstSeasonAfterPromotion = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{topFourId}",
            topFourCorrection with { competitionLeaguePosition = 4 })).StatusCode);
        var promotedChampion = new
        {
            code = "G019", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 12, totalCompetitionMatches = 37, inChargeAtCompetitionEnd = true,
            competitionLeaguePosition = 1, firstSeasonAfterPromotion = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, promotedChampion)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, promotedChampion with { managedCompetitionMatches = 13, competitionLeaguePosition = 2 })).StatusCode);
        var championCreated = await client.PostAsJsonAsync(url, promotedChampion with { managedCompetitionMatches = 13 });
        Assert.Equal(HttpStatusCode.Created, championCreated.StatusCode);
        var championId = (await championCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var championCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            managedCompetitionMatches = 13, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = true,
            competitionLeaguePosition = 1, firstSeasonAfterPromotion = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{championId}",
            championCorrection with { competitionLeaguePosition = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{championId}",
            championCorrection with { firstSeasonAfterPromotion = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{championId}", championCorrection)).StatusCode);
        var domesticDouble = new
        {
            code = "G013", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 12, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = true,
            wonFirstDivisionTitle = true, wonMajorNationalCup = true, domesticCupMatchesManaged = 2, managedDecidingMatch = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { countryCode = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { wonMajorNationalCup = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { domesticCupMatchesManaged = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { managedDecidingMatch = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { clubName = "Different Club" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { countryCode = "ES" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, domesticDouble with { wonFirstDivisionTitle = false })).StatusCode);
        var domesticDoubleCreated = await client.PostAsJsonAsync(url, domesticDouble);
        Assert.Equal(HttpStatusCode.Created, domesticDoubleCreated.StatusCode);
        var domesticDoubleId = (await domesticDoubleCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var domesticDoubleScore = (await client.GetFromJsonAsync<TestScore>($"/api/challenges/{created.Id}/scores"))!;
        Assert.Equal(7, Assert.Single(domesticDoubleScore.Breakdown, item => item.Label.StartsWith("G013")).Points);
        var domesticDoubleCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            managedCompetitionMatches = 12, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = true,
            wonFirstDivisionTitle = true, wonMajorNationalCup = true, domesticCupMatchesManaged = 2,
            managedDecidingMatch = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { countryCode = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { wonMajorNationalCup = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { domesticCupMatchesManaged = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { managedDecidingMatch = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { clubName = "Different Club" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { countryCode = "ES" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}",
            domesticDoubleCorrection with { wonMajorNationalCup = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{domesticDoubleId}", domesticDoubleCorrection)).StatusCode);
        var unbeaten = new
        {
            code = "G014", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 12, totalCompetitionMatches = 36, inChargeAtCompetitionEnd = true, wonLeagueTitleUnbeaten = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, unbeaten)).StatusCode);
        var unbeatenCreated = await client.PostAsJsonAsync(url, unbeaten with { wonLeagueTitleUnbeaten = true });
        Assert.Equal(HttpStatusCode.Created, unbeatenCreated.StatusCode);
        var unbeatenId = (await unbeatenCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var unbeatenCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction",
            version = 0L, managedCompetitionMatches = 12, totalCompetitionMatches = 36,
            inChargeAtCompetitionEnd = true, wonLeagueTitleUnbeaten = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{unbeatenId}", unbeatenCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{unbeatenId}",
            unbeatenCorrection with { wonLeagueTitleUnbeaten = true })).StatusCode);
        var lowerCup = new
        {
            code = "G015", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            managedCompetitionMatches = 2, managedDecidingMatch = true, cupWonInLowerDivision = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, lowerCup)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, lowerCup with { cupWonInLowerDivision = true, managedDecidingMatch = false })).StatusCode);
        var lowerCupCreated = await client.PostAsJsonAsync(url, lowerCup with { cupWonInLowerDivision = true });
        Assert.Equal(HttpStatusCode.Created, lowerCupCreated.StatusCode);
        var lowerCupId = (await lowerCupCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var lowerCupCorrection = new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            managedCompetitionMatches = 2, managedDecidingMatch = true, cupWonInLowerDivision = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{lowerCupId}", lowerCupCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{lowerCupId}",
            lowerCupCorrection with { cupWonInLowerDivision = true, managedDecidingMatch = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{lowerCupId}",
            lowerCupCorrection with { cupWonInLowerDivision = true })).StatusCode);
        foreach (var (code, destination) in new[] { ("G005", 1), ("G006", 2), ("G007", 3) })
        {
            var promotion = new
            {
                code, seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
                promotionDestinationLevel = destination, promotionWithoutTitle = false,
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, promotion)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, promotion with { promotionDestinationLevel = 0, promotionWithoutTitle = true })).StatusCode);
            var promotionCreated = await client.PostAsJsonAsync(url, promotion with { promotionWithoutTitle = true });
            Assert.Equal(HttpStatusCode.Created, promotionCreated.StatusCode);
            var promotionId = (await promotionCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
            var promotionCorrection = new
            {
                seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
                promotionDestinationLevel = destination, promotionWithoutTitle = true,
            };
            var invalidDestination = code == "G007" ? 2 : 3;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{promotionId}",
                promotionCorrection with { promotionDestinationLevel = invalidDestination })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{promotionId}",
                promotionCorrection with { promotionWithoutTitle = false })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{promotionId}", promotionCorrection)).StatusCode);
        }
        foreach (var (code, season) in new[] { ("G017", 1), ("G018", 2) })
        {
            var streak = new { code, seasonIndex = season, clubName = "Club", countryCode = "PT", notes = "", promotionStreakStartSeason = 1 };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, streak)).StatusCode);
            var streakCreated = await client.PostAsJsonAsync(url, streak with { promotionStreakStartSeason = 0 });
            Assert.Equal(HttpStatusCode.Created, streakCreated.StatusCode);
            var streakId = (await streakCreated.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
            var streakCorrection = new
            {
                seasonIndex = season, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
                promotionStreakStartSeason = 1,
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{streakId}", streakCorrection)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{streakId}",
                streakCorrection with { promotionStreakStartSeason = 0 })).StatusCode);
        }
        var firstContinental = new
        {
            code = "G025", seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "",
            reachedContinentalMainPhase = false, priorContinentalMainPhase = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, firstContinental)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, firstContinental with { reachedContinentalMainPhase = true, priorContinentalMainPhase = true })).StatusCode);
        var firstResponse = await client.PostAsJsonAsync(url, firstContinental with { reachedContinentalMainPhase = true });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstId = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var repeatContinental = new
        {
            code = "G026", seasonIndex = 0, clubName = " club ", countryCode = "PT", notes = "",
            reachedContinentalMainPhase = true, priorContinentalMainPhase = true,
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, repeatContinental)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, repeatContinental with { seasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, repeatContinental with { seasonIndex = 2, priorContinentalMainPhase = false })).StatusCode);
        var repeatResponse = await client.PostAsJsonAsync(url, repeatContinental with { seasonIndex = 2 });
        Assert.Equal(HttpStatusCode.Created, repeatResponse.StatusCode);
        var repeatId = (await repeatResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var repeatCorrection = new
        {
            seasonIndex = 2, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            reachedContinentalMainPhase = true, priorContinentalMainPhase = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{repeatId}",
            repeatCorrection with { reachedContinentalMainPhase = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{repeatId}",
            repeatCorrection with { priorContinentalMainPhase = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{url}/{repeatId}",
            repeatCorrection with { seasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{repeatId}", repeatCorrection)).StatusCode);
        var secondChallengeResponse = await client.PostAsJsonAsync("/api/challenges", new { name = "Other participation", startSeasonYear = 2026 });
        Assert.Equal(HttpStatusCode.Created, secondChallengeResponse.StatusCode);
        var secondChallenge = await secondChallengeResponse.Content.ReadFromJsonAsync<TestCreatedChallenge>();
        await ConfirmUnemployedStart(client, secondChallenge!.Id);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            $"/api/challenges/{secondChallenge!.Id}/achievement-claims", repeatContinental)).StatusCode);
        var otherUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Other manager" };
        var otherParticipant = new ChallengeParticipant { ChallengeId = created.Id, UserId = otherUser.Id };
        using (var otherScope = factory.Services.CreateScope())
        {
            var otherDatabase = otherScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            otherDatabase.Users.Add(otherUser);
            otherDatabase.ChallengeParticipants.Add(otherParticipant);
            otherDatabase.Careers.Add(new Career { ParticipantId = otherParticipant.Id, TrainerName = "Other manager" });
            await otherDatabase.SaveChangesAsync();
        }
        using (var otherClient = await CreateAuthenticatedClient(otherUser.Id))
            Assert.Equal(HttpStatusCode.Created, (await otherClient.PostAsJsonAsync(url, repeatContinental)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{url}/{firstId}", new
        {
            seasonIndex = 2, clubName = "Club", countryCode = "PT", notes = "", version = 0L,
            reachedContinentalMainPhase = true, priorContinentalMainPhase = false,
        })).StatusCode);
        var topTier = new
        {
            code = "G027", seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "",
            reachedContinentalMainPhase = true, firstTopTierMainPhase = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, topTier)).StatusCode);
        var topTierResponse = await client.PostAsJsonAsync(url, topTier with { firstTopTierMainPhase = true });
        Assert.Equal(HttpStatusCode.Created, topTierResponse.StatusCode);
        var topTierId = (await topTierResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var topTierCorrection = new
        {
            seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "Correction", version = 0L,
            reachedContinentalMainPhase = true, firstTopTierMainPhase = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{topTierId}",
            topTierCorrection with { reachedContinentalMainPhase = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{topTierId}",
            topTierCorrection with { firstTopTierMainPhase = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{topTierId}", topTierCorrection)).StatusCode);
        var continentalTitles = new
        {
            code = "G032", seasonIndex = 2, clubName = "Club B", countryCode = "PT", notes = "",
            firstContinentalTitleClub = "Club A", firstContinentalTitleCountry = "PT",
            firstContinentalTitleSeason = 1, wonContinentalTitlesWithBothClubs = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, continentalTitles with { firstContinentalTitleClub = " club b " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, continentalTitles with { firstContinentalTitleSeason = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, continentalTitles with { wonContinentalTitlesWithBothClubs = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, continentalTitles with { countryCode = "" })).StatusCode);
        var titleResponse = await client.PostAsJsonAsync(url, continentalTitles);
        Assert.Equal(HttpStatusCode.Created, titleResponse.StatusCode);
        var titleId = (await titleResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var titleCorrection = new
        {
            seasonIndex = 2, clubName = "Club B", countryCode = "PT", notes = "Correction", version = 0L,
            firstContinentalTitleClub = "Club B", firstContinentalTitleCountry = "PT",
            firstContinentalTitleSeason = 1, wonContinentalTitlesWithBothClubs = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{titleId}", titleCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{titleId}",
            titleCorrection with { firstContinentalTitleClub = "Club A", firstContinentalTitleSeason = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{titleId}",
            titleCorrection with { firstContinentalTitleClub = "Club A", wonContinentalTitlesWithBothClubs = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{titleId}", titleCorrection with { firstContinentalTitleCountry = "ES" })).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var saved = await verify.AchievementClaims.SingleAsync(item => item.Id == leagueId);
        Assert.Equal(1, saved.SeasonIndex);
        Assert.Equal(12, saved.ManagedCompetitionMatches);
        Assert.Equal(36, saved.TotalCompetitionMatches);
        Assert.True(saved.InChargeAtCompetitionEnd);
        Assert.Null(saved.ManagedDecidingMatch);
        var savedTitles = await verify.AchievementClaims.SingleAsync(item => item.Id == titleId);
        Assert.Equal("ES", savedTitles.FirstContinentalTitleCountry);
        Assert.Equal(1, savedTitles.FirstContinentalTitleSeason);
        Assert.True(savedTitles.WonContinentalTitlesWithBothClubs);
        var savedRepeat = await verify.AchievementClaims.SingleAsync(item => item.Id == repeatId);
        Assert.Equal(2, savedRepeat.SeasonIndex);
        Assert.True(savedRepeat.ReachedContinentalMainPhase);
        Assert.True(savedRepeat.PriorContinentalMainPhase);
        var savedTopTier = await verify.AchievementClaims.SingleAsync(item => item.Id == topTierId);
        Assert.True(savedTopTier.ReachedContinentalMainPhase);
        Assert.True(savedTopTier.FirstTopTierMainPhase);
        var creationAudit = await verify.AuditLogs.SingleAsync(item => item.EntityId == firstId.ToString() && item.Action == "Created");
        Assert.Contains("\"ReachedContinentalMainPhase\":true", creationAudit.NewData);
        Assert.Contains("\"PriorContinentalMainPhase\":false", creationAudit.NewData);
        var score = await client.GetFromJsonAsync<TestScore>($"/api/challenges/{created.Id}/scores");
        Assert.DoesNotContain(score!.Breakdown, item => item.Explanation?.Contains("older record") == true);

        var oldFirst = await verify.AchievementClaims.SingleAsync(item => item.Id == firstId);
        oldFirst.ReachedContinentalMainPhase = null;
        oldFirst.PriorContinentalMainPhase = null;
        await verify.SaveChangesAsync();
        await verifyScope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(created.Id, CancellationToken.None);
        score = await client.GetFromJsonAsync<TestScore>($"/api/challenges/{created.Id}/scores");
        var legacy = Assert.Single(score!.Breakdown, item => item.SourceType == "Achievement" && item.Label.StartsWith("G025"));
        Assert.Equal(6, legacy.Points);
        Assert.Contains("older record", legacy.Explanation);
        var legacyCorrection = new { seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "", version = 0L };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{firstId}", legacyCorrection)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{firstId}", new
        {
            seasonIndex = 1, clubName = "Club", countryCode = "PT", notes = "", version = 0L,
            reachedContinentalMainPhase = true, priorContinentalMainPhase = false,
        })).StatusCode);
    }

    [Fact]
    public async Task ClubAchievementFrequencyUsesCountryAndProtectsUnknownLegacyIdentity()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var createdResponse = await client.PostAsJsonAsync("/api/challenges", new { name = "Club identity", startSeasonYear = 2026 });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var challenge = await createdResponse.Content.ReadFromJsonAsync<TestCreatedChallenge>();
        await ConfirmUnemployedStart(client, challenge!.Id);
        var url = $"/api/challenges/{challenge!.Id}/achievement-claims";
        var first = new
        {
            code = "G025", seasonIndex = 1, clubName = "United", countryCode = "PT", notes = "",
            reachedContinentalMainPhase = true, priorContinentalMainPhase = false,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, first)).StatusCode);
        var spanish = await client.PostAsJsonAsync(url, first with { clubName = " united ", countryCode = "ES" });
        Assert.Equal(HttpStatusCode.Created, spanish.StatusCode);
        var spanishId = (await spanish.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, first with { seasonIndex = 2, countryCode = "pt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, first with { seasonIndex = 2, countryCode = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{url}/{spanishId}", new
        {
            seasonIndex = 1, clubName = "United", countryCode = "PT", notes = "", version = 0L,
            reachedContinentalMainPhase = true, priorContinentalMainPhase = false,
        })).StatusCode);
        var repeat = new
        {
            code = "G026", seasonIndex = 0, clubName = "United", countryCode = "FR", notes = "",
            reachedContinentalMainPhase = true, priorContinentalMainPhase = true,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, repeat)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, repeat with { countryCode = "ES" })).StatusCode);
        var survival = new
        {
            code = "G011", seasonIndex = 0, clubName = "United", countryCode = "PT", notes = "",
            managedCompetitionMatches = 18, totalCompetitionMatches = 36, firstSeasonAfterPromotion = true, avoidedRelegation = true,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, survival)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, survival with { countryCode = "ES" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, survival with { countryCode = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, survival with { seasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, first with { clubName = "Legacy", countryCode = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, first with { clubName = "legacy", countryCode = "PT" })).StatusCode);
    }

    [Fact]
    public async Task CareerDivisionAchievementsRequireAttestedDivisionEvidence()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Division evidence", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/achievement-claims";
        var firstDivision = new { code = "G038", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", firstDivisionExperience = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, firstDivision)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { code = "G038", seasonIndex = 0 })).StatusCode);
        var firstResponse = await client.PostAsJsonAsync(url, firstDivision with { firstDivisionExperience = true });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstId = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{firstId}", new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", version = 0L, firstDivisionExperience = false,
        })).StatusCode);
        var move = new { code = "G039", seasonIndex = 1, clubName = "New Club", countryCode = "PT", notes = "",
            higherDivisionClubMove = true, previousBestDivisionLevel = 3, newDivisionLevel = 2 };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, move with { higherDivisionClubMove = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, move with { previousBestDivisionLevel = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, move with { newDivisionLevel = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, move with { newDivisionLevel = 0 })).StatusCode);
        var moveResponse = await client.PostAsJsonAsync(url, move);
        Assert.Equal(HttpStatusCode.Created, moveResponse.StatusCode);
        var moveId = (await moveResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{moveId}", new
        {
            seasonIndex = 1, clubName = "New Club", countryCode = "PT", notes = "", version = 0L,
            higherDivisionClubMove = true, previousBestDivisionLevel = 2, newDivisionLevel = 2,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{moveId}", new
        {
            seasonIndex = 1, clubName = "New Club", countryCode = "PT", notes = "", version = 0L,
            higherDivisionClubMove = true, previousBestDivisionLevel = 4, newDivisionLevel = 1,
        })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var first = await database.AchievementClaims.SingleAsync(item => item.Id == firstId);
            var updated = await database.AchievementClaims.SingleAsync(item => item.Id == moveId);
            Assert.True(first.FirstDivisionExperience);
            Assert.True(updated.HigherDivisionClubMove);
            Assert.Equal(4, updated.PreviousBestDivisionLevel);
            Assert.Equal(1, updated.NewDivisionLevel);
            first.FirstDivisionExperience = null;
            updated.HigherDivisionClubMove = null;
            updated.PreviousBestDivisionLevel = null;
            updated.NewDivisionLevel = null;
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        var score = (await client.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        foreach (var (code, points) in new[] { ("G038", 6), ("G039", 4) })
        {
            var legacy = Assert.Single(score.Breakdown, item => item.Label.StartsWith(code));
            Assert.Equal(points, legacy.Points);
            Assert.Contains("older record", legacy.Explanation);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{firstId}", new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", version = 0L,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{firstId}", new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", version = 0L,
            firstDivisionExperience = true,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{moveId}", new
        {
            seasonIndex = 1, clubName = "New Club", countryCode = "PT", notes = "", version = 1L,
        })).StatusCode);
    }

    [Fact]
    public async Task FirstDivisionEvidenceTracksEarlierStintCorrectionsWithoutChangingLegacyClaims()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "First division chronology", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var claimsUrl = $"{root}/achievement-claims";
        var claim = new { code = "G038", seasonIndex = 1, firstDivisionExperience = true };
        var created = await client.PostAsJsonAsync(claimsUrl, claim);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var claimId = (await created.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var move = new { code = "G039", seasonIndex = 1, higherDivisionClubMove = true,
            previousBestDivisionLevel = 2, newDivisionLevel = 1 };
        var createdMove = await client.PostAsJsonAsync(claimsUrl, move);
        Assert.Equal(HttpStatusCode.Created, createdMove.StatusCode);
        var moveId = (await createdMove.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;

        var stint = new { clubName = "Earlier club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 1, officialMatches = 0, eligibleSeasonIndex = (int?)null };
        var createdStint = await client.PostAsJsonAsync($"{root}/career/stints", stint);
        Assert.Equal(HttpStatusCode.Created, createdStint.StatusCode);
        var stintId = (await createdStint.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        var contradicted = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G038"));
        Assert.Equal(0, contradicted.Points);
        Assert.Contains("earlier season", contradicted.Explanation);
        var contradictedMove = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G039"));
        Assert.Equal(0, contradictedMove.Points);
        Assert.Contains("previous best", contradictedMove.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{claimsUrl}/{claimId}",
            new { seasonIndex = 2, version = 0L, firstDivisionExperience = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{claimsUrl}/{moveId}",
            new { seasonIndex = 2, version = 0L, move.higherDivisionClubMove, move.previousBestDivisionLevel, move.newDivisionLevel })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, move with { seasonIndex = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{root}/career/stints/{stintId}",
            new { stint.clubName, stint.countryCode, stint.countryName, stint.startSeasonIndex, stint.endSeasonIndex,
                initialDivisionLevel = 2, stint.officialMatches, stint.eligibleSeasonIndex, version = 0L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(6, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G038")).Points);
        Assert.Equal(4, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G039")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{root}/career/stints/{stintId}",
            new { stint.clubName, stint.countryCode, stint.countryName, stint.startSeasonIndex, stint.endSeasonIndex,
                initialDivisionLevel = 1, stint.officialMatches, stint.eligibleSeasonIndex, version = 1L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, claim with { seasonIndex = 2 })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var legacy = await database.AchievementClaims.SingleAsync(item => item.Id == claimId);
            legacy.FirstDivisionExperience = null;
            var legacyMove = await database.AchievementClaims.SingleAsync(item => item.Id == moveId);
            legacyMove.HigherDivisionClubMove = null;
            legacyMove.PreviousBestDivisionLevel = null;
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        var legacyScore = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G038"));
        Assert.Equal(6, legacyScore.Points);
        Assert.Contains("older record", legacyScore.Explanation);
        var legacyMoveScore = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G039"));
        Assert.Equal(4, legacyMoveScore.Points);
        Assert.Contains("older record", legacyMoveScore.Explanation);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{claimId}",
            new { seasonIndex = 0, version = 0L, firstDivisionExperience = true })).StatusCode);
    }

    [Fact]
    public async Task DivisionClaimsRecalculateFromDetailedSeasons()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Detailed division evidence", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var firstClaim = new { code = "G038", seasonIndex = 2, firstDivisionExperience = true };
        var moveClaim = new { code = "G039", seasonIndex = 2, higherDivisionClubMove = true,
            previousBestDivisionLevel = 3, newDivisionLevel = 2 };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{root}/achievement-claims", firstClaim)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{root}/achievement-claims", moveClaim)).StatusCode);
        var createdStint = await client.PostAsJsonAsync($"{root}/career/stints", new
        {
            clubName = "Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 3, officialMatches = 20, eligibleSeasonIndex = (int?)null,
        });
        Assert.Equal(HttpStatusCode.Created, createdStint.StatusCode);
        var stintId = (await createdStint.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var detail = new
        {
            careerStintId = stintId, divisionLevel = 1, officialMatches = 20, leagueMatches = 20,
            leagueWins = 10, leagueDraws = 5, leagueLosses = 5, leaguePoints = 35,
            goalsFor = 30, goalsAgainst = 20, leaguePosition = (int?)4,
        };
        var season = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = (long?)null, confirmTransferCurrencyRelabel = false,
            usesClubBreakdown = true, clubSeasons = new[] { detail }, clearClubBreakdown = false,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{root}/seasons/0", season)).StatusCode);
        var score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G038")).Points);
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G039")).Points);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/achievement-claims", firstClaim with { seasonIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/achievement-claims", moveClaim with { seasonIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{root}/seasons/0",
            season with { version = 1L, clubSeasons = new[] { detail with { divisionLevel = 3 } } })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(6, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G038")).Points);
        Assert.Equal(4, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G039")).Points);
    }

    [Fact]
    public async Task ThirdToFirstClaimRecalculatesAfterInterveningStintCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Club progression", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var claim = new { code = "G050", clubName = "Home", countryCode = "PT", seasonIndex = 4,
            thirdDivisionStartSeason = 0, stayedWithClubFromThirdToFirst = true };
        var created = await client.PostAsJsonAsync($"{root}/achievement-claims", claim);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var claimId = (await created.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var stint = new { clubName = "Away", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 1,
            endSeasonIndex = (int?)2, initialDivisionLevel = 2, officialMatches = 1, eligibleSeasonIndex = (int?)null };
        var createdStint = await client.PostAsJsonAsync($"{root}/career/stints", stint);
        Assert.Equal(HttpStatusCode.Created, createdStint.StatusCode);
        var stintId = (await createdStint.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        var item = Assert.Single(score.Breakdown, row => row.Label.StartsWith("G050"));
        Assert.Equal(0, item.Points);
        Assert.Contains("another club", item.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{root}/achievement-claims/{claimId}",
            new { claim.clubName, claim.countryCode, claim.seasonIndex, claim.thirdDivisionStartSeason,
                claim.stayedWithClubFromThirdToFirst, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/achievement-claims", claim)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{root}/career/stints/{stintId}", new
        {
            stint.clubName, stint.countryCode, stint.countryName, stint.startSeasonIndex, stint.endSeasonIndex,
            stint.initialDivisionLevel, officialMatches = 0, stint.eligibleSeasonIndex, version = 0L,
        })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(12, Assert.Single(score.Breakdown, row => row.Label.StartsWith("G050")).Points);
    }

    [Fact]
    public async Task CompetitionCatalogScopesAndDistinguishesContinentalResults()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Competitions", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var catalogUrl = $"/api/challenges/{challenge.Id}/competitions";
        var firstResponse = await client.PostAsJsonAsync(catalogUrl, new { name = "Top A", tier = "Top" });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<TestCompetition>())!;
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(opponent);
            database.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id });
            await database.SaveChangesAsync();
        }
        using (var opponentClient = await CreateAuthenticatedClient(opponent.Id))
        {
            Assert.Equal(HttpStatusCode.OK, (await opponentClient.GetAsync(catalogUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PostAsJsonAsync(catalogUrl, new { name = "Forbidden", tier = "Top" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PutAsJsonAsync($"{catalogUrl}/{first.Id}", new { name = "Forbidden", tier = "Top", version = 0L })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.DeleteAsync($"{catalogUrl}/{first.Id}?version=0")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(catalogUrl, new { name = " top a ", tier = "Top" })).StatusCode);
        var second = (await (await client.PostAsJsonAsync(catalogUrl, new { name = "Top B", tier = "Top" })).Content.ReadFromJsonAsync<TestCompetition>())!;
        var third = (await (await client.PostAsJsonAsync(catalogUrl, new { name = "Second", tier = "Second" })).Content.ReadFromJsonAsync<TestCompetition>())!;
        response = await client.PostAsJsonAsync("/api/challenges", new { name = "Other competitions", startSeasonYear = 2026 });
        var otherChallenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, otherChallenge.Id);
        var other = (await (await client.PostAsJsonAsync($"/api/challenges/{otherChallenge.Id}/competitions",
            new { name = "Other", tier = "Top" })).Content.ReadFromJsonAsync<TestCompetition>())!;
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var winner = new { code = "G020", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", competitionId = third.Id,
            competitionResultKind = "ClubContinentalTop", competitionResultStage = "Winner" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, winner)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, winner with { competitionId = other.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, winner with { competitionId = first.Id })).StatusCode);
        var quarterResponse = await client.PostAsJsonAsync(claimsUrl, winner with { code = "G028", competitionId = second.Id,
            competitionResultStage = "QuarterFinalElimination" });
        Assert.Equal(HttpStatusCode.Created, quarterResponse.StatusCode);
        var quarter = (await quarterResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(4, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G028")).Points);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{catalogUrl}/{first.Id}?version=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{catalogUrl}/{first.Id}", new { name = "Top A", tier = "Other", version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{catalogUrl}/{first.Id}", new { name = "Renamed", tier = "Top", version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{catalogUrl}/{first.Id}", new { name = "Stale", tier = "Top", version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{quarter.Id}", new
        {
            seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "", version = 0L, competitionId = first.Id,
            competitionResultKind = "ClubContinentalTop", competitionResultStage = "QuarterFinalElimination",
        })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G028")).Points);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{catalogUrl}/{second.Id}?version=0")).StatusCode);
    }

    [Fact]
    public async Task InternationalResultsRequireMatchingCompetitionAndStage()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "International results", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/achievement-claims";
        var results = new[]
        {
            ("G020", "ClubContinentalTop", "Winner"),
            ("G021", "ClubContinentalSecond", "Winner"),
            ("G022", "ClubContinentalThird", "Winner"),
            ("G024", "ClubWorld", "Winner"),
            ("G028", "ClubContinentalTop", "QuarterFinalElimination"),
            ("G029", "ClubContinentalTop", "SemiFinalElimination"),
            ("G030", "ClubContinentalTop", "RunnerUp"),
            ("G031", "ClubContinentalSecond", "RunnerUp"),
            ("G034", "NationalWorld", "Winner"),
            ("G035", "NationalContinental", "Winner"),
            ("G036", "NationalWorld", "SemiFinalElimination"),
        };
        Guid winnerId = default;
        var claimIds = new List<Guid>();
        foreach (var (code, kind, stage) in results)
        {
            var claim = new { code, seasonIndex = 0, competitionResultKind = kind, competitionResultStage = stage };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, claim with { competitionResultKind = "Other" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, claim with { competitionResultStage = "Other" })).StatusCode);
            var saved = await client.PostAsJsonAsync(url, claim);
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
            var claimId = (await saved.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
            claimIds.Add(claimId);
            if (code == "G020") winnerId = claimId;
            var correction = new { seasonIndex = 0, notes = "Correction", version = 0L, competitionResultKind = kind, competitionResultStage = stage };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{claimId}",
                correction with { competitionResultKind = "Other" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{claimId}",
                correction with { competitionResultStage = "Other" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{claimId}", correction)).StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var savedResults = await database.AchievementClaims.Where(item => claimIds.Contains(item.Id)).ToArrayAsync();
            foreach (var (code, kind, stage) in results)
            {
                var savedResult = Assert.Single(savedResults, item => item.AchievementCode == code);
                Assert.Equal(kind, savedResult.CompetitionResultKind);
                Assert.Equal(stage, savedResult.CompetitionResultStage);
                Assert.Equal("Correction", savedResult.Notes);
            }
            var legacy = await database.AchievementClaims.SingleAsync(item => item.Id == winnerId);
            legacy.CompetitionResultKind = null;
            legacy.CompetitionResultStage = null;
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        var score = await client.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores");
        var legacyWinner = Assert.Single(score!.Breakdown, item => item.Label.StartsWith("G020"));
        Assert.Equal(30, legacyWinner.Points);
        Assert.Contains("older record", legacyWinner.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{winnerId}",
            new { seasonIndex = 0, version = 1L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{winnerId}",
            new { seasonIndex = 0, version = 1L, competitionResultKind = "ClubContinentalTop", competitionResultStage = "Winner" })).StatusCode);
    }

    [Fact]
    public async Task TenMajorTrophiesAreDerivedAndRecalculatedFromClaims()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Trophy milestone", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        Guid lastCup = default;
        for (var season = 0; season < 5; season++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, new
            {
                code = "G001", seasonIndex = season, managedCompetitionMatches = 10,
                totalCompetitionMatches = 30, inChargeAtCompetitionEnd = true,
            })).StatusCode);
            var cup = await client.PostAsJsonAsync(claimsUrl, new
            {
                code = "G008", seasonIndex = season, managedCompetitionMatches = 2, managedDecidingMatch = true,
            });
            Assert.Equal(HttpStatusCode.Created, cup.StatusCode);
            lastCup = (await cup.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        }
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        Assert.Equal(8, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown,
            item => item.Label.StartsWith("G048")).Points);
        var manual = new { code = "G048", seasonIndex = 4, wonTenMajorTrophies = true };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(claimsUrl, manual)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{claimsUrl}/{lastCup}?version=0")).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G048"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, manual with { wonTenMajorTrophies = false })).StatusCode);
        var manualResponse = await client.PostAsJsonAsync(claimsUrl, manual);
        Assert.Equal(HttpStatusCode.Created, manualResponse.StatusCode);
        var manualId = (await manualResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var manualCorrection = new { seasonIndex = 4, version = 0L, wonTenMajorTrophies = true, notes = "Correction" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{claimsUrl}/{manualId}",
            manualCorrection with { wonTenMajorTrophies = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{manualId}", manualCorrection)).StatusCode);
        Assert.Equal(8, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown,
            item => item.Label.StartsWith("G048")).Points);
    }

    [Fact]
    public async Task CareerAchievementsRequireTheirSpecificAttestations()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Career evidence", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/achievement-claims";
        var trophy = new { code = "G040", seasonIndex = 2, clubName = "Club", firstMajorClubTrophy = true, trophyNotSuperCup = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, trophy)).StatusCode);
        var trophyResponse = await client.PostAsJsonAsync(url, trophy with { trophyNotSuperCup = true });
        Assert.Equal(HttpStatusCode.Created, trophyResponse.StatusCode);
        var trophyId = (await trophyResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{trophyId}",
            new { seasonIndex = 2, clubName = "Club", version = 0L, firstMajorClubTrophy = false, trophyNotSuperCup = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{trophyId}",
            new { seasonIndex = 2, clubName = "Club", version = 0L, firstMajorClubTrophy = true, trophyNotSuperCup = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{trophyId}",
            new { seasonIndex = 2, clubName = "Club", version = 0L, firstMajorClubTrophy = true, trophyNotSuperCup = true })).StatusCode);
        var progression = new { code = "G050", seasonIndex = 2, clubName = "Club", thirdDivisionStartSeason = 3, stayedWithClubFromThirdToFirst = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, progression)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, progression with { thirdDivisionStartSeason = 0, stayedWithClubFromThirdToFirst = false })).StatusCode);
        var progressionResponse = await client.PostAsJsonAsync(url, progression with { thirdDivisionStartSeason = 0 });
        Assert.Equal(HttpStatusCode.Created, progressionResponse.StatusCode);
        var progressionId = (await progressionResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var progressionCorrection = new
        {
            seasonIndex = 2, clubName = "Club", version = 0L, thirdDivisionStartSeason = 0,
            stayedWithClubFromThirdToFirst = true, notes = "Correction",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{progressionId}",
            progressionCorrection with { thirdDivisionStartSeason = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{progressionId}",
            progressionCorrection with { stayedWithClubFromThirdToFirst = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{progressionId}", progressionCorrection)).StatusCode);
        var award = new { code = "G051", seasonIndex = 2, officialManagerOfYear = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, award)).StatusCode);
        var awardResponse = await client.PostAsJsonAsync(url, award with { officialManagerOfYear = true });
        Assert.Equal(HttpStatusCode.Created, awardResponse.StatusCode);
        var awardId = (await awardResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var awardCorrection = new { seasonIndex = 2, version = 0L, officialManagerOfYear = true, notes = "Correction" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{awardId}",
            awardCorrection with { officialManagerOfYear = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{awardId}", awardCorrection)).StatusCode);
        var achievements = await client.GetFromJsonAsync<List<TestAchievement>>($"/api/challenges/{challenge.Id}/achievements");
        Assert.NotNull(achievements);
        Assert.Contains(achievements, item => item.Code == "G040");
        var score = (await client.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G040")).Points);
        Assert.Equal(12, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G050")).Points);
        Assert.Equal(4, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G051")).Points);
        Guid legacyId;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var participant = await database.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challenge.Id && item.UserId == owner.Id);
            var career = await database.Careers.SingleAsync(item => item.ParticipantId == participant.Id);
            var legacy = new AchievementClaim
            {
                ParticipantId = participant.Id, CareerId = career.Id, AchievementCode = "G051",
                SeasonIndex = 3, OccurrenceKey = "season:3",
            };
            legacyId = legacy.Id;
            database.AchievementClaims.Add(legacy);
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        score = (await client.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        Assert.Contains(score.Breakdown, item => item.Label.StartsWith("G051") && item.Points == 4
            && item.Explanation != null && item.Explanation.Contains("older record"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{url}/{legacyId}",
            new { seasonIndex = 3, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{legacyId}",
            new { seasonIndex = 3, version = 0L, officialManagerOfYear = true })).StatusCode);
    }

    [Fact]
    public async Task ThreeContinentsMilestoneUsesCatalogAndRecalculatesAfterCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Continents", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var participant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
            database.ChallengeParticipants.Add(participant);
            database.Careers.Add(new Career { ParticipantId = participant.Id, TrainerName = "Rival" });
            await database.SaveChangesAsync();
        }
        using var rivalClient = await CreateAuthenticatedClient(rival.Id);
        var stintsUrl = $"/api/challenges/{challenge.Id}/career/stints";
        var stint = new
        {
            clubName = "Porto", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 1, eligibleSeasonIndex = (int?)null,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, stint)).StatusCode);
        var brazil = await client.PostAsJsonAsync(stintsUrl, stint with
        {
            clubName = "Brazil", countryCode = "BR", countryName = "Brazil", startSeasonIndex = 1, endSeasonIndex = 1,
        });
        Assert.Equal(HttpStatusCode.Created, brazil.StatusCode);
        var brazilId = (await brazil.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var teamsUrl = $"/api/challenges/{challenge.Id}/national-teams";
        var japan = new
        {
            teamName = "Japan", countryCode = "JP", startSeasonIndex = 2, endSeasonIndex = (int?)2,
            officialMatches = 1, notes = "", version = (long?)null,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(teamsUrl, japan with { countryCode = "INVALID" })).StatusCode);
        var japanResponse = await client.PostAsJsonAsync(teamsUrl, japan);
        Assert.Equal(HttpStatusCode.Created, japanResponse.StatusCode);
        var japanId = (await japanResponse.Content.ReadFromJsonAsync<TestSavedTransfer>())!.Id;
        var url = $"/api/challenges/{challenge.Id}/career/three-continents-milestone";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { seasonIndex = 1, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rivalClient.PutAsJsonAsync(url, new { seasonIndex = 2, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new { seasonIndex = 2, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, new { seasonIndex = 2, version = 0L })).StatusCode);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        Assert.True((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.ThreeContinentsMilestoneValid);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        Assert.Equal(10, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown,
            item => item.Label.StartsWith("G053")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{teamsUrl}/{japanId}", new
        {
            teamName = "Japan", startSeasonIndex = 2, endSeasonIndex = (int?)2,
            officialMatches = 1, notes = "", version = 1L,
        })).StatusCode);
        Assert.True((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.ThreeContinentsMilestoneValid);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims",
            new { code = "G053", seasonIndex = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{brazilId}/matches",
            new { officialMatches = 0, eligibleSeasonIndex = (int?)null, version = 0L })).StatusCode);
        Assert.False((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.ThreeContinentsMilestoneValid);
        Assert.DoesNotContain((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G053"));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new { seasonIndex = (int?)null, version = 1L })).StatusCode);
    }

    [Fact]
    public async Task CountryMilestoneCombinesClubsAndRecalculatesAfterCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Country career", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var other = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
            database.ChallengeParticipants.Add(other);
            database.Careers.Add(new Career { ParticipantId = other.Id, TrainerName = "Rival" });
            await database.SaveChangesAsync();
        }
        using var rivalClient = await CreateAuthenticatedClient(rival.Id);
        var stintsUrl = $"/api/challenges/{challenge.Id}/career/stints";
        var start = new
        {
            clubName = "Start", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 20, eligibleSeasonIndex = (int?)0,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, start)).StatusCode);
        var firstResponse = await client.PostAsJsonAsync(stintsUrl, start with
        {
            clubName = "Spanish First", countryCode = "ES", countryName = "Spain", officialMatches = 12, eligibleSeasonIndex = null,
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstId = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var secondResponse = await client.PostAsJsonAsync(stintsUrl, start with
        {
            clubName = "Spanish Second", countryCode = "ES", countryName = "Spain", startSeasonIndex = 2,
            endSeasonIndex = null, officialMatches = 8, eligibleSeasonIndex = null,
        });
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var secondId = (await secondResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var milestoneUrl = $"{stintsUrl}/{secondId}/country-milestone";
        Assert.Equal(HttpStatusCode.NotFound, (await rivalClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 1, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 1L })).StatusCode);
        var claim = new
        {
            code = "G041", seasonIndex = 2, clubName = "", countryCode = "ES", notes = "",
            startingCareerClub = "Start", startingCareerCountry = "PT",
        };
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, claim)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, claim)).StatusCode);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        var second = Assert.Single(career.Clubs, item => item.Id == secondId);
        Assert.Equal(20, second.CountryOfficialMatches);
        Assert.Equal(2, second.CountryEligibleSeason);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        Assert.Equal(5, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{firstId}/matches",
            new { officialMatches = 11, eligibleSeasonIndex = (int?)null, version = 0L })).StatusCode);
        Assert.Equal(0, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        second = Assert.Single(career.Clubs, item => item.Id == secondId);
        Assert.Null(second.CountryEligibleSeason);
        Assert.Equal(2, second.CountryEligibleSeasonIndex);
        Assert.False(second.CountryMilestoneValid);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, start with
        {
            clubName = "Spanish Third", countryCode = "ES", countryName = "Spain", startSeasonIndex = 3,
            endSeasonIndex = null, officialMatches = 20, eligibleSeasonIndex = 3,
        })).StatusCode);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        second = Assert.Single(career.Clubs, item => item.Id == secondId);
        Assert.Equal(3, second.CountryEligibleSeason);
        Assert.False(second.CountryMilestoneValid);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{firstId}/matches",
            new { officialMatches = 12, eligibleSeasonIndex = (int?)null, version = 1L })).StatusCode);
        Assert.True(Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs,
            item => item.Id == secondId).CountryMilestoneValid);
        Assert.Equal(5, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = (int?)null, version = 1L })).StatusCode);
        Assert.Equal(0, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 2L })).StatusCode);
        Assert.Equal(5, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{stintsUrl}/{secondId}?version=3")).StatusCode);
        Assert.Equal(0, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown, item => item.Label.StartsWith("G041")).Points);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.Equal(3, Assert.Single(career.Clubs, item => item.OfficialMatches == 20 && item.CountryEligibleSeason == 3).CountryEligibleSeason);
    }

    [Fact]
    public async Task ClubEligibilityCombinesRecordedStintsAndRespondsToCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Returning to a club", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var stintsUrl = $"/api/challenges/{challenge.Id}/career/stints";
        var stint = new
        {
            clubName = "Start", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 20, eligibleSeasonIndex = (int?)0,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, stint)).StatusCode);
        var firstResponse = await client.PostAsJsonAsync(stintsUrl, stint with
        {
            clubName = "Return Club", officialMatches = 12, eligibleSeasonIndex = null,
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstId = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var returned = stint with
        {
            clubName = " return club ", startSeasonIndex = 2, endSeasonIndex = null,
            officialMatches = 8, eligibleSeasonIndex = 2,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(stintsUrl, returned with { officialMatches = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(stintsUrl, returned with { countryCode = "ES" })).StatusCode);
        var returnedResponse = await client.PostAsJsonAsync(stintsUrl, returned);
        Assert.Equal(HttpStatusCode.Created, returnedResponse.StatusCode);
        var returnedId = (await returnedResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        var returnedInfo = Assert.Single(career.Clubs, item => item.Id == returnedId);
        Assert.Equal(20, returnedInfo.ClubOfficialMatches);
        Assert.Equal(2, returnedInfo.ClubEligibleSeasonIndex);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var claimResponse = await client.PostAsJsonAsync(claimsUrl, new
        {
            code = "G037", seasonIndex = 2, clubName = "Return Club", countryCode = "PT", notes = "",
            startingCareerClub = "Start", startingCareerCountry = "PT",
        });
        Assert.Equal(HttpStatusCode.Created, claimResponse.StatusCode);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{firstId}/matches",
            new { officialMatches = 11, eligibleSeasonIndex = (int?)null, version = 0L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        returnedInfo = Assert.Single(career.Clubs, item => item.Id == returnedId);
        Assert.Equal(19, returnedInfo.ClubOfficialMatches);
        Assert.Null(returnedInfo.ClubEligibleSeasonIndex);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{firstId}/matches",
            new { officialMatches = 12, eligibleSeasonIndex = (int?)null, version = 1L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
    }

    [Fact]
    public async Task ClubMoveRequiresCareerEvidenceAndRecalculatesAfterMatchCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var challengeResponse = await client.PostAsJsonAsync("/api/challenges", new { name = "Career move", startSeasonYear = 2026 });
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var stintsUrl = $"/api/challenges/{challenge.Id}/career/stints";
        var start = new
        {
            clubName = "Starting Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)0, initialDivisionLevel = 3, officialMatches = 20, eligibleSeasonIndex = (int?)0,
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, start)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(stintsUrl, start with { clubName = "Alternate Club" })).StatusCode);
        var second = start with { clubName = "Next Club", startSeasonIndex = 1, endSeasonIndex = null, officialMatches = 19, eligibleSeasonIndex = null };
        var secondResponse = await client.PostAsJsonAsync(stintsUrl, second);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var secondId = (await secondResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var move = new
        {
            code = "G037", seasonIndex = 2, clubName = "Next Club", countryCode = "PT", notes = "",
            startingCareerClub = "Starting Club", startingCareerCountry = "PT",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, move)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{secondId}/matches", new { officialMatches = 20, eligibleSeasonIndex = 2, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, move with { clubName = "Starting Club", seasonIndex = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, move with { seasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, move with { startingCareerClub = "Next Club" })).StatusCode);
        var claimResponse = await client.PostAsJsonAsync(claimsUrl, move);
        Assert.Equal(HttpStatusCode.Created, claimResponse.StatusCode);
        var claim = (await claimResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl,
            move with { seasonIndex = 0, clubName = "Starting Club", startingCareerClub = "Alternate Club" })).StatusCode);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{secondId}/matches", new { officialMatches = 19, eligibleSeasonIndex = (int?)null, version = 1L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        var ineligible = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037"));
        Assert.Equal(0, ineligible.Points);
        Assert.Contains("career requirements", ineligible.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{claimsUrl}/{claim.Id}", new
        {
            seasonIndex = 2, clubName = "Next Club", countryCode = "PT", notes = "", version = 0L,
            startingCareerClub = "Starting Club", startingCareerCountry = "PT",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{secondId}/matches", new { officialMatches = 20, eligibleSeasonIndex = 2, version = 2L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
        var correction = new
        {
            clubName = "Next Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 1,
            endSeasonIndex = (int?)3, initialDivisionLevel = 2, officialMatches = 20, eligibleSeasonIndex = (int?)2, version = 2L,
        };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{stintsUrl}/{secondId}", correction)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{stintsUrl}/{secondId}", correction with { version = 3L, endSeasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{stintsUrl}/{secondId}", correction with { version = 3L })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{stintsUrl}/{secondId}?version=3")).StatusCode);
        var foreignClub = second with
        {
            clubName = "Foreign Club", countryCode = "ES", countryName = "Spain",
            startSeasonIndex = 3, officialMatches = 20, eligibleSeasonIndex = (int?)4,
        };
        var foreignResponse = await client.PostAsJsonAsync(stintsUrl, foreignClub);
        Assert.Equal(HttpStatusCode.Created, foreignResponse.StatusCode);
        var foreignId = (await foreignResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var countryMove = new
        {
            code = "G041", seasonIndex = 4, clubName = (string?)null, countryCode = "ES", notes = "",
            startingCareerClub = "Starting Club", startingCareerCountry = "PT",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, countryMove with { countryCode = "PT", seasonIndex = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, countryMove with { seasonIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, countryMove with { startingCareerClub = "Alternate Club" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, countryMove)).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"{stintsUrl}/{foreignId}/matches", new { officialMatches = 19, eligibleSeasonIndex = (int?)null, version = 0L })).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G041")).Points);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{stintsUrl}/{secondId}?version=4")).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(0, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G037")).Points);
    }

    [Fact]
    public async Task ScoresExposePendingVictoryUntilBothManagersCompleteTheSeason()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Pending win", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active, TargetPoints = 10 };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var opponentCareer = new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant);
            database.Careers.AddRange(ownerCareer, opponentCareer);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed },
                new CareerSeason { CareerId = opponentCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.InProgress });
            database.CareerStints.AddRange(new[] { "A", "B", "C" }.Select(name => new CareerStint
            {
                CareerId = ownerCareer.Id, ClubName = name, CountryCode = "PT", CountryName = "Portugal",
                StartSeasonIndex = 0, InitialDivisionLevel = 1, OfficialMatches = 20, EligibleSeasonIndex = 0,
            }));
            database.AchievementClaims.Add(new AchievementClaim
            {
                CareerId = ownerCareer.Id, ParticipantId = ownerParticipant.Id, AchievementCode = "G008", SeasonIndex = 0,
                OccurrenceKey = "season:0",
            });
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = await ownerClient.GetFromJsonAsync<TestScore>(scoreUrl);
        Assert.True(score!.VictoryPending);
        Assert.Equal(0, score.PendingVictorySeasonIndex);
        var legacyClaim = Assert.Single(score.Breakdown, item => item.SourceType == "Achievement" && item.Label.StartsWith("G008"));
        Assert.Contains("older record", legacyClaim.Explanation);
        var seasonUpdate = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = 0L, confirmTransferCurrencyRelabel = false,
        };
        Assert.Equal(HttpStatusCode.OK, (await opponentClient.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/seasons/0", seasonUpdate)).StatusCode);
        score = await ownerClient.GetFromJsonAsync<TestScore>(scoreUrl);
        Assert.False(score!.VictoryPending);
        Assert.Null(score.PendingVictorySeasonIndex);
    }

    [Fact]
    public async Task ScoreBreakdownExplainsCheckpointPendingOnUnresolvedDuel()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Pending checkpoint", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var opponentCareer = new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant);
            database.Careers.AddRange(ownerCareer, opponentCareer);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LargestOutgoingTransfer = 100, Currency = "EUR" },
                new CareerSeason { CareerId = opponentCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LargestOutgoingTransfer = 100, Currency = "GBP" });
            database.AchievementClaims.AddRange(new[] { "G001", "G008", "G020" }.Select(code => new AchievementClaim
            {
                CareerId = ownerCareer.Id, ParticipantId = ownerParticipant.Id, AchievementCode = code, SeasonIndex = 0,
                OccurrenceKey = "season:0",
            }));
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var pending = await ownerClient.GetFromJsonAsync<TestScore>(scoreUrl);
        var checkpoint = Assert.Single(pending!.Breakdown, item => item.SourceType == "Checkpoint");
        Assert.Equal("Pending", checkpoint.Status);
        Assert.Contains("scoring is still pending", checkpoint.Explanation);

        var seasonUpdate = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 100m,
            currency = "EUR", notes = "", isCompleted = true, version = 0L, confirmTransferCurrencyRelabel = false,
        };
        Assert.Equal(HttpStatusCode.OK, (await opponentClient.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/seasons/0", seasonUpdate)).StatusCode);
        var confirmed = await ownerClient.GetFromJsonAsync<TestScore>(scoreUrl);
        checkpoint = Assert.Single(confirmed!.Breakdown, item => item.SourceType == "Checkpoint");
        Assert.Equal("Confirmed", checkpoint.Status);
        Assert.Null(checkpoint.Explanation);
    }

    [Fact]
    public async Task NewCareerRequiresStartBeforeGameplayAndAllowsOneInitialClub()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Required start", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        var root = $"/api/challenges/{challenge.Id}";
        var pendingCareer = await client.GetFromJsonAsync<JsonElement>($"{root}/career");
        Assert.True(pendingCareer.GetProperty("requiresStartingStatus").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pendingCareer.GetProperty("startingStatus").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{root}/achievement-claims",
            new { code = "G008", seasonIndex = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{root}/national-teams",
            new { teamName = "Portugal", startSeasonIndex = 0, officialMatches = 20 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{root}/transfers",
            new { seasonIndex = 0, clubName = "Club", playerName = "Player", direction = "Outgoing", fixedAmount = 1, currency = "EUR" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{root}/seasons/0",
            new { leagueMatches = 0, isCompleted = true })).StatusCode);
        var stint = new { clubName = "First", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 1, officialMatches = 0, eligibleSeasonIndex = (int?)null };
        var firstResponse = await client.PostAsJsonAsync($"{root}/career/stints", stint);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstId = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{root}/career/stints", stint with { clubName = "Second" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{root}/career/start",
            new { status = "Club", stintId = firstId, version = 0L })).StatusCode);
        await ConfirmUnemployedStart(client, challenge.Id);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{root}/career/stints", stint with { clubName = "Second" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{root}/national-teams",
            new { teamName = "Portugal", startSeasonIndex = 0, officialMatches = 0 })).StatusCode);
    }

    [Fact]
    public async Task CareerStartAllowsUnemploymentAndProtectsDeclaredStartingClub()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        var challenge = new Challenge { Name = "Career start", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id };
        var rivalParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
        var career = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var rivalCareer = new Career { ParticipantId = rivalParticipant.Id, TrainerName = "Rival" };
        var first = new CareerStint { CareerId = career.Id, ClubName = "First", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 0, InitialDivisionLevel = 2 };
        var top = new CareerStint { CareerId = career.Id, ClubName = "Top", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 0, InitialDivisionLevel = 1 };
        var later = new CareerStint { CareerId = career.Id, ClubName = "Later", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 2, InitialDivisionLevel = 3 };
        var foreign = new CareerStint { CareerId = rivalCareer.Id, ClubName = "Foreign", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 0, InitialDivisionLevel = 2 };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, rivalParticipant);
            database.Careers.AddRange(career, rivalCareer);
            database.CareerStints.AddRange(first, top, later, foreign);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var url = $"/api/challenges/{challenge.Id}/career/start";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { status = "Club", stintId = top.Id, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { status = "Club", stintId = later.Id, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { status = "Club", stintId = foreign.Id, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { status = "Unemployed", stintId = first.Id, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new { status = "Club", stintId = first.Id, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, new { status = "Unemployed", stintId = (Guid?)null, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/challenges/{challenge.Id}/career/stints/{first.Id}?version=0")).StatusCode);
        var edit = new { clubName = "First", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 1, officialMatches = 0, eligibleSeasonIndex = (int?)null, version = 0 };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/challenges/{challenge.Id}/career/stints/{first.Id}", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new { status = "Unemployed", stintId = (Guid?)null, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/challenges/{challenge.Id}/career/stints/{first.Id}", edit)).StatusCode);
        var response = await client.GetFromJsonAsync<JsonElement>($"/api/challenges/{challenge.Id}/career");
        Assert.Equal("Unemployed", response.GetProperty("startingStatus").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("startingStintId").ValueKind);
        Assert.Equal(2, response.GetProperty("startingStatusVersion").GetInt64());
    }

    [Fact]
    public async Task DetailedSeasonAggregatesClubStintsAndProtectsAllocations()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        var challenge = new Challenge { Name = "Detailed season", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var rivalParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var rivalCareer = new Career { ParticipantId = rivalParticipant.Id, TrainerName = "Rival" };
        var first = new CareerStint { CareerId = ownerCareer.Id, ClubName = "First", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 0, EndSeasonIndex = 1, InitialDivisionLevel = 2, OfficialMatches = 10 };
        var second = new CareerStint { CareerId = ownerCareer.Id, ClubName = "Second", CountryCode = "ES", CountryName = "Spain", StartSeasonIndex = 0, EndSeasonIndex = 1, InitialDivisionLevel = 1, OfficialMatches = 12 };
        var other = new CareerStint { CareerId = rivalCareer.Id, ClubName = "Rival", CountryCode = "PT", CountryName = "Portugal", StartSeasonIndex = 0, InitialDivisionLevel = 1, OfficialMatches = 10 };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, rivalParticipant);
            database.Careers.AddRange(ownerCareer, rivalCareer);
            database.CareerStints.AddRange(first, second, other);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, Currency = "EUR" },
                new CareerSeason { CareerId = rivalCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LeagueMatches = 20, LeagueWins = 10, LeagueDraws = 0, LeagueLosses = 10, LeaguePoints = 30, GoalsFor = 20, Currency = "EUR" });
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        var url = $"/api/challenges/{challenge.Id}/seasons/0";
        var row = new
        {
            careerStintId = first.Id, divisionLevel = 2, officialMatches = 10, leagueMatches = 10,
            leagueWins = 4, leagueDraws = 3, leagueLosses = 3, leaguePoints = 20,
            goalsFor = 15, goalsAgainst = 10, leaguePosition = (int?)4,
        };
        var update = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = (long?)0, confirmTransferCurrencyRelabel = false,
            usesClubBreakdown = true, clubSeasons = new[] { row, row with { careerStintId = second.Id, officialMatches = 12, divisionLevel = 1 } },
            clearClubBreakdown = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(url, update with { clubSeasons = new[] { row, row } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(url, update with { clubSeasons = new[] { row with { careerStintId = other.Id } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(url, update with { clubSeasons = new[] { row with { officialMatches = 11 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(url, update)).StatusCode);
        var seasons = await ownerClient.GetFromJsonAsync<List<TestDetailedSeason>>($"/api/challenges/{challenge.Id}/seasons");
        var saved = Assert.Single(seasons!);
        Assert.True(saved.UsesClubBreakdown);
        Assert.Equal(20, saved.LeagueMatches);
        Assert.Equal(40, saved.LeaguePoints);
        Assert.Equal(2, saved.ClubSeasons.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var duels = await database.ScoreEvents.Where(item => item.ChallengeId == challenge.Id && item.SourceType == "Duel").ToListAsync();
            Assert.Equal(7, duels.Where(item => item.ParticipantId == ownerParticipant.Id).Sum(item => item.Points));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(url, update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(url, new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0, goalsFor = 0, goalsAgainst = 0,
            largestOutgoingTransfer = 0m, currency = "EUR", isCompleted = true, version = 1L,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.DeleteAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{first.Id}?version=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PatchAsJsonAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{first.Id}/matches",
            new { officialMatches = 9, eligibleSeasonIndex = (int?)null, version = 0L })).StatusCode);
        var emptyRow = row with { officialMatches = 0, leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0, goalsFor = 0, goalsAgainst = 0 };
        var laterUrl = $"/api/challenges/{challenge.Id}/seasons/1";
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(laterUrl,
            update with { version = null, clubSeasons = new[] { emptyRow }, isCompleted = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(laterUrl,
            update with { version = (long?)1, usesClubBreakdown = false, clubSeasons = null!, clearClubBreakdown = true, isCompleted = false })).StatusCode);
        var quick = update with { usesClubBreakdown = false, clubSeasons = null!, version = (long?)1 };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(url, quick)).StatusCode);
        seasons = await ownerClient.GetFromJsonAsync<List<TestDetailedSeason>>($"/api/challenges/{challenge.Id}/seasons");
        saved = Assert.Single(seasons!, item => item.SeasonIndex == 0);
        Assert.False(saved.UsesClubBreakdown);
        Assert.Equal(2, saved.ClubSeasons.Count);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(url, quick with { version = 2, clearClubBreakdown = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync(
            $"/api/challenges/{challenge.Id}/career/stints/{first.Id}?version=0")).StatusCode);
    }

    [Fact]
    public async Task CompleteSeasonAllocationsCanEstablishMilestonesWithoutAnAttestation()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var created = await client.PostAsJsonAsync("/api/challenges", new { name = "Automatic chronology", startSeasonYear = 2026 });
        var challenge = (await created.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var response = await client.PostAsJsonAsync($"{careerUrl}/stints", new
        {
            clubName = "Automatic Club", countryCode = "PT", countryName = "Portugal", startSeasonIndex = 0,
            endSeasonIndex = (int?)null, initialDivisionLevel = 1, officialMatches = 20, eligibleSeasonIndex = (int?)null,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var before = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs);
        Assert.Null(before.ClubEligibleSeasonIndex);
        var row = new
        {
            careerStintId = before.Id, divisionLevel = 1, officialMatches = 20, leagueMatches = 20,
            leagueWins = 10, leagueDraws = 5, leagueLosses = 5, leaguePoints = 35,
            goalsFor = 30, goalsAgainst = 20, leaguePosition = (int?)4,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/challenges/{challenge.Id}/seasons/0", new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = (long?)null, confirmTransferCurrencyRelabel = false,
            usesClubBreakdown = true, clubSeasons = new[] { row }, clearClubBreakdown = false,
        })).StatusCode);
        var after = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs);
        Assert.Null(after.EligibleSeasonIndex);
        Assert.Equal(0, after.ClubEligibleSeasonIndex);
        Assert.Equal(0, after.CountryEligibleSeason);
    }

    [Fact]
    public async Task CompleteSeasonAllocationsOverrideAnInaccurateClubMilestone()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var challenge = new Challenge { Name = "Match chronology", CreatedByUserId = owner.Id };
        var participant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var career = new Career { ParticipantId = participant.Id, TrainerName = "Owner" };
        var stint = new CareerStint
        {
            CareerId = career.Id, ClubName = "Club", CountryCode = "PT", CountryName = "Portugal",
            StartSeasonIndex = 0, EndSeasonIndex = 1, InitialDivisionLevel = 1, OfficialMatches = 20, EligibleSeasonIndex = 0,
        };
        var startingStint = new CareerStint
        {
            CareerId = career.Id, ClubName = "Start", CountryCode = "ES", CountryName = "Spain",
            StartSeasonIndex = 0, EndSeasonIndex = 0, InitialDivisionLevel = 1, OfficialMatches = 0,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.Add(participant);
            database.Careers.Add(career);
            database.CareerStints.AddRange(stint, startingStint);
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var row = new
        {
            careerStintId = stint.Id, divisionLevel = 1, officialMatches = 10, leagueMatches = 10,
            leagueWins = 4, leagueDraws = 3, leagueLosses = 3, leaguePoints = 15,
            goalsFor = 12, goalsAgainst = 10, leaguePosition = (int?)4,
        };
        var season = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = (long?)null, confirmTransferCurrencyRelabel = false,
            usesClubBreakdown = true, clubSeasons = new[] { row }, clearClubBreakdown = false,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/challenges/{challenge.Id}/seasons/0", season)).StatusCode);
        var before = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs, item => item.Id == stint.Id);
        Assert.Equal(0, before.ClubEligibleSeasonIndex);
        Assert.True(before.ClubMilestoneValid);
        var claimsUrl = $"/api/challenges/{challenge.Id}/achievement-claims";
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var clubMove = new
        {
            code = "G037", seasonIndex = 0, clubName = "Club", countryCode = "PT", notes = "",
            startingCareerClub = "Start", startingCareerCountry = "ES",
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, clubMove)).StatusCode);
        Assert.Equal(5, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown,
            item => item.Label.StartsWith("G037")).Points);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/challenges/{challenge.Id}/seasons/1", season)).StatusCode);
        var after = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs, item => item.Id == stint.Id);
        Assert.Equal(1, after.ClubEligibleSeasonIndex);
        Assert.Equal(1, after.CountryEligibleSeason);
        Assert.False(after.ClubMilestoneValid);
        Assert.Equal(0, Assert.Single((await client.GetFromJsonAsync<TestScore>(scoreUrl))!.Breakdown,
            item => item.Label.StartsWith("G037")).Points);
        var countryMove = new
        {
            code = "G041", seasonIndex = 0, clubName = (string?)null, countryCode = "PT", notes = "",
            startingCareerClub = "Start", startingCareerCountry = "ES",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, countryMove)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, countryMove with { seasonIndex = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(
            $"{careerUrl}/stints/{stint.Id}/country-milestone", new { seasonIndex = 0, version = 0L })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"{careerUrl}/stints/{stint.Id}/country-milestone", new { seasonIndex = 1, version = 0L })).StatusCode);
        var quickSeason = new
        {
            leagueMatches = 10, leagueWins = 4, leagueDraws = 3, leagueLosses = 3, leaguePoints = 15,
            goalsFor = 12, goalsAgainst = 10, leaguePosition = (int?)4, largestOutgoingTransfer = 0m,
            currency = "EUR", notes = "", isCompleted = true, version = 1L, confirmTransferCurrencyRelabel = false,
            usesClubBreakdown = false, clearClubBreakdown = false,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/seasons/1", quickSeason)).StatusCode);
        var fallback = Assert.Single((await client.GetFromJsonAsync<TestCareer>(careerUrl))!.Clubs, item => item.Id == stint.Id);
        Assert.Equal(0, fallback.ClubEligibleSeasonIndex);
        Assert.Equal(0, fallback.CountryEligibleSeason);
        Assert.True(fallback.ClubMilestoneValid);
        var fallbackScore = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(5, Assert.Single(fallbackScore.Breakdown, item => item.Label.StartsWith("G037")).Points);
        Assert.Equal(0, Assert.Single(fallbackScore.Breakdown, item => item.Label.StartsWith("G041")).Points);
    }

    [Fact]
    public async Task LegacyTransferInAnotherCurrencyKeepsDuelPendingUntilCorrected()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        var challenge = new Challenge { Name = "Legacy currency", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var rivalParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var rivalCareer = new Career { ParticipantId = rivalParticipant.Id, TrainerName = "Rival" };
        var transfer = new Transfer
        {
            CareerId = ownerCareer.Id, SeasonIndex = 0, ClubName = "Club", PlayerName = "Player",
            CounterpartyName = "Other club", Direction = "Outgoing", FixedAmount = 200, Currency = "GBP", Version = 1,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, rivalParticipant);
            database.Careers.AddRange(ownerCareer, rivalCareer);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, Currency = "EUR" },
                new CareerSeason { CareerId = rivalCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LargestOutgoingTransfer = 150, Currency = "EUR" });
            database.Transfers.Add(transfer);
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var scoresUrl = $"/api/challenges/{challenge.Id}/scores";
        var pending = await client.GetFromJsonAsync<TestScore>(scoresUrl);
        Assert.Contains(0, pending!.CurrencyMismatchSeasons);
        Assert.Equal(2, pending.Breakdown.Count(item => item.SourceType == "Duel" && item.Status == "Pending"));
        Assert.All(pending.Totals, total => Assert.Equal(0, total.ConfirmedPoints));
        var correction = new
        {
            seasonIndex = 0, clubName = "Club", playerName = "Player", counterpartyName = "Other club",
            direction = "Outgoing", fixedAmount = 200m, currency = "EUR", notes = "", version = 1L,
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"/api/challenges/{challenge.Id}/transfers/{transfer.Id}", correction)).StatusCode);
        var corrected = await client.GetFromJsonAsync<TestScore>(scoresUrl);
        Assert.Empty(corrected!.CurrencyMismatchSeasons);
        var duel = Assert.Single(corrected.Breakdown, item => item.SourceType == "Duel");
        Assert.Equal("Confirmed", duel.Status);
        Assert.Equal(4, duel.Points);
    }

    [Fact]
    public async Task TransfersAreOwnerScopedAndRecalculateTheSeasonDuel()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Transfer test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        var opponentCareer = new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant);
            database.Careers.AddRange(ownerCareer, opponentCareer);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = ownerCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LargestOutgoingTransfer = 100, Currency = "EUR" },
                new CareerSeason { CareerId = opponentCareer.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27", Status = SeasonStatus.Completed, LargestOutgoingTransfer = 150, Currency = "EUR" });
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var url = $"/api/challenges/{challenge.Id}/transfers";
        var transfer = new
        {
            seasonIndex = 0, clubName = "Club A", playerName = "Player A", counterpartyName = "Club B",
            direction = "Outgoing", fixedAmount = 200m, currency = "EUR", notes = "", version = (long?)null,
        };
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(url, transfer with { currency = "GBP" })).StatusCode);
        var created = await ownerClient.PostAsJsonAsync(url, transfer);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<TestSavedTransfer>();
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(url, transfer)).StatusCode);
        var itemUrl = $"{url}/{saved.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.PutAsJsonAsync(itemUrl, transfer with { version = 1 })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var duel = await database.ScoreEvents.SingleAsync(item => item.ChallengeId == challenge.Id && item.SourceType == "Duel");
            Assert.Equal(ownerParticipant.Id, duel.ParticipantId);
        }
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(itemUrl, transfer with { fixedAmount = 120, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.DeleteAsync($"{itemUrl}?version=1")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var duel = await database.ScoreEvents.SingleAsync(item => item.ChallengeId == challenge.Id && item.SourceType == "Duel");
            Assert.Equal(opponentParticipant.Id, duel.ParticipantId);
        }
        var seasonUpdate = new
        {
            leagueMatches = 0, leagueWins = 0, leagueDraws = 0, leagueLosses = 0, leaguePoints = 0,
            goalsFor = 0, goalsAgainst = 0, leaguePosition = (int?)null, largestOutgoingTransfer = 100m,
            currency = "GBP", notes = "", isCompleted = true, version = 0L, confirmTransferCurrencyRelabel = false,
        };
        var seasonUrl = $"/api/challenges/{challenge.Id}/seasons/0";
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(seasonUrl, seasonUpdate)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(
            seasonUrl, seasonUpdate with { confirmTransferCurrencyRelabel = true })).StatusCode);
        var score = await ownerClient.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores");
        Assert.Contains(0, score!.CurrencyMismatchSeasons);
        Assert.Equal(0, score.CommonSeasonIndex);
        Assert.All(score.Totals, total =>
        {
            Assert.Equal(0, total.ConfirmedPoints);
            Assert.Equal(4, total.PendingPoints);
            Assert.Equal(0, total.CommonSeasonPoints);
        });
        var pendingDuels = score.Breakdown.Where(item => item.SourceType == "Duel" && item.Status == "Pending").ToArray();
        Assert.Equal(2, pendingDuels.Length);
        Assert.All(pendingDuels, item => Assert.Contains("currencies differ", item.Explanation));
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync($"{itemUrl}?version=3")).StatusCode);
        var transfers = await ownerClient.GetFromJsonAsync<List<TestSavedTransfer>>(url);
        Assert.Empty(transfers!);

        var teamsUrl = $"/api/challenges/{challenge.Id}/national-teams";
        var team = new { teamName = "Portugal", startSeasonIndex = 0, endSeasonIndex = (int?)null, officialMatches = 20, notes = "", version = (long?)null };
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(teamsUrl, team with { endSeasonIndex = -1 })).StatusCode);
        var createdTeam = await ownerClient.PostAsJsonAsync(teamsUrl, team);
        Assert.Equal(HttpStatusCode.Created, createdTeam.StatusCode);
        var savedTeam = await createdTeam.Content.ReadFromJsonAsync<TestSavedTransfer>();
        Assert.NotNull(savedTeam);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(teamsUrl, team)).StatusCode);
        var teamUrl = $"{teamsUrl}/{savedTeam.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await opponentClient.PutAsJsonAsync(teamUrl, team with { version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(teamUrl, team with { officialMatches = 24, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.DeleteAsync($"{teamUrl}?version=1")).StatusCode);
        var opponentTeams = await opponentClient.GetFromJsonAsync<List<TestSavedTransfer>>(teamsUrl);
        Assert.Empty(opponentTeams!);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            Assert.Equal(0, (await database.CareerSeasons.SingleAsync(item => item.CareerId == ownerCareer.Id)).LeagueMatches);
            Assert.Equal(24, (await database.NationalTeamStints.SingleAsync(item => item.Id == savedTeam.Id)).OfficialMatches);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync($"{teamUrl}?version=2")).StatusCode);
    }

    [Fact]
    public async Task SupplementalRulesCanBePreviewedAndVersioned()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var challenge = (await (await ownerClient.PostAsJsonAsync("/api/challenges",
            new { name = "Supplemental rules", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var ownerParticipant = await database.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challenge.Id);
            var ownerCareer = await database.Careers.SingleAsync(item => item.ParticipantId == ownerParticipant.Id);
            var rival = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
            var rivalCareer = new Career { ParticipantId = rival.Id, TrainerName = "Opponent" };
            database.ChallengeParticipants.Add(rival);
            database.Careers.Add(rivalCareer);
            database.CareerSeasons.AddRange(new CareerSeason
                { CareerId = ownerCareer.Id, SeasonIndex = 0, DisplayName = "2026/27", Status = SeasonStatus.Completed },
                new CareerSeason { CareerId = rivalCareer.Id, SeasonIndex = 0, DisplayName = "2026/27", Status = SeasonStatus.Completed });
            database.AchievementClaims.Add(new AchievementClaim
                { CareerId = ownerCareer.Id, ParticipantId = ownerParticipant.Id, AchievementCode = "G008", SeasonIndex = 0, OccurrenceKey = "season:0" });
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        var url = $"/api/challenges/{challenge.Id}/rules";
        var book = (await ownerClient.GetFromJsonAsync<TestRuleBook>(url))!;
        Assert.Contains(book.Rules!, item => item.AchievementCode == "bonus:first-major-national-cup" && item.Points == 5);
        Assert.Contains(book.Rules!, item => item.AchievementCode == "checkpoint-50" && item.Points == 3);
        Assert.Contains(book.Rules!, item => item.AchievementCode == "duel:league-points-per-game" && item.Points == 4);
        var change = new
        {
            code = "bonus:first-major-national-cup", points = 12, isEnabled = true, policy = "Retroactive",
            effectiveSeasonIndex = 0, expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion,
            confirm = false, reason = "Adjust bonus",
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PostAsJsonAsync($"{url}/preview", change)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync($"{url}/preview", change with { code = "duel:unknown" })).StatusCode);
        var previewResponse = await ownerClient.PostAsJsonAsync($"{url}/preview", change);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<TestRuleImpact>())!;
        Assert.Contains(preview.Impact, item => item.AfterConfirmed - item.BeforeConfirmed == 7);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(url, change with { confirm = true })).StatusCode);
        book = (await ownerClient.GetFromJsonAsync<TestRuleBook>(url))!;
        Assert.Contains(book.Rules!, item => item.AchievementCode == change.code && item.Points == 12 && item.IsEnabled);
        var disable = change with
        {
            isEnabled = false, expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = true,
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(url, disable)).StatusCode);
        var score = (await ownerClient.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        Assert.DoesNotContain(score.Breakdown, item => item.SourceType == "FirstBonus" && item.Label.Contains("major national cup"));
    }

    [Fact]
    public async Task RuleVersionsPreviewAndApplyChronologically()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Rule test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var otherChallenge = new Challenge { Name = "Other rule test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var opponentParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var otherParticipant = new ChallengeParticipant { ChallengeId = otherChallenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var ownerCareer = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.AddRange(challenge, otherChallenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, opponentParticipant, otherParticipant);
            database.Careers.AddRange(ownerCareer, new Career { ParticipantId = opponentParticipant.Id, TrainerName = "Opponent" },
                new Career { ParticipantId = otherParticipant.Id, TrainerName = "Owner" });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = otherChallenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            database.AchievementClaims.AddRange(Enumerable.Range(0, 2).Select(index => new AchievementClaim
            {
                CareerId = ownerCareer.Id, ParticipantId = ownerParticipant.Id, AchievementCode = "G008",
                OccurrenceKey = $"season:{index}", SeasonIndex = index,
            }));
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var opponentClient = await CreateAuthenticatedClient(opponent.Id);
        var url = $"/api/challenges/{challenge.Id}/rules";
        var book = await ownerClient.GetFromJsonAsync<TestRuleBook>(url);
        Assert.NotNull(book);
        var request = new { code = "G008", points = 20, isEnabled = true, policy = "FromSeason", effectiveSeasonIndex = 1, expectedVersion = 1, expectedChallengeVersion = book.ChallengeVersion, confirm = false, reason = "Test",
            nameOverride = (string?)"Future label", descriptionOverride = (string?)"Future description", categoryOverride = (string?)"Future category" };
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync($"{url}/preview",
            request with { code = "duel:league-points-per-game" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync($"{url}/preview",
            request with { nameOverride = new string('x', 121) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await opponentClient.PostAsJsonAsync($"{url}/preview", request)).StatusCode);
        var previewResponse = await ownerClient.PostAsJsonAsync($"{url}/preview", request);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<TestRuleImpact>();
        var ownerImpact = Assert.Single(preview!.Impact, item => item.ParticipantId == ownerParticipant.Id);
        Assert.Equal(20, ownerImpact.BeforeConfirmed);
        Assert.Equal(30, ownerImpact.AfterConfirmed);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            Assert.Equal(1, await database.ChallengeRules.Where(item => item.ChallengeId == challenge.Id).MaxAsync(item => item.VersionNumber));
            Assert.Null(await database.ChallengeRules.Where(item => item.ChallengeId == challenge.Id && item.AchievementCode == "G008")
                .Select(item => item.NameOverride).SingleAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(url, request)).StatusCode);
        Guid claimId;
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            claimId = await database.AchievementClaims.Where(item => item.CareerId == ownerCareer.Id && item.SeasonIndex == 0)
                .Select(item => item.Id).SingleAsync();
        }
        var claimUrl = $"/api/challenges/{challenge.Id}/achievement-claims/{claimId}";
        var correction = new { seasonIndex = 2, clubName = (string?)null, countryCode = (string?)null, notes = (string?)null, version = 0L, managedCompetitionMatches = 2, managedDecidingMatch = true };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(claimUrl, correction)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(url, request with { confirm = true })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            Assert.Equal(1, await database.ChallengeRules.Where(item => item.ChallengeId == challenge.Id).MaxAsync(item => item.VersionNumber));
            Assert.Empty(await database.AuditLogs.Where(item => item.ChallengeId == challenge.Id && item.EntityType == "ChallengeRules").ToListAsync());
        }
        var changedBook = await ownerClient.GetFromJsonAsync<TestRuleBook>(url);
        Assert.NotNull(changedBook);
        var changedRequest = request with { expectedChallengeVersion = changedBook.ChallengeVersion };
        var changedPreview = await (await ownerClient.PostAsJsonAsync($"{url}/preview", changedRequest)).Content.ReadFromJsonAsync<TestRuleImpact>();
        Assert.Equal(40, Assert.Single(changedPreview!.Impact, item => item.ParticipantId == ownerParticipant.Id).AfterConfirmed);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(claimUrl, correction with { seasonIndex = 0, version = 1L })).StatusCode);
        book = await ownerClient.GetFromJsonAsync<TestRuleBook>(url);
        Assert.NotNull(book);
        request = request with { expectedChallengeVersion = book.ChallengeVersion };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"{url}/preview", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(url, request with { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(url, request with { confirm = true })).StatusCode);
        var achievements = await ownerClient.GetFromJsonAsync<TestAchievement[]>($"/api/challenges/{challenge.Id}/achievements");
        Assert.Equal("Future label", Assert.Single(achievements!, item => item.Code == "G008").Name);
        var otherAchievements = await ownerClient.GetFromJsonAsync<TestAchievement[]>($"/api/challenges/{otherChallenge.Id}/achievements");
        Assert.NotEqual("Future label", Assert.Single(otherAchievements!, item => item.Code == "G008").Name);
        var score = (await ownerClient.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores"))!;
        var labels = score.Breakdown.Where(item => item.Label.StartsWith("G008")).OrderBy(item => item.SeasonIndex).ToArray();
        Assert.Equal(2, labels.Length);
        Assert.DoesNotContain("Future label", labels[0].Label);
        Assert.Contains("Future label", labels[1].Label);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var events = await database.ScoreEvents.Where(item => item.ChallengeId == challenge.Id && item.SourceType == "Achievement")
                .OrderBy(item => item.SeasonIndex).ToArrayAsync();
            Assert.Equal(new[] { 10, 20 }, events.Select(item => item.Points));
            Assert.Equal(new[] { 1, 2 }, events.Select(item => item.RuleVersion));
            Assert.Equal(53 + RaceScoringEngine.SupplementalRuleDefaults.Count,
                await database.ChallengeRules.CountAsync(item => item.ChallengeId == challenge.Id && item.VersionNumber == 2));
            Assert.Single(await database.AuditLogs.Where(item => item.ChallengeId == challenge.Id && item.EntityType == "ChallengeRules").ToListAsync());
        }
        var updatedBook = await ownerClient.GetFromJsonAsync<TestRuleBook>(url);
        Assert.NotNull(updatedBook);
        var retroactive = request with { points = 5, policy = "Retroactive", expectedVersion = 2,
            nameOverride = (string?)null, descriptionOverride = (string?)null, categoryOverride = (string?)null,
            expectedChallengeVersion = updatedBook.ChallengeVersion, confirm = true };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(url, retroactive)).StatusCode);
        using var finalScope = factory.Services.CreateScope();
        var finalDatabase = finalScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var finalEvents = await finalDatabase.ScoreEvents.Where(item => item.ChallengeId == challenge.Id && item.SourceType == "Achievement")
            .OrderBy(item => item.SeasonIndex).ToListAsync();
        Assert.All(finalEvents, item => Assert.Equal(5, item.Points));
        Assert.Equal(new[] { 3, 4 }, finalEvents.Select(item => item.RuleVersion));
        var finalRules = await finalDatabase.ChallengeRules.Where(item => item.ChallengeId == challenge.Id && item.AchievementCode == "G008"
            && item.VersionNumber >= 3).OrderBy(item => item.VersionNumber).ToArrayAsync();
        Assert.Null(finalRules[0].NameOverride);
        Assert.Equal("Future label", finalRules[1].NameOverride);
        Assert.Equal("Future description", finalRules[1].DescriptionOverride);
        Assert.NotEqual("Future label", (await finalDatabase.AchievementDefinitions.SingleAsync(item => item.Code == "G008")).Name);
    }

    [Fact]
    public async Task FrequencyChangesAreVersionedPreviewedAndEnforcedAgainstEarlierRecords()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(user);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(user.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Frequency versions", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var claimsUrl = $"{root}/achievement-claims";
        var claim = new { code = "G008", seasonIndex = 0, clubName = "A", countryCode = "PT",
            managedCompetitionMatches = 2, managedDecidingMatch = true };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, claim)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(claimsUrl, claim with { seasonIndex = 1 })).StatusCode);
        var book = (await client.GetFromJsonAsync<TestRuleBook>($"{root}/rules"))!;
        var change = new { code = "G008", points = 10, isEnabled = true, policy = "FromSeason", effectiveSeasonIndex = 1,
            expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = false,
            frequencyOverride = "PerClub" };
        var previewResponse = await client.PostAsJsonAsync($"{root}/rules/preview", change);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var impact = (await previewResponse.Content.ReadFromJsonAsync<TestRuleImpact>())!;
        Assert.Equal(20, Assert.Single(impact.Impact).BeforeConfirmed);
        Assert.Equal(10, Assert.Single(impact.Impact).AfterConfirmed);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/rules/preview",
            change with { code = "G042" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/rules/preview",
            change with { frequencyOverride = "Invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{root}/rules", change with { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{root}/rules", change with { confirm = true })).StatusCode);
        var achievements = await client.GetFromJsonAsync<JsonElement>($"{root}/achievements");
        var achievement = achievements.EnumerateArray().Single(item => item.GetProperty("code").GetString() == "G008");
        Assert.Equal("PerClub", achievement.GetProperty("frequency").GetString());
        var versions = achievement.GetProperty("frequencyVersions").EnumerateArray().ToArray();
        Assert.Contains(versions, item => item.GetProperty("effectiveSeasonIndex").GetInt32() == 0 && item.GetProperty("frequency").GetString() == "Season");
        Assert.Contains(versions, item => item.GetProperty("effectiveSeasonIndex").GetInt32() == 1 && item.GetProperty("frequency").GetString() == "PerClub");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(claimsUrl, claim with { seasonIndex = 2, clubName = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(claimsUrl, claim with { seasonIndex = 2 })).StatusCode);
        var third = await client.PostAsJsonAsync(claimsUrl, claim with { seasonIndex = 2, clubName = "B" });
        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        var claimId = (await third.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var correction = new { seasonIndex = 2, clubName = "A", countryCode = "PT", managedCompetitionMatches = 2,
            managedDecidingMatch = true, version = 0L };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{claimsUrl}/{claimId}", correction)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{claimsUrl}/{claimId}", correction with { clubName = "B", seasonIndex = 3 })).StatusCode);
        var scores = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(new[] { 0, 3 }, scores.Breakdown.Where(item => item.Label.StartsWith("G008")).Select(item => item.SeasonIndex).Order().ToArray());
        book = (await client.GetFromJsonAsync<TestRuleBook>($"{root}/rules"))!;
        var oldClient = new { code = "G008", points = 12, isEnabled = true, policy = "Retroactive", effectiveSeasonIndex = 0,
            expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = true };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{root}/rules", oldClient)).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var boundaries = await verify.ChallengeRules.Where(item => item.ChallengeId == challenge.Id
            && item.AchievementCode == "G008" && item.VersionNumber > book.Version).OrderBy(item => item.VersionNumber).ToArrayAsync();
        Assert.Null(boundaries[0].FrequencyOverride);
        Assert.Equal(AchievementFrequency.PerClub, boundaries[1].FrequencyOverride);
        scores = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(new[] { 0, 3 }, scores.Breakdown.Where(item => item.Label.StartsWith("G008")).Select(item => item.SeasonIndex).Order().ToArray());
    }

    [Fact]
    public async Task CupParticipationRulePreviewsRecalculatesAndValidatesEachSeason()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(user);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(user.Id);
        var response = await client.PostAsJsonAsync("/api/challenges", new { name = "Cup minimum", startSeasonYear = 2026 });
        var challenge = (await response.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var claim = new { code = "G008", seasonIndex = 0, clubName = "A", countryCode = "PT",
            managedCompetitionMatches = 2, managedDecidingMatch = true };
        var firstResponse = await client.PostAsJsonAsync($"{root}/achievement-claims", claim);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = (await firstResponse.Content.ReadFromJsonAsync<TestCreatedClaim>())!;
        var book = (await client.GetFromJsonAsync<TestRuleBook>($"{root}/rules"))!;
        var change = new { code = "G008", points = 10, isEnabled = true, policy = "Retroactive", effectiveSeasonIndex = 0,
            expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = false,
            frequencyOverride = (string?)"PerClub", minimumCupMatchesOverride = 3 };
        var preview = await client.PostAsJsonAsync($"{root}/rules/preview", change);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var impact = (await preview.Content.ReadFromJsonAsync<TestRuleImpact>())!;
        Assert.Equal(10, Assert.Single(impact.Impact).BeforeConfirmed);
        Assert.Equal(0, Assert.Single(impact.Impact).AfterConfirmed);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/rules/preview",
            change with { minimumCupMatchesOverride = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/rules/preview",
            change with { code = "G001" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{root}/rules", change with { confirm = true })).StatusCode);
        var score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        var zero = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G008"));
        Assert.Equal(0, zero.Points);
        Assert.Contains("fewer than 3", zero.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/achievement-claims",
            claim with { seasonIndex = 1 })).StatusCode);
        var second = await client.PostAsJsonAsync($"{root}/achievement-claims", claim with { seasonIndex = 1, managedCompetitionMatches = 3 });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(new[] { 0, 10 }, score.Breakdown.Where(item => item.Label.StartsWith("G008"))
            .OrderBy(item => item.SeasonIndex).Select(item => item.Points));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{root}/achievement-claims/{first.Id}",
            new { seasonIndex = 0, clubName = "A", countryCode = "PT", managedCompetitionMatches = 3,
                managedDecidingMatch = true, version = 0L })).StatusCode);
        book = (await client.GetFromJsonAsync<TestRuleBook>($"{root}/rules"))!;
        var future = change with { policy = "FromSeason", effectiveSeasonIndex = 2,
            expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = true,
            frequencyOverride = (string?)null, minimumCupMatchesOverride = 5 };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{root}/rules", future)).StatusCode);
        book = (await client.GetFromJsonAsync<TestRuleBook>($"{root}/rules"))!;
        var oldClient = new { code = "G008", points = 11, isEnabled = true, policy = "Retroactive", effectiveSeasonIndex = 0,
            expectedVersion = book.Version, expectedChallengeVersion = book.ChallengeVersion, confirm = true };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{root}/rules", oldClient)).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var boundaries = await verify.ChallengeRules.Where(item => item.ChallengeId == challenge.Id
            && item.AchievementCode == "G008" && item.VersionNumber > book.Version).OrderBy(item => item.VersionNumber).ToArrayAsync();
        Assert.Equal(new int?[] { 3, 5 }, boundaries.Select(item => item.MinimumCupMatchesOverride));
    }

    [Fact]
    public async Task FirstClubTrophyRecalculatesWhenEarlierCupEvidenceChanges()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(user);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(user.Id);
        var created = await client.PostAsJsonAsync("/api/challenges", new { name = "First club trophy", startSeasonYear = 2026 });
        var challenge = (await created.Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var root = $"/api/challenges/{challenge.Id}";
        var trophy = new { code = "G040", seasonIndex = 2, clubName = "United", countryCode = "PT",
            firstMajorClubTrophy = true, trophyNotSuperCup = true };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"{root}/achievement-claims", trophy)).StatusCode);
        var cup = new { code = "G008", seasonIndex = 1, clubName = "United", countryCode = "PT",
            managedCompetitionMatches = 2, managedDecidingMatch = true };
        var response = await client.PostAsJsonAsync($"{root}/achievement-claims", cup);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cupId = (await response.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        var eventItem = Assert.Single(score.Breakdown, item => item.Label.StartsWith("G040"));
        Assert.Equal(0, eventItem.Points);
        Assert.Contains("earlier major trophy", eventItem.Explanation);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{root}/achievement-claims",
            trophy with { seasonIndex = 3, clubName = "United" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{root}/achievement-claims/{cupId}?version=0")).StatusCode);
        score = (await client.GetFromJsonAsync<TestScore>($"{root}/scores"))!;
        Assert.Equal(5, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G040")).Points);
    }

    [Fact]
    public async Task ProfileOnboardingAndEditsAreScopedAndValidated()
    {
        var ownerEmail = $"{Guid.NewGuid():N}@example.test";
        var otherEmail = $"{Guid.NewGuid():N}@example.test";
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = ownerEmail, Email = ownerEmail, NormalizedEmail = ownerEmail.ToUpperInvariant(), DisplayName = "Initial", SecurityStamp = Guid.NewGuid().ToString() };
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = otherEmail, Email = otherEmail, NormalizedEmail = otherEmail.ToUpperInvariant(), DisplayName = "Other", SecurityStamp = Guid.NewGuid().ToString() };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, other);
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var otherClient = await CreateAuthenticatedClient(other.Id);
        var before = await ownerClient.GetFromJsonAsync<TestProfile>("/api/me");
        Assert.False(before!.HasCompletedOnboarding);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync("/api/me", new { displayName = " ", avatarUrl = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync("/api/me", new { displayName = "Manager", avatarUrl = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync("/api/me", new { displayName = "Manager", avatarUrl = "https://user:pass@example.test/image" })).StatusCode);
        var response = await ownerClient.PutAsJsonAsync("/api/me", new { displayName = "  Manager  ", avatarUrl = "https://example.test/avatar.png" });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = await response.Content.ReadFromJsonAsync<TestProfile>();
        Assert.Equal("Manager", saved!.DisplayName);
        Assert.True(saved.HasCompletedOnboarding);
        Assert.Equal("https://example.test/avatar.png", saved.AvatarUrl);
        Assert.Equal("Other", (await otherClient.GetFromJsonAsync<TestProfile>("/api/me"))!.DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync("/api/me", new { displayName = "Manager", avatarUrl = (string?)null })).StatusCode);
        Assert.Null((await ownerClient.GetFromJsonAsync<TestProfile>("/api/me"))!.AvatarUrl);
    }

    [Fact]
    public async Task ArchivingAndRestoringPreservesStatusAndRevokesInvitations()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var email = $"{Guid.NewGuid():N}@example.test";
        var invitee = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Invitee",
            Email = email, NormalizedEmail = email.ToUpperInvariant(),
        };
        var challenge = new Challenge { Name = "Archive test", CreatedByUserId = owner.Id };
        var admin = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var career = new Career { ParticipantId = admin.Id, TrainerName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, invitee);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.Add(admin);
            database.Careers.Add(career);
            database.CareerSeasons.AddRange(
                new CareerSeason { CareerId = career.Id, SeasonIndex = 0, StartYear = 2026, DisplayName = "2026/27" },
                new CareerSeason { CareerId = career.Id, SeasonIndex = 2, StartYear = 2028, DisplayName = "2028/29" });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var inviteeClient = await CreateAuthenticatedClient(invitee.Id);
        var invitesUrl = $"/api/challenges/{challenge.Id}/invites";
        var invitation = await (await ownerClient.PostAsJsonAsync(invitesUrl, new { email })).Content.ReadFromJsonAsync<TestInvite>();
        Assert.NotNull(invitation);
        var archiveUrl = $"/api/challenges/{challenge.Id}/archive";
        Assert.Equal(HttpStatusCode.Forbidden, (await inviteeClient.PostAsJsonAsync(archiveUrl, new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(archiveUrl, new { confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(archiveUrl, new { confirm = true })).StatusCode);
        var archivedCards = await ownerClient.GetFromJsonAsync<List<TestChallengeCard>>("/api/me/challenges");
        var card = Assert.Single(archivedCards!, item => item.Id == challenge.Id);
        Assert.Equal("Archived", card.Status);
        Assert.Equal("PendingPlayers", card.StatusBeforeArchive);
        Assert.Equal("Administrator", card.Role);
        Assert.Equal("2028/29", card.CurrentSeason);
        Assert.Equal(HttpStatusCode.Conflict, (await inviteeClient.PostAsync($"/api/me/invitations/{invitation.Id}/accept", null)).StatusCode);
        Assert.Empty((await inviteeClient.GetFromJsonAsync<List<TestInvite>>("/api/me/invitations"))!);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var archived = await database.Challenges.SingleAsync(item => item.Id == challenge.Id);
            Assert.Equal(ChallengeStatus.Archived, archived.Status);
            Assert.Equal(ChallengeStatus.PendingPlayers, archived.StatusBeforeArchive);
            Assert.NotNull((await database.ChallengeInvitations.SingleAsync(item => item.Id == invitation.Id)).RevokedAt);
        }
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(
            $"/api/challenges/{challenge.Id}/restore", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await inviteeClient.PostAsync($"/api/me/invitations/{invitation.Id}/accept", null)).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var restored = await verify.Challenges.SingleAsync(item => item.Id == challenge.Id);
        Assert.Equal(ChallengeStatus.PendingPlayers, restored.Status);
        Assert.Null(restored.StatusBeforeArchive);
        Assert.Equal(2, restored.Version);
        Assert.Equal(2, await verify.AuditLogs.CountAsync(item => item.ChallengeId == challenge.Id
            && (item.Action == "challenge.archived" || item.Action == "challenge.restored")));
        var restoredCards = await ownerClient.GetFromJsonAsync<List<TestChallengeCard>>("/api/me/challenges");
        Assert.Null(Assert.Single(restoredCards!, item => item.Id == challenge.Id).StatusBeforeArchive);
    }

    [Fact]
    public async Task ThreeContinentsCanBeCalculatedFromNationalTeamSeasonMatches()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "Continental chronology", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var url = $"/api/challenges/{challenge.Id}/national-teams";
        var team = new
        {
            teamName = "Portugal", countryCode = "PT", startSeasonIndex = 0, endSeasonIndex = (int?)null,
            officialMatches = 1, notes = "", version = (long?)null,
            seasonMatches = new[] { new { seasonIndex = 0, officialMatches = 1 } },
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, team)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, team with
        {
            teamName = "Brazil", countryCode = "BR", startSeasonIndex = 1,
            seasonMatches = new[] { new { seasonIndex = 1, officialMatches = 1 } },
        })).StatusCode);
        var japan = team with
        {
            teamName = "Japan", countryCode = "JP", startSeasonIndex = 2,
            seasonMatches = new[] { new { seasonIndex = 2, officialMatches = 1 } },
        };
        var response = await client.PostAsJsonAsync(url, japan);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.Equal(2, career.ThreeContinentsCalculatedSeasonIndex);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(2, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G053")).SeasonIndex);
        var milestoneUrl = $"{careerUrl}/three-continents-milestone";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 3, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{id}", japan with
        {
            version = 1L, seasonMatches = new[] { new { seasonIndex = 3, officialMatches = 1 } },
        })).StatusCode);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.Equal(3, career.ThreeContinentsCalculatedSeasonIndex);
        Assert.False(career.ThreeContinentsMilestoneValid);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(3, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G053")).SeasonIndex);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{url}/{id}", japan with
        {
            version = 2L, seasonMatches = japan.seasonMatches[..0],
        })).StatusCode);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.Null(career.ThreeContinentsCalculatedSeasonIndex);
        Assert.True(career.ThreeContinentsMilestoneValid);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(2, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G053")).SeasonIndex);
    }

    [Fact]
    public async Task NationalTeamSeasonAllocationsDeriveAndRecalculateMilestone()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Manager" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var challenge = (await (await client.PostAsJsonAsync("/api/challenges",
            new { name = "National season matches", startSeasonYear = 2026 })).Content.ReadFromJsonAsync<TestCreatedChallenge>())!;
        await ConfirmUnemployedStart(client, challenge.Id);
        var teamsUrl = $"/api/challenges/{challenge.Id}/national-teams";
        var first = new
        {
            teamName = "Portugal", countryCode = "PT", startSeasonIndex = 0, endSeasonIndex = (int?)1,
            officialMatches = 12, notes = "", version = (long?)null,
            seasonMatches = new[] { new { seasonIndex = 0, officialMatches = 4 }, new { seasonIndex = 1, officialMatches = 8 } },
        };
        var second = new
        {
            teamName = "Spain", countryCode = "ES", startSeasonIndex = 1, endSeasonIndex = (int?)2,
            officialMatches = 10, notes = "", version = (long?)null,
            seasonMatches = new[] { new { seasonIndex = 1, officialMatches = 7 }, new { seasonIndex = 2, officialMatches = 3 } },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(teamsUrl, first with
        {
            seasonMatches = new[] { new { seasonIndex = 0, officialMatches = 13 } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(teamsUrl, first with
        {
            seasonMatches = new[] { new { seasonIndex = 0, officialMatches = 1 }, new { seasonIndex = 0, officialMatches = 1 } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(teamsUrl, first)).StatusCode);
        var response = await client.PostAsJsonAsync(teamsUrl, second);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<TestCreatedClaim>())!.Id;
        var careerUrl = $"/api/challenges/{challenge.Id}/career";
        var career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.True(career.NationalTeamSeasonMatchesComplete);
        Assert.Equal(2, career.NationalTeamCalculatedSeasonIndex);
        var scoreUrl = $"/api/challenges/{challenge.Id}/scores";
        var score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(2, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G033")).SeasonIndex);
        var milestoneUrl = $"{careerUrl}/national-team-milestone";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 1, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{teamsUrl}/{id}", second with
        {
            version = 1L, seasonMatches = new[] { new { seasonIndex = 1, officialMatches = 8 }, new { seasonIndex = 2, officialMatches = 2 } },
        })).StatusCode);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.Equal(1, career.NationalTeamCalculatedSeasonIndex);
        Assert.False(career.NationalTeamMilestoneValid);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(1, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G033")).SeasonIndex);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{teamsUrl}/{id}", new
        {
            teamName = "Spain", countryCode = "ES", startSeasonIndex = 1, endSeasonIndex = (int?)2,
            officialMatches = 9, notes = "", version = 2L,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{teamsUrl}/{id}", new
        {
            teamName = "Spain", countryCode = "ES", startSeasonIndex = 1, endSeasonIndex = (int?)2,
            officialMatches = 10, notes = "Legacy edit", version = 2L,
        })).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<TestNationalTeam[]>(teamsUrl))!.Single(item => item.Id == id).SeasonMatches.Length);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{teamsUrl}/{id}", second with
        {
            version = 3L, seasonMatches = new[] { new { seasonIndex = 1, officialMatches = 7 } },
        })).StatusCode);
        career = (await client.GetFromJsonAsync<TestCareer>(careerUrl))!;
        Assert.False(career.NationalTeamSeasonMatchesComplete);
        Assert.Null(career.NationalTeamCalculatedSeasonIndex);
        Assert.True(career.NationalTeamMilestoneValid);
        score = (await client.GetFromJsonAsync<TestScore>(scoreUrl))!;
        Assert.Equal(2, Assert.Single(score.Breakdown, item => item.Label.StartsWith("G033")).SeasonIndex);
        var teams = (await client.GetFromJsonAsync<TestNationalTeam[]>(teamsUrl))!;
        Assert.Single(teams.Single(item => item.Id == id).SeasonMatches);
    }

    [Fact]
    public async Task NationalTeamMilestoneUsesConfirmedSeasonAndRecalculatesAfterCorrections()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var rival = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Rival" };
        var challenge = new Challenge { Name = "National teams", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var ownerParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var rivalParticipant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = rival.Id };
        var career = new Career { ParticipantId = ownerParticipant.Id, TrainerName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, rival);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.AddRange(ownerParticipant, rivalParticipant);
            database.Careers.AddRange(career, new Career { ParticipantId = rivalParticipant.Id, TrainerName = "Rival" });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
        }

        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var rivalClient = await CreateAuthenticatedClient(rival.Id);
        var teamsUrl = $"/api/challenges/{challenge.Id}/national-teams";
        var first = new { teamName = "Portugal", startSeasonIndex = 0, endSeasonIndex = (int?)0, officialMatches = 12, notes = "", version = (long?)null };
        var second = new { teamName = "Spain", startSeasonIndex = 2, endSeasonIndex = (int?)3, officialMatches = 8, notes = "", version = (long?)null };
        Assert.Equal(HttpStatusCode.Created, (await ownerClient.PostAsJsonAsync(teamsUrl, first)).StatusCode);
        var secondResponse = await ownerClient.PostAsJsonAsync(teamsUrl, second);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var savedSecond = await secondResponse.Content.ReadFromJsonAsync<TestSavedTransfer>();
        var milestoneUrl = $"/api/challenges/{challenge.Id}/career/national-team-milestone";
        Assert.Equal(HttpStatusCode.Created, (await ownerClient.PostAsJsonAsync(teamsUrl, second with
        {
            teamName = "France", startSeasonIndex = 4, endSeasonIndex = (int?)4, officialMatches = 0,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 4, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 0, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 1, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0 })).StatusCode);
        Assert.True((await ownerClient.GetFromJsonAsync<TestCareer>($"/api/challenges/{challenge.Id}/career"))!.NationalTeamMilestoneValid);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PutAsJsonAsync(milestoneUrl, new { seasonIndex = 2, version = 0 })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            var scoreEvent = await database.ScoreEvents.SingleAsync(item => item.ChallengeId == challenge.Id && item.SourceKey == "automatic:G033");
            Assert.Equal(ownerParticipant.Id, scoreEvent.ParticipantId);
            Assert.Equal(2, scoreEvent.SeasonIndex);
            Assert.Equal(8, scoreEvent.Points);
        }
        var notifications = await rivalClient.GetFromJsonAsync<List<TestNotification>>("/api/me/notifications");
        Assert.Contains(notifications!, item => item.ChallengeId == challenge.Id && item.Type == "achievement.automatic");
        var claim = new { code = "G033", seasonIndex = 2, clubName = (string?)null, countryCode = (string?)null, notes = (string?)null };
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync($"{teamsUrl}/{savedSecond!.Id}", second with { officialMatches = 7, version = 1 })).StatusCode);
        Assert.False((await ownerClient.GetFromJsonAsync<TestCareer>($"/api/challenges/{challenge.Id}/career"))!.NationalTeamMilestoneValid);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            Assert.False(await database.ScoreEvents.AnyAsync(item => item.ChallengeId == challenge.Id && item.SourceKey == "automatic:G033"));
            Assert.Equal(2, (await database.Careers.SingleAsync(item => item.Id == career.Id)).NationalTeam20MatchSeasonIndex);
        }
    }

    [Fact]
    public async Task InvitationsCanBeDeclinedAndRevokedAndCannotRaceAcceptance()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var email = $"{Guid.NewGuid():N}@example.test";
        var invitee = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Invitee",
            Email = email, NormalizedEmail = email.ToUpperInvariant(),
        };
        var outsider = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Outsider", Email = "other@example.test" };
        var challenge = new Challenge { Name = "Invite test", CreatedByUserId = owner.Id };
        var admin = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, invitee, outsider);
            database.Challenges.Add(challenge);
            database.ChallengeParticipants.Add(admin);
            database.Careers.Add(new Career { ParticipantId = admin.Id, TrainerName = "Owner" });
            database.ChallengeRules.AddRange(AchievementCatalog.All.Select(definition => new ChallengeRule
            {
                ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
            }));
            await database.SaveChangesAsync();
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var inviteeClient = await CreateAuthenticatedClient(invitee.Id);
        using var outsiderClient = await CreateAuthenticatedClient(outsider.Id);
        var invitesUrl = $"/api/challenges/{challenge.Id}/invites";
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync(invitesUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(invitesUrl, new { email, expiresInDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync(invitesUrl, new { email = "not-an-email", expiresInDays = 2 })).StatusCode);

        async Task<TestInvite> CreateInvite()
        {
            var response = await ownerClient.PostAsJsonAsync(invitesUrl, new { email, expiresInDays = 2 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<TestInvite>())!;
        }

        var declined = await CreateInvite();
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderClient.PostAsync($"/api/me/invitations/{declined.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await inviteeClient.PostAsync($"/api/me/invitations/{declined.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await inviteeClient.PostAsync($"/api/me/invitations/{declined.Id}/accept", null)).StatusCode);
        var revoked = await CreateInvite();
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.PostAsync($"{invitesUrl}/{revoked.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.PostAsync($"{invitesUrl}/{revoked.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await inviteeClient.PostAsync($"/api/me/invitations/{revoked.Id}/accept", null)).StatusCode);
        var remaining = await inviteeClient.GetFromJsonAsync<List<TestInvite>>( "/api/me/invitations");
        Assert.Empty(remaining!);
        var racing = await CreateInvite();
        var results = await Task.WhenAll(
            inviteeClient.PostAsync($"/api/me/invitations/{racing.Id}/accept", null),
            ownerClient.PostAsync($"{invitesUrl}/{racing.Id}/revoke", null));
        Assert.Single(results, item => item.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        Assert.Single(results, item => item.StatusCode == HttpStatusCode.Conflict);
        var listed = await ownerClient.GetFromJsonAsync<List<TestInviteStatus>>(invitesUrl);
        Assert.Equal("Declined", Assert.Single(listed!, item => item.Id == declined.Id).Status);
        Assert.Equal("Revoked", Assert.Single(listed!, item => item.Id == revoked.Id).Status);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var stored = await verify.ChallengeInvitations.SingleAsync(item => item.Id == racing.Id);
        Assert.NotEqual(stored.RedeemedAt.HasValue, stored.RevokedAt.HasValue);
        Assert.Contains(await verify.AuditLogs.Where(item => item.EntityId == declined.Id.ToString()).Select(item => item.Action).ToListAsync(), action => action == "Declined");
        Assert.Contains(await verify.AuditLogs.Where(item => item.EntityId == revoked.Id.ToString()).Select(item => item.Action).ToListAsync(), action => action == "Revoked");
    }

    [Fact]
    public async Task NewChallengeCanSelectOfficialCatalogAchievements()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.Add(owner);
            await database.SaveChangesAsync();
        }
        using var client = await CreateAuthenticatedClient(owner.Id);
        var catalog = await client.GetFromJsonAsync<List<TestAchievement>>("/api/achievement-catalog");
        Assert.Equal(53, catalog!.Count);
        Assert.DoesNotContain(catalog, item => item.Code.StartsWith("C"));
        var invalid = await client.PostAsJsonAsync("/api/challenges", new
        {
            name = "Invalid", startSeasonYear = 2026, achievementCodes = new[] { "INVALID" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var response = await client.PostAsJsonAsync("/api/challenges", new
        {
            name = "Selected goals", startSeasonYear = 2026, achievementCodes = new[] { "G008" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TestCreatedChallenge>();
        Assert.NotNull(created);
        await ConfirmUnemployedStart(client, created.Id);
        var achievements = await client.GetFromJsonAsync<List<TestAchievement>>($"/api/challenges/{created.Id}/achievements");
        Assert.True(Assert.Single(achievements!, item => item.Code == "G008").IsEnabled);
        Assert.False(Assert.Single(achievements!, item => item.Code == "G001").IsEnabled);
        var claim = new { code = "G001", seasonIndex = 0, clubName = (string?)null, countryCode = (string?)null, notes = (string?)null, managedCompetitionMatches = 2, managedDecidingMatch = true };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/challenges/{created.Id}/achievement-claims", claim)).StatusCode);
        var claimUrl = $"/api/challenges/{created.Id}/achievement-claims";
        var competing = await Task.WhenAll(
            client.PostAsJsonAsync(claimUrl, claim with { code = "G008" }),
            client.PostAsJsonAsync(claimUrl, claim with { code = "G008" }));
        Assert.Single(competing, item => item.StatusCode == HttpStatusCode.Created);
        Assert.Single(competing, item => item.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CustomAchievementsAreChallengeScopedAndPreserveScheduledRules()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Owner" };
        var opponent = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Opponent" };
        var challenge = new Challenge { Name = "Custom test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var otherChallenge = new Challenge { Name = "Other test", CreatedByUserId = owner.Id, Status = ChallengeStatus.Active };
        var participant = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var rival = new ChallengeParticipant { ChallengeId = challenge.Id, UserId = opponent.Id };
        var otherParticipant = new ChallengeParticipant { ChallengeId = otherChallenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator };
        var career = new Career { ParticipantId = participant.Id, TrainerName = "Owner" };
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
            database.Users.AddRange(owner, opponent);
            database.Challenges.AddRange(challenge, otherChallenge);
            database.ChallengeParticipants.AddRange(participant, rival, otherParticipant);
            database.Careers.AddRange(career, new Career { ParticipantId = rival.Id }, new Career { ParticipantId = otherParticipant.Id });
            foreach (var definition in AchievementCatalog.All)
            {
                database.ChallengeRules.Add(new ChallengeRule
                {
                    ChallengeId = challenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
                });
                database.ChallengeRules.Add(new ChallengeRule
                {
                    ChallengeId = challenge.Id, AchievementCode = definition.Code, VersionNumber = 2,
                    EffectiveSeasonIndex = 3, Points = definition.Code == "G008" ? 20 : definition.DefaultPoints,
                });
                database.ChallengeRules.Add(new ChallengeRule
                {
                    ChallengeId = otherChallenge.Id, AchievementCode = definition.Code, Points = definition.DefaultPoints,
                });
            }
            database.AchievementClaims.AddRange(new[] { 0, 1, 3 }.Select(index => new AchievementClaim
            {
                CareerId = career.Id, ParticipantId = participant.Id, AchievementCode = "G008",
                SeasonIndex = index, OccurrenceKey = $"season:{index}",
            }));
            await database.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ScoreRecalculator>().RecalculateAsync(challenge.Id, CancellationToken.None);
        }
        using var ownerClient = await CreateAuthenticatedClient(owner.Id);
        using var rivalClient = await CreateAuthenticatedClient(opponent.Id);
        var rulesUrl = $"/api/challenges/{challenge.Id}/rules";
        var book = await ownerClient.GetFromJsonAsync<TestRuleBook>(rulesUrl);
        var request = new
        {
            name = "Custom objective", description = "Do something special", frequency = "Season", points = 7,
            effectiveSeasonIndex = 1, expectedVersion = 2, expectedChallengeVersion = book!.ChallengeVersion,
        };
        var customUrl = $"/api/challenges/{challenge.Id}/custom-achievements";
        Assert.Equal(HttpStatusCode.Forbidden, (await rivalClient.PostAsJsonAsync(customUrl, request)).StatusCode);
        var response = await ownerClient.PostAsJsonAsync(customUrl, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TestCustomAchievement>();
        Assert.NotNull(created);
        Assert.StartsWith("C", created.Code);
        Assert.Equal(4, created.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync(customUrl, request)).StatusCode);
        var achievements = await ownerClient.GetFromJsonAsync<List<TestAchievement>>($"/api/challenges/{challenge.Id}/achievements");
        Assert.Contains(achievements!, item => item.Code == created.Code && item.Name == request.name);
        var otherAchievements = await ownerClient.GetFromJsonAsync<List<TestAchievement>>($"/api/challenges/{otherChallenge.Id}/achievements");
        Assert.DoesNotContain(otherAchievements!, item => item.Code == created.Code);
        var claim = new { code = created.Code, seasonIndex = 1, clubName = (string?)null, countryCode = (string?)null, notes = (string?)null };
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim with { seasonIndex = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.PostAsJsonAsync($"/api/challenges/{otherChallenge.Id}/achievement-claims", claim)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ownerClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await rivalClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim)).StatusCode);
        var breakdown = await ownerClient.GetFromJsonAsync<TestScore>($"/api/challenges/{challenge.Id}/scores");
        Assert.Contains(breakdown!.Breakdown, item => item.Label.Contains("Custom objective") && item.Points == 7 && item.Status == "Confirmed");
        var currentBook = await ownerClient.GetFromJsonAsync<TestRuleBook>(rulesUrl);
        var ruleChange = new
        {
            code = "G009", points = 11, isEnabled = true, policy = "Retroactive", effectiveSeasonIndex = 0,
            expectedVersion = 4, expectedChallengeVersion = currentBook!.ChallengeVersion, confirm = true, reason = "Test",
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"{rulesUrl}/preview", ruleChange)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(rulesUrl, ruleChange)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ownerClient.PostAsJsonAsync($"/api/challenges/{challenge.Id}/achievement-claims", claim with { seasonIndex = 0 })).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ChallengeDbContext>();
        var events = await verify.ScoreEvents.Where(item => item.ChallengeId == challenge.Id
            && item.ParticipantId == participant.Id && item.SourceType == "Achievement").OrderBy(item => item.SeasonIndex).ToListAsync();
        Assert.Equal(new[] { 10, 10, 7, 20 }, events.OrderBy(item => item.SeasonIndex).ThenByDescending(item => item.Points).Select(item => item.Points));
        Assert.Single(await verify.AchievementDefinitions.Where(item => item.Code == created.Code && item.ChallengeId == challenge.Id).ToListAsync());
    }

    private static async Task ConfirmUnemployedStart(HttpClient client, Guid challengeId)
    {
        var response = await client.PutAsJsonAsync($"/api/challenges/{challengeId}/career/start",
            new { status = "Unemployed", stintId = (Guid?)null, version = 0L });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpClient> CreateAuthenticatedClient(Guid userId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());
        var token = await client.GetFromJsonAsync<CsrfResponse>("/api/auth/csrf");
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.RequestToken);
        return client;
    }

    private sealed record CsrfResponse(string RequestToken);
    private sealed record TestNotification(string Type, Guid? ChallengeId);
    private sealed record TestGeography(TestGeographyCountry[] Countries, TestGeographyContinent[] Continents);
    private sealed record TestGeographyCountry(string Code, string Name, string ContinentCode);
    private sealed record TestGeographyContinent(string Code, string Name);
    private sealed record TestCareer(List<TestStint> Clubs, bool NationalTeamMilestoneValid = false,
        bool ThreeContinentsMilestoneValid = false, int? NationalTeamCalculatedSeasonIndex = null,
        bool NationalTeamSeasonMatchesComplete = false, int? ThreeContinentsCalculatedSeasonIndex = null);
    private sealed record TestNationalTeam(Guid Id, TestNationalSeasonMatch[] SeasonMatches);
    private sealed record TestNationalSeasonMatch(int SeasonIndex, int OfficialMatches);
    private sealed record TestStint(int OfficialMatches, int? EligibleSeasonIndex, Guid Id = default,
        int? ClubEligibleSeasonIndex = null, long ClubOfficialMatches = 0,
        int? CountryEligibleSeasonIndex = null, int? CountryEligibleSeason = null, long CountryOfficialMatches = 0,
        bool ClubMilestoneValid = false, bool CountryMilestoneValid = false, Guid? ClubId = null, long Version = 0);
    private sealed record TestCatalogClub(Guid Id, string Name, string CountryCode, string CountryName, string IdentityLabel);
    private sealed record TestAchievementClaims(string Code, TestClaimClub[] Claims);
    private sealed record TestClaimClub(Guid Id, Guid? ClubId, Guid? StartingCareerClubId, Guid? FirstContinentalTitleClubId);
    private sealed record TestSeason(string Status, int LeaguePoints, long Version);
    private sealed record TestDetailedSeason(int SeasonIndex, bool UsesClubBreakdown, int LeagueMatches, int LeaguePoints, List<TestClubSeason> ClubSeasons);
    private sealed record TestClubSeason(Guid CareerStintId, int OfficialMatches);
    private sealed record TestCreatedClaim(Guid Id);
    private sealed record TestCompetition(Guid Id, string Name, string Tier, long Version);
    private sealed record TestSavedTransfer(Guid Id, long Version);
    private sealed record TestScore(int[] CurrencyMismatchSeasons, int? CommonSeasonIndex, TestScoreTotal[] Totals, TestScoreEvent[] Breakdown, bool VictoryPending, int? PendingVictorySeasonIndex);
    private sealed record TestScoreTotal(int ConfirmedPoints, int PendingPoints, int? CommonSeasonPoints);
    private sealed record TestScoreEvent(string SourceType, string Status, string Label, string? Explanation, int Points, int SeasonIndex);
    private sealed record TestChallengeCard(Guid Id, string Status, string? StatusBeforeArchive, string Role, string? CurrentSeason);
    private sealed record TestRuleImpact(TestRuleParticipantImpact[] Impact);
    private sealed record TestRuleParticipantImpact(Guid ParticipantId, int BeforeConfirmed, int AfterConfirmed);
    private sealed record TestRuleBook(long ChallengeVersion, int Version = 0, TestRule[]? Rules = null);
    private sealed record TestRule(string AchievementCode, int Points, bool IsEnabled);
    private sealed record TestCustomAchievement(string Code, int Version);
    private sealed record TestAchievement(string Code, string Name, bool IsEnabled);
    private sealed record TestCreatedChallenge(Guid Id);
    private sealed record TestInvite(Guid Id);
    private sealed record TestInviteStatus(Guid Id, string Status);
    private sealed record TestProfile(string DisplayName, string? AvatarUrl, bool HasCompletedOnboarding);
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Guid.TryParse(Request.Headers["X-Test-User"].FirstOrDefault(), out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class TestProxyAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["X-Test-Proxy"].FirstOrDefault(), out var address))
                context.Connection.RemoteIpAddress = address;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Test-Observed-IP"] = context.Connection.RemoteIpAddress?.ToString();
                context.Response.Headers["X-Test-Observed-Host"] = context.Request.Host.Host;
                return Task.CompletedTask;
            });
            await nextMiddleware();
        });
        next(app);
    };
}
