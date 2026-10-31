using System.Data;
using FmCareerChallenge.Api.Domain;
using FmCareerChallenge.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FmCareerChallenge.IntegrationTests;

public sealed class DatabaseTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("FMCC_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("fmcc_test")
                .WithUsername("fmcc_test")
                .WithPassword(Guid.NewGuid().ToString("N"))
                .Build();
            await container.StartAsync();
            connectionString = container.GetConnectionString();
        }
        else
        {
            connectionString = configuredConnection;
        }

        await using var database = CreateContext();
        await DatabaseMigrations.ApplyAsync(database);
    }

    public async Task DisposeAsync()
    {
        if (container is not null) await container.DisposeAsync();
    }

    [Fact]
    public async Task MigrationsCreateTheCompleteAchievementCatalog()
    {
        await using var database = CreateContext();
        Assert.Equal(53, await database.AchievementDefinitions.CountAsync(item => item.ChallengeId == null));
        Assert.True(await database.AchievementDefinitions.AnyAsync(item => item.ChallengeId == null && item.Code == "G001"));
        Assert.True(await database.AchievementDefinitions.AnyAsync(item => item.ChallengeId == null && item.Code == "G053"));
        var stored = await database.AchievementDefinitions.Where(item => item.ChallengeId == null)
            .ToDictionaryAsync(item => item.Code, item => item.Description);
        Assert.All(AchievementCatalog.All, item =>
        {
            Assert.Equal(item.Description, stored[item.Code]);
            Assert.InRange(item.Description.Length, 1, 500);
        });
    }

    [Fact]
    public async Task MigrationsCreateCountryAndContinentCatalogs()
    {
        await using var database = CreateContext();
        Assert.Equal(GeographyCatalog.Countries.Length, await database.Countries.CountAsync());
        Assert.Equal(7, await database.Continents.CountAsync());
        Assert.Equal("EU", (await database.Countries.SingleAsync(item => item.Code == "PT")).ContinentCode);
        Assert.Equal("SA", (await database.Countries.SingleAsync(item => item.Code == "BR")).ContinentCode);
        Assert.Equal("EU", (await database.Countries.SingleAsync(item => item.Code == "GB-ENG")).ContinentCode);
    }

    [Fact]
    public async Task MigrationWaitsForAnotherInitializer()
    {
        await using var first = CreateContext();
        await first.Database.OpenConnectionAsync();
        await first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(1179476803)");
        var released = false;
        try
        {
            await using var second = CreateContext();
            var migration = DatabaseMigrations.ApplyAsync(second);
            await Task.Delay(250);
            Assert.False(migration.IsCompleted);
            await first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(1179476803)");
            released = true;
            await migration.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (!released) await first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(1179476803)");
            await first.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task SerializableJoinTransactionsCannotBothFillTheLastSlot()
    {
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString() };
        var firstUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString() };
        var secondUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString() };
        var challenge = new Challenge { Name = "Concurrency test", CreatedByUserId = owner.Id };
        await using (var setup = CreateContext())
        {
            setup.Users.AddRange(owner, firstUser, secondUser);
            setup.Challenges.Add(challenge);
            setup.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id, UserId = owner.Id, Role = ParticipantRole.Administrator,
            });
            await setup.SaveChangesAsync();
        }

        await using var first = CreateContext();
        await using var second = CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        Assert.Equal(1, await first.ChallengeParticipants.CountAsync(item => item.ChallengeId == challenge.Id));
        Assert.Equal(1, await second.ChallengeParticipants.CountAsync(item => item.ChallengeId == challenge.Id));

        first.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserId = firstUser.Id });
        second.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserId = secondUser.Id });
        await first.SaveChangesAsync();
        await firstTransaction.CommitAsync();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await second.SaveChangesAsync();
            await secondTransaction.CommitAsync();
        });
        Assert.NotNull(exception);
        var current = exception;
        while (current is not null && current is not PostgresException) current = current.InnerException;
        var postgres = current as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres.SqlState);

        await using var verification = CreateContext();
        Assert.Equal(2, await verification.ChallengeParticipants.CountAsync(item => item.ChallengeId == challenge.Id));
    }

    private ChallengeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ChallengeDbContext>().UseNpgsql(connectionString).Options);
}
