using Microsoft.AspNetCore.Identity;

namespace FmCareerChallenge.Api.Domain;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public enum ChallengeStatus
{
    Draft,
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

public sealed class Continent
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class Country
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContinentCode { get; set; } = string.Empty;
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
    public ChallengeStatus? StatusBeforeArchive { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? WinningParticipantId { get; set; }
    public int? WinningSeasonIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsCorrectionMode { get; set; }
    public string? FinalScoreSnapshot { get; set; }
    public int? FinalRulesVersion { get; set; }
    public long Version { get; set; }
}

public sealed class ChallengeParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public Guid UserId { get; set; }
    public ParticipantRole Role { get; set; } = ParticipantRole.Participant;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Career
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParticipantId { get; set; }
    public string TrainerName { get; set; } = string.Empty;
    public string? CurrentClubName { get; set; }
    public string? CurrentCountryCode { get; set; }
    public string? StartingStatus { get; set; }
    public Guid? StartingStintId { get; set; }
    public long StartingStatusVersion { get; set; }
    public bool RequiresStartingStatus { get; set; }
    public int? NationalTeam20MatchSeasonIndex { get; set; }
    public long NationalTeamMilestoneVersion { get; set; }
    public int? ThreeContinentsSeasonIndex { get; set; }
    public long ThreeContinentsMilestoneVersion { get; set; }
}

public sealed class CareerStint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public Guid? ClubId { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string CountryName { get; set; } = string.Empty;
    public int StartSeasonIndex { get; set; }
    public int? EndSeasonIndex { get; set; }
    public int InitialDivisionLevel { get; set; }
    public int OfficialMatches { get; set; }
    public int? EligibleSeasonIndex { get; set; }
    public int? CountryEligibleSeasonIndex { get; set; }
    public long Version { get; set; }
}

public sealed class Club
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string IdentityLabel { get; set; } = string.Empty;
    public string NormalizedIdentityLabel { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
}

public sealed class NationalTeamStint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public int StartSeasonIndex { get; set; }
    public int? EndSeasonIndex { get; set; }
    public int OfficialMatches { get; set; }
    public string? Notes { get; set; }
    public long Version { get; set; }
    public List<NationalTeamSeasonMatch> SeasonMatches { get; set; } = [];
}

public sealed class NationalTeamSeasonMatch
{
    public Guid NationalTeamStintId { get; set; }
    public int SeasonIndex { get; set; }
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
    public bool UsesClubBreakdown { get; set; }
    public decimal LargestOutgoingTransfer { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Notes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ClubSeasonStatistics
{
    public Guid CareerSeasonId { get; set; }
    public Guid CareerStintId { get; set; }
    public int DivisionLevel { get; set; }
    public int OfficialMatches { get; set; }
    public int LeagueMatches { get; set; }
    public int LeagueWins { get; set; }
    public int LeagueDraws { get; set; }
    public int LeagueLosses { get; set; }
    public int LeaguePoints { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int? LeaguePosition { get; set; }
}

public sealed class AchievementDefinition
{
    public string Code { get; set; } = string.Empty;
    public Guid? ChallengeId { get; set; }
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
    public string? NameOverride { get; set; }
    public string? DescriptionOverride { get; set; }
    public string? CategoryOverride { get; set; }
    public AchievementFrequency? FrequencyOverride { get; set; }
    public int? MinimumCupMatchesOverride { get; set; }
    public int VersionNumber { get; set; } = 1;
    public int EffectiveSeasonIndex { get; set; }
}

public sealed class ChallengeCompetition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class AchievementClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CareerId { get; set; }
    public Guid ParticipantId { get; set; }
    public string AchievementCode { get; set; } = string.Empty;
    public string OccurrenceKey { get; set; } = string.Empty;
    public int SeasonIndex { get; set; }
    public Guid? ClubId { get; set; }
    public string? ClubName { get; set; }
    public string? CountryCode { get; set; }
    public string? Notes { get; set; }
    public int? ManagedCompetitionMatches { get; set; }
    public int? TotalCompetitionMatches { get; set; }
    public bool? InChargeAtCompetitionEnd { get; set; }
    public bool? ManagedDecidingMatch { get; set; }
    public bool? FirstSeasonAfterPromotion { get; set; }
    public bool? AvoidedRelegation { get; set; }
    public int? CompetitionLeaguePosition { get; set; }
    public bool? WonFirstDivisionTitle { get; set; }
    public bool? WonMajorNationalCup { get; set; }
    public bool? WonLeagueTitleUnbeaten { get; set; }
    public bool? CupWonInLowerDivision { get; set; }
    public int? DomesticCupMatchesManaged { get; set; }
    public int? PromotionDestinationLevel { get; set; }
    public bool? PromotionWithoutTitle { get; set; }
    public int? PromotionStreakStartSeason { get; set; }
    public bool? ReachedContinentalMainPhase { get; set; }
    public bool? PriorContinentalMainPhase { get; set; }
    public bool? FirstTopTierMainPhase { get; set; }
    public string? FirstContinentalTitleClub { get; set; }
    public string? FirstContinentalTitleCountry { get; set; }
    public Guid? FirstContinentalTitleClubId { get; set; }
    public int? FirstContinentalTitleSeason { get; set; }
    public bool? WonContinentalTitlesWithBothClubs { get; set; }
    public string? StartingCareerClub { get; set; }
    public string? StartingCareerCountry { get; set; }
    public Guid? StartingCareerClubId { get; set; }
    public bool? FirstDivisionExperience { get; set; }
    public bool? HigherDivisionClubMove { get; set; }
    public int? PreviousBestDivisionLevel { get; set; }
    public int? NewDivisionLevel { get; set; }
    public bool? FirstMajorClubTrophy { get; set; }
    public bool? TrophyNotSuperCup { get; set; }
    public bool? WonTenMajorTrophies { get; set; }
    public int? ThirdDivisionStartSeason { get; set; }
    public bool? StayedWithClubFromThirdToFirst { get; set; }
    public bool? OfficialManagerOfYear { get; set; }
    public string? CompetitionResultKind { get; set; }
    public string? CompetitionResultStage { get; set; }
    public Guid? CompetitionId { get; set; }
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
    public string CounterpartyName { get; set; } = string.Empty;
    public string Direction { get; set; } = "Outgoing";
    public decimal FixedAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; }
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
    public DateTimeOffset? DeclinedAt { get; set; }
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
