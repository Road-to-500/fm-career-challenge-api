using FmCareerChallenge.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FmCareerChallenge.Api.Domain;

public sealed class ScoreRecalculator(ChallengeDbContext database)
{
    public async Task<RaceScoringResult> RecalculateAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var challenge = await database.Challenges.SingleOrDefaultAsync(
            item => item.Id == challengeId, cancellationToken)
            ?? throw new InvalidOperationException("The challenge does not exist.");

        var participants = await database.ChallengeParticipants
            .Where(item => item.ChallengeId == challengeId)
            .OrderBy(item => item.JoinedAt)
            .ToListAsync(cancellationToken);
        var participantIds = participants.Select(item => item.Id).ToArray();
        var careers = await database.Careers
            .Where(item => participantIds.Contains(item.ParticipantId))
            .ToListAsync(cancellationToken);
        var careerIds = careers.Select(item => item.Id).ToArray();

        var claims = await database.AchievementClaims
            .Where(item => participantIds.Contains(item.ParticipantId))
            .ToListAsync(cancellationToken);
        var seasons = await database.CareerSeasons
            .Where(item => careerIds.Contains(item.CareerId))
            .ToListAsync(cancellationToken);
        var stints = await database.CareerStints
            .Where(item => careerIds.Contains(item.CareerId))
            .ToListAsync(cancellationToken);
        var rules = await database.ChallengeRules
            .Where(item => item.ChallengeId == challengeId && item.VersionNumber == 1)
            .ToDictionaryAsync(item => item.AchievementCode, item => item.IsEnabled ? item.Points : 0, cancellationToken);

        var careerParticipantIds = careers.ToDictionary(item => item.Id, item => item.ParticipantId);
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            challengeId,
            challenge.TargetPoints,
            participantIds,
            claims.Select(item => new ClaimInput(
                item.Id,
                item.ParticipantId,
                item.AchievementCode,
                item.SeasonIndex,
                item.ClubName,
                item.CountryCode,
                item.CreatedAt)).ToArray(),
            seasons.Select(item => new SeasonInput(
                careerParticipantIds[item.CareerId],
                item.SeasonIndex,
                item.Status == SeasonStatus.Completed,
                item.LeagueMatches,
                item.LeaguePoints,
                item.GoalsFor,
                item.LargestOutgoingTransfer)).ToArray(),
            stints.Select(item => new ClubInput(
                careerParticipantIds[item.CareerId],
                item.ClubName,
                item.OfficialMatches)).ToArray(),
            rules));

        var oldEvents = await database.ScoreEvents
            .Where(item => item.ChallengeId == challengeId)
            .ToListAsync(cancellationToken);
        database.ScoreEvents.RemoveRange(oldEvents);
        database.ScoreEvents.AddRange(result.Events.Select(item => new ScoreEvent
        {
            ChallengeId = challengeId,
            ParticipantId = item.ParticipantId,
            SeasonIndex = item.SeasonIndex,
            SourceType = item.SourceType,
            SourceKey = item.SourceKey,
            Points = item.Points,
            Status = item.Status,
            RuleVersion = 1,
        }));

        challenge.WinningParticipantId = result.WinningParticipantId;
        challenge.WinningSeasonIndex = result.WinningSeasonIndex;
        if (result.WinningParticipantId.HasValue)
        {
            challenge.Status = ChallengeStatus.Completed;
            challenge.CompletedAt ??= DateTimeOffset.UtcNow;
        }
        else if (challenge.Status == ChallengeStatus.Completed)
        {
            challenge.Status = ChallengeStatus.Active;
            challenge.CompletedAt = null;
        }

        challenge.Version++;
        await database.SaveChangesAsync(cancellationToken);
        return result;
    }
}
