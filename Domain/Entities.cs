using Microsoft.AspNetCore.Identity;

namespace FmCareerChallenge.Api.Domain;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public enum ChallengeStatus
{
    PendingPlayers,
    Active,
    Completed,
    Archived
}

public enum ParticipantRole
{
    Administrator,
    Participant
}

public enum SeasonStatus
{
    InProgress,
    Completed
}

public enum AchievementFrequency
{
    Once,
    Season,
    PerClub,
    PerCountry,
    ClubSeason
}

public sealed class Challenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Mode { get; set; } = "RaceTo500";
    public int TargetPoints { get; set; } = 500;
    public int StartSeasonYear { get; set; } = 2026;
    public ChallengeStatus Status { get; set; } = ChallengeStatus.PendingPlayers;
    public Guid CreatedByUserId { get; set; }
    public Guid? WinningParticipantId { get; set; }
    public int? WinningSeasonIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ChallengeParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public Guid UserId { get; set; }
    public ParticipantRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Career
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParticipantId { get; set; }
    public string TrainerName { get; set; } = string.Empty;
    public string? CurrentClubName { get; set; }
    public string? CurrentCountryCode { get; set; }
}

public sealed class CareerStint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public int StartSeasonIndex { get; set; }
    public int? EndSeasonIndex { get; set; }
    public int InitialDivisionLevel { get; set; }
    public int OfficialMatches { get; set; }
}

public sealed class CareerSeason
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public int SeasonIndex { get; set; }
    public int StartYear { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public SeasonStatus Status { get; set; } = SeasonStatus.InProgress;
    public int LeagueMatches { get; set; }
    public int LeagueWins { get; set; }
    public int LeagueDraws { get; set; }
    public int LeagueLosses { get; set; }
    public int LeaguePoints { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int? LeaguePosition { get; set; }
    public decimal LargestOutgoingTransfer { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Notes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}

public sealed class AchievementDefinition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AchievementFrequency Frequency { get; set; }
    public int DefaultPoints { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class ChallengeRule
{
    public Guid ChallengeId { get; set; }
    public string AchievementCode { get; set; } = string.Empty;
    public int Points { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int VersionNumber { get; set; } = 1;
}

public sealed class AchievementClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public Guid ParticipantId { get; set; }
    public string AchievementCode { get; set; } = string.Empty;
    public string OccurrenceKey { get; set; } = string.Empty;
    public int SeasonIndex { get; set; }
    public string? ClubName { get; set; }
    public string? CountryCode { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; }
}

public sealed class Transfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public int SeasonIndex { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string Direction { get; set; } = "Outgoing";
    public decimal FixedAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ScoreEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public Guid ParticipantId { get; set; }
    public int SeasonIndex { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public int Points { get; set; }
    public string Status { get; set; } = "Confirmed";
    public int RuleVersion { get; set; } = 1;
}

public sealed class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ChallengeId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RelatedEntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class ChallengeInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public Guid InvitedByUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string? InviteeEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ActivityEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public Guid ParticipantId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ClubName { get; set; }
    public int? SeasonIndex { get; set; }
    public int? Points { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; }
    public Guid? ChallengeId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? OldData { get; set; }
    public string? NewData { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
