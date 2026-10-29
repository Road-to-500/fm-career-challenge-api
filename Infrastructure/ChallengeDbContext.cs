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
    public DbSet<ChallengeParticipant> ChallengeParticipants => Set<ChallengeParticipant>();
    public DbSet<Career> Careers => Set<Career>();
    public DbSet<CareerStint> CareerStints => Set<CareerStint>();
    public DbSet<CareerSeason> CareerSeasons => Set<CareerSeason>();
    public DbSet<AchievementDefinition> AchievementDefinitions => Set<AchievementDefinition>();
    public DbSet<ChallengeRule> ChallengeRules => Set<ChallengeRule>();
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
            entity.HasIndex(career => career.ParticipantId).IsUnique();
        });

        builder.Entity<CareerStint>(entity =>
        {
            entity.Property(stint => stint.ClubName).HasMaxLength(120).IsRequired();
            entity.Property(stint => stint.CountryCode).HasMaxLength(8).IsRequired();
            entity.Property(stint => stint.CountryName).HasMaxLength(80).IsRequired();
            entity.HasIndex(stint => new { stint.CareerId, stint.ClubName });
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

        builder.Entity<AchievementDefinition>(entity =>
        {
            entity.HasKey(definition => definition.Code);
            entity.Property(definition => definition.Code).HasMaxLength(4);
            entity.Property(definition => definition.Name).HasMaxLength(120).IsRequired();
            entity.Property(definition => definition.Category).HasMaxLength(32).IsRequired();
            entity.Property(definition => definition.Description).HasMaxLength(500).IsRequired();
            entity.Property(definition => definition.Frequency).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<ChallengeRule>(entity =>
        {
            entity.HasKey(rule => new { rule.ChallengeId, rule.AchievementCode });
            entity.HasIndex(rule => new { rule.ChallengeId, rule.VersionNumber });
        });

        builder.Entity<AchievementClaim>(entity =>
        {
            entity.Property(claim => claim.AchievementCode).HasMaxLength(4).IsRequired();
            entity.Property(claim => claim.OccurrenceKey).HasMaxLength(180).IsRequired();
            entity.Property(claim => claim.ClubName).HasMaxLength(120);
            entity.Property(claim => claim.CountryCode).HasMaxLength(8);
            entity.Property(claim => claim.Notes).HasMaxLength(1000);
            entity.Property(claim => claim.Version).IsConcurrencyToken();
            entity.HasIndex(claim => new { claim.ParticipantId, claim.SeasonIndex });
            entity.HasIndex(claim => new { claim.ParticipantId, claim.AchievementCode, claim.OccurrenceKey }).IsUnique();
        });

        builder.Entity<Transfer>(entity =>
        {
            entity.Property(transfer => transfer.ClubName).HasMaxLength(120).IsRequired();
            entity.Property(transfer => transfer.PlayerName).HasMaxLength(120).IsRequired();
            entity.Property(transfer => transfer.Direction).HasMaxLength(16).IsRequired();
            entity.Property(transfer => transfer.FixedAmount).HasPrecision(18, 2);
            entity.Property(transfer => transfer.Currency).HasMaxLength(3).IsRequired();
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
