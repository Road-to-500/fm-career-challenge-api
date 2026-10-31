using Microsoft.EntityFrameworkCore;

namespace FmCareerChallenge.Api.Infrastructure;

public static class DatabaseMigrations
{
    public static async Task ApplyAsync(ChallengeDbContext database, CancellationToken cancellationToken = default)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(1179476803)", cancellationToken);
            try
            {
                await database.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(1179476803)", CancellationToken.None);
            }
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
