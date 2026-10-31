using FmCareerChallenge.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FmCareerChallenge.Api.Infrastructure;

public sealed class ChallengeDbContext(
    DbContextOptions<ChallengeDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Continent> Continents => Set<Continent>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ChallengeParticipant> ChallengeParticipants => Set<ChallengeParticipant>();
    public DbSet<Career> Careers => Set<Career>();
    public DbSet<CareerStint> CareerStints => Set<CareerStint>();
    public DbSet<NationalTeamStint> NationalTeamStints => Set<NationalTeamStint>();
    public DbSet<NationalTeamSeasonMatch> NationalTeamSeasonMatches => Set<NationalTeamSeasonMatch>();
    public DbSet<CareerSeason> CareerSeasons => Set<CareerSeason>();
    public DbSet<ClubSeasonStatistics> ClubSeasonStatistics => Set<ClubSeasonStatistics>();
    public DbSet<AchievementDefinition> AchievementDefinitions => Set<AchievementDefinition>();
    public DbSet<ChallengeRule> ChallengeRules => Set<ChallengeRule>();
    public DbSet<ChallengeCompetition> ChallengeCompetitions => Set<ChallengeCompetition>();
    public DbSet<AchievementClaim> AchievementClaims => Set<AchievementClaim>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<ScoreEvent> ScoreEvents => Set<ScoreEvent>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<ChallengeInvitation> ChallengeInvitations => Set<ChallengeInvitation>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Continent>(entity =>
        {
            entity.HasKey(item => item.Code);
            entity.Property(item => item.Code).HasMaxLength(2);
            entity.Property(item => item.Name).HasMaxLength(80).IsRequired();
            entity.HasData(GeographyCatalog.Continents);
        });
        builder.Entity<Country>(entity =>
        {
            entity.HasKey(item => item.Code);
            entity.Property(item => item.Code).HasMaxLength(8);
            entity.Property(item => item.Name).HasMaxLength(80).IsRequired();
            entity.Property(item => item.ContinentCode).HasMaxLength(2).IsRequired();
            entity.HasOne<Continent>().WithMany().HasForeignKey(item => item.ContinentCode).OnDelete(DeleteBehavior.Restrict);
            entity.HasData(GeographyCatalog.Countries);
        });
        builder.Entity<Club>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.NormalizedName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.IdentityLabel).HasMaxLength(80).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(item => item.NormalizedIdentityLabel).HasMaxLength(80).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(item => item.CountryCode).HasMaxLength(8).IsRequired();
            entity.HasIndex(item => new { item.CountryCode, item.NormalizedName, item.NormalizedIdentityLabel }).IsUnique();
            entity.HasOne<Country>().WithMany().HasForeignKey(item => item.CountryCode).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(80);
            entity.Property(user => user.AvatarUrl).HasMaxLength(2048);
        });

        builder.Entity<Challenge>(entity =>
        {
            entity.Property(challenge => challenge.Name).HasMaxLength(80).IsRequired();
            entity.Property(challenge => challenge.Description).HasMaxLength(500);
            entity.Property(challenge => challenge.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(challenge => challenge.StatusBeforeArchive).HasConversion<string>().HasMaxLength(24);
            entity.Property(challenge => challenge.Version).IsConcurrencyToken();
            entity.HasIndex(challenge => challenge.CreatedByUserId);
        });

        builder.Entity<ChallengeParticipant>(entity =>
        {
            entity.Property(participant => participant.Role).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(participant => new { participant.ChallengeId, participant.UserId }).IsUnique();
            entity.HasIndex(participant => participant.UserId);
        });

        builder.Entity<Career>(entity =>
        {
            entity.Property(career => career.TrainerName).HasMaxLength(80).IsRequired();
            entity.Property(career => career.CurrentClubName).HasMaxLength(120);
            entity.Property(career => career.StartingStatus).HasMaxLength(16);
            entity.Property(career => career.StartingStatusVersion).IsConcurrencyToken();
            entity.HasOne<CareerStint>().WithMany().HasForeignKey(career => career.StartingStintId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(career => career.NationalTeamMilestoneVersion).IsConcurrencyToken();
            entity.Property(career => career.ThreeContinentsMilestoneVersion).IsConcurrencyToken();
            entity.HasIndex(career => career.ParticipantId).IsUnique();
        });

        builder.Entity<CareerStint>(entity =>
        {
            entity.Property(stint => stint.ClubName).HasMaxLength(120).IsRequired();
            entity.Property(stint => stint.CountryCode).HasMaxLength(8).IsRequired();
            entity.Property(stint => stint.CountryName).HasMaxLength(80).IsRequired();
            entity.Property(stint => stint.Version).IsConcurrencyToken();
            entity.HasIndex(stint => new { stint.CareerId, stint.ClubName });
            entity.HasOne<Club>().WithMany().HasForeignKey(stint => stint.ClubId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CareerSeason>(entity =>
        {
            entity.Property(season => season.DisplayName).HasMaxLength(16).IsRequired();
            entity.Property(season => season.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(season => season.LargestOutgoingTransfer).HasPrecision(18, 2);
            entity.Property(season => season.Currency).HasMaxLength(3);
            entity.Property(season => season.Version).IsConcurrencyToken();
            entity.HasIndex(season => new { season.CareerId, season.SeasonIndex }).IsUnique();
        });

        builder.Entity<ClubSeasonStatistics>(entity =>
        {
            entity.HasKey(item => new { item.CareerSeasonId, item.CareerStintId });
            entity.HasOne<CareerSeason>().WithMany().HasForeignKey(item => item.CareerSeasonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CareerStint>().WithMany().HasForeignKey(item => item.CareerStintId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<NationalTeamStint>(entity =>
        {
            entity.Property(stint => stint.TeamName).HasMaxLength(120).IsRequired();
            entity.Property(stint => stint.CountryCode).HasMaxLength(8);
            entity.Property(stint => stint.Notes).HasMaxLength(1000);
            entity.Property(stint => stint.Version).IsConcurrencyToken();
            entity.HasIndex(stint => new { stint.CareerId, stint.StartSeasonIndex });
        });
        builder.Entity<NationalTeamSeasonMatch>(entity =>
        {
            entity.HasKey(item => new { item.NationalTeamStintId, item.SeasonIndex });
            entity.HasOne<NationalTeamStint>().WithMany(stint => stint.SeasonMatches)
                .HasForeignKey(item => item.NationalTeamStintId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AchievementDefinition>(entity =>
        {
            entity.HasKey(definition => definition.Code);
            entity.Property(definition => definition.Code).HasMaxLength(33);
            entity.Property(definition => definition.Name).HasMaxLength(120).IsRequired();
            entity.Property(definition => definition.Category).HasMaxLength(32).IsRequired();
            entity.Property(definition => definition.Description).HasMaxLength(500).IsRequired();
            entity.Property(definition => definition.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Challenge>().WithMany().HasForeignKey(definition => definition.ChallengeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChallengeRule>(entity =>
        {
            entity.Property(rule => rule.FrequencyOverride).HasConversion<string>().HasMaxLength(20);
            entity.HasKey(rule => new { rule.ChallengeId, rule.VersionNumber, rule.AchievementCode });
            entity.HasIndex(rule => new { rule.ChallengeId, rule.VersionNumber });
        });

        builder.Entity<ChallengeCompetition>(entity =>
        {
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.NormalizedName).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Tier).HasMaxLength(24).IsRequired();
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => new { item.ChallengeId, item.NormalizedName }).IsUnique();
            entity.HasOne<Challenge>().WithMany().HasForeignKey(item => item.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AchievementClaim>(entity =>
        {
            entity.Property(claim => claim.AchievementCode).HasMaxLength(33).IsRequired();
            entity.Property(claim => claim.OccurrenceKey).HasMaxLength(1000).IsRequired();
            entity.Property(claim => claim.ClubName).HasMaxLength(120);
            entity.Property(claim => claim.CountryCode).HasMaxLength(8);
            entity.Property(claim => claim.Notes).HasMaxLength(1000);
            entity.Property(claim => claim.CompetitionResultKind).HasMaxLength(32);
            entity.Property(claim => claim.CompetitionResultStage).HasMaxLength(32);
            entity.Property(claim => claim.Version).IsConcurrencyToken();
            entity.HasIndex(claim => new { claim.ParticipantId, claim.SeasonIndex });
            entity.HasIndex(claim => new { claim.ParticipantId, claim.AchievementCode, claim.OccurrenceKey }).IsUnique();
            entity.HasOne<ChallengeCompetition>().WithMany().HasForeignKey(claim => claim.CompetitionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Club>().WithMany().HasForeignKey(claim => claim.ClubId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Club>().WithMany().HasForeignKey(claim => claim.StartingCareerClubId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Club>().WithMany().HasForeignKey(claim => claim.FirstContinentalTitleClubId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Transfer>(entity =>
        {
            entity.Property(transfer => transfer.ClubName).HasMaxLength(120).IsRequired();
            entity.Property(transfer => transfer.PlayerName).HasMaxLength(120).IsRequired();
            entity.Property(transfer => transfer.CounterpartyName).HasMaxLength(120).IsRequired();
            entity.Property(transfer => transfer.Direction).HasMaxLength(16).IsRequired();
            entity.Property(transfer => transfer.FixedAmount).HasPrecision(18, 2);
            entity.Property(transfer => transfer.Currency).HasMaxLength(3).IsRequired();
            entity.Property(transfer => transfer.Notes).HasMaxLength(1000);
            entity.Property(transfer => transfer.Version).IsConcurrencyToken();
            entity.HasIndex(transfer => new { transfer.CareerId, transfer.SeasonIndex });
        });

        builder.Entity<ScoreEvent>(entity =>
        {
            entity.Property(score => score.SourceType).HasMaxLength(32).IsRequired();
            entity.Property(score => score.SourceKey).HasMaxLength(100).IsRequired();
            entity.Property(score => score.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(score => new { score.ChallengeId, score.ParticipantId, score.SourceType, score.SourceKey }).IsUnique();
        });

        builder.Entity<UserNotification>(entity =>
        {
            entity.Property(notification => notification.Type).HasMaxLength(40).IsRequired();
            entity.Property(notification => notification.Title).HasMaxLength(120).IsRequired();
            entity.Property(notification => notification.Message).HasMaxLength(500).IsRequired();
            entity.HasIndex(notification => new { notification.UserId, notification.CreatedAt });
        });

        builder.Entity<ChallengeInvitation>(entity =>
        {
            entity.Property(invitation => invitation.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(invitation => invitation.InviteeEmail).HasMaxLength(256);
            entity.HasIndex(invitation => invitation.TokenHash).IsUnique();
            entity.HasIndex(invitation => new { invitation.ChallengeId, invitation.InviteeEmail });
        });

        builder.Entity<ActivityEvent>(entity =>
        {
            entity.Property(activity => activity.Type).HasMaxLength(40).IsRequired();
            entity.Property(activity => activity.Message).HasMaxLength(500).IsRequired();
            entity.Property(activity => activity.ClubName).HasMaxLength(120);
            entity.HasIndex(activity => new { activity.ChallengeId, activity.CreatedAt });
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.Property(log => log.EntityType).HasMaxLength(80).IsRequired();
            entity.Property(log => log.EntityId).HasMaxLength(100).IsRequired();
            entity.Property(log => log.Action).HasMaxLength(40).IsRequired();
            entity.HasIndex(log => new { log.ChallengeId, log.CreatedAt });
        });

        builder.Entity<Challenge>().HasMany<ChallengeParticipant>()
            .WithOne().HasForeignKey(participant => participant.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ChallengeParticipant>().HasOne<ApplicationUser>()
            .WithMany().HasForeignKey(participant => participant.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ChallengeParticipant>().HasMany<Career>()
            .WithOne().HasForeignKey(career => career.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Career>().HasMany<CareerStint>()
            .WithOne().HasForeignKey(stint => stint.CareerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Career>().HasMany<CareerSeason>()
            .WithOne().HasForeignKey(season => season.CareerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Career>().HasMany<NationalTeamStint>()
            .WithOne().HasForeignKey(stint => stint.CareerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Career>().HasMany<AchievementClaim>()
            .WithOne().HasForeignKey(claim => claim.CareerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Career>().HasMany<Transfer>()
            .WithOne().HasForeignKey(transfer => transfer.CareerId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Challenge>().HasMany<ChallengeRule>()
            .WithOne().HasForeignKey(rule => rule.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Challenge>().HasMany<ScoreEvent>()
            .WithOne().HasForeignKey(score => score.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Challenge>().HasMany<ChallengeInvitation>()
            .WithOne().HasForeignKey(invitation => invitation.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Challenge>().HasMany<ActivityEvent>()
            .WithOne().HasForeignKey(activity => activity.ChallengeId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Challenge>().HasMany<AuditLog>()
            .WithOne().HasForeignKey(log => log.ChallengeId).OnDelete(DeleteBehavior.SetNull);

        builder.Entity<AchievementDefinition>().HasData(AchievementCatalog.All);
    }
}
