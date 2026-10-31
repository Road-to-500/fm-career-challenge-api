using FmCareerChallenge.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FmCareerChallenge.Api.Domain;

public sealed class ScoreRecalculator(ChallengeDbContext database)
{
    public async Task<ScoringInput> GetInputAsync(Guid challengeId, CancellationToken cancellationToken)
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
        var claimCountries = claims.SelectMany(item => new[] { item.CountryCode, item.StartingCareerCountry })
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim().ToUpperInvariant()).Distinct().ToArray();
        var claimClubNames = claims.SelectMany(item => new[] { item.ClubName, item.StartingCareerClub })
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim().ToUpperInvariant()).Distinct().ToArray();
        var claimCatalogEntries = await database.Clubs.AsNoTracking()
            .Where(item => claimCountries.Contains(item.CountryCode) && claimClubNames.Contains(item.NormalizedName))
            .ToListAsync(cancellationToken);
        var claimCatalog = claimCatalogEntries.GroupBy(item => (item.CountryCode, item.NormalizedName))
            .Where(group => group.Select(item => item.Id).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id);
        var ambiguousClaimCatalogIdentities = claimCatalogEntries.GroupBy(item => (item.CountryCode, item.NormalizedName))
            .Where(group => group.Select(item => item.Id).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var seasons = await database.CareerSeasons
            .Where(item => careerIds.Contains(item.CareerId))
            .ToListAsync(cancellationToken);
        var stints = await database.CareerStints
            .Where(item => careerIds.Contains(item.CareerId))
            .ToListAsync(cancellationToken);
        var transfers = await database.Transfers
            .Where(item => careerIds.Contains(item.CareerId) && item.Direction == "Outgoing")
            .ToListAsync(cancellationToken);
        var nationalTeams = await database.NationalTeamStints.Include(item => item.SeasonMatches)
            .Where(item => careerIds.Contains(item.CareerId)).ToListAsync(cancellationToken);
        var countryContinents = (await database.Countries.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(item => item.Code, item => item.ContinentCode, StringComparer.OrdinalIgnoreCase);
        var storedRules = await database.ChallengeRules.Where(item => item.ChallengeId == challengeId).ToListAsync(cancellationToken);
        var definitions = await database.AchievementDefinitions
            .Where(item => item.ChallengeId == null || item.ChallengeId == challengeId).ToListAsync(cancellationToken);
        var ruleVersions = storedRules.GroupBy(item => (item.VersionNumber, item.EffectiveSeasonIndex))
            .Select(group => new RuleVersionInput(group.Key.VersionNumber, group.Key.EffectiveSeasonIndex,
                RaceScoringEngine.WithSupplementalDefaults(group.ToDictionary(item => item.AchievementCode,
                    item => new RuleSetting(item.Points, item.IsEnabled, item.NameOverride, item.DescriptionOverride, item.CategoryOverride,
                        item.FrequencyOverride, item.MinimumCupMatchesOverride)))))
            .ToArray();
        var rules = storedRules.Where(item => item.VersionNumber == 1)
            .ToDictionary(item => item.AchievementCode, item => item.IsEnabled ? item.Points : 0);

        var careerParticipantIds = careers.ToDictionary(item => item.Id, item => item.ParticipantId);
        var clubInputs = await ClubInputLoader.LoadAsync(database, stints, careerParticipantIds, cancellationToken);
        return new ScoringInput(
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
                item.CreatedAt, item.StartingCareerClub, item.StartingCareerCountry, item.CompetitionId,
                item.ClubId ?? (!string.IsNullOrWhiteSpace(item.CountryCode) && !string.IsNullOrWhiteSpace(item.ClubName)
                    && claimCatalog.TryGetValue((item.CountryCode.Trim().ToUpperInvariant(),
                        item.ClubName.Trim().ToUpperInvariant()), out var clubId) ? clubId : null),
                item.StartingCareerClubId ?? (!string.IsNullOrWhiteSpace(item.StartingCareerCountry)
                    && !string.IsNullOrWhiteSpace(item.StartingCareerClub)
                    && claimCatalog.TryGetValue((item.StartingCareerCountry.Trim().ToUpperInvariant(),
                        item.StartingCareerClub.Trim().ToUpperInvariant()), out var startingClubId) ? startingClubId : null),
                item.FirstDivisionExperience, item.HigherDivisionClubMove, item.PreviousBestDivisionLevel,
                item.ThirdDivisionStartSeason, item.StayedWithClubFromThirdToFirst, item.ManagedCompetitionMatches,
                item.FirstMajorClubTrophy, RaceScoringEngine.HasAttestedClubTrophy(item),
                item.WonFirstDivisionTitle, item.WonMajorNationalCup,
                item.ClubId is null && !string.IsNullOrWhiteSpace(item.CountryCode) && !string.IsNullOrWhiteSpace(item.ClubName)
                    && ambiguousClaimCatalogIdentities.Contains((item.CountryCode.Trim().ToUpperInvariant(),
                        item.ClubName.Trim().ToUpperInvariant())))).ToArray(),
            seasons.Select(item => new SeasonInput(
                careerParticipantIds[item.CareerId],
                item.SeasonIndex,
                item.Status == SeasonStatus.Completed,
                item.LeagueMatches,
                item.LeaguePoints,
                item.GoalsFor,
                Math.Max(item.LargestOutgoingTransfer, transfers
                    .Where(transfer => transfer.CareerId == item.CareerId && transfer.SeasonIndex == item.SeasonIndex
                        && string.Equals(transfer.Currency, item.Currency, StringComparison.OrdinalIgnoreCase))
                    .Select(transfer => transfer.FixedAmount).DefaultIfEmpty().Max()),
                item.Currency,
                transfers.Any(transfer => transfer.CareerId == item.CareerId && transfer.SeasonIndex == item.SeasonIndex
                    && transfer.FixedAmount > 0 && !string.Equals(transfer.Currency, item.Currency, StringComparison.OrdinalIgnoreCase)))).ToArray(),
            clubInputs,
            rules,
            ruleVersions,
            definitions,
            careers.Select(career => new
                {
                    career.ParticipantId,
                    Season = RaceScoringEngine.NationalTeamMilestone(career.NationalTeam20MatchSeasonIndex,
                        nationalTeams.Where(item => item.CareerId == career.Id)),
                })
                .Where(item => item.Season.HasValue).ToDictionary(item => item.ParticipantId, item => item.Season!.Value),
            careers.Select(career => new
                {
                    career.ParticipantId,
                    Season = RaceScoringEngine.ThreeContinentsMilestone(career.ThreeContinentsSeasonIndex,
                        RaceScoringEngine.GeographicStints(clubInputs.Where(item => item.ParticipantId == career.ParticipantId),
                            nationalTeams.Where(item => item.CareerId == career.Id)), countryContinents),
                })
                .Where(item => item.Season.HasValue).ToDictionary(item => item.ParticipantId, item => item.Season!.Value));
    }

    public async Task<RaceScoringResult> RecalculateAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var input = await GetInputAsync(challengeId, cancellationToken);
        var result = RaceScoringEngine.Calculate(input);
        var challenge = await database.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
        var participants = await database.ChallengeParticipants.Where(item => item.ChallengeId == challengeId)
            .OrderBy(item => item.JoinedAt).ToListAsync(cancellationToken);
        var ruleVersions = input.RuleVersions ?? [];

        var oldEvents = await database.ScoreEvents
            .Where(item => item.ChallengeId == challengeId)
            .ToListAsync(cancellationToken);
        var oldAutomatic = oldEvents.Where(item => item.SourceType == "Achievement" && item.SourceKey.StartsWith("automatic:"))
            .Select(item => (item.ParticipantId, item.SourceKey, item.SeasonIndex, item.Points)).ToHashSet();
        foreach (var milestone in result.Events.Where(item => item.SourceType == "Achievement"
                     && item.SourceKey.StartsWith("automatic:", StringComparison.Ordinal)
                     && !oldAutomatic.Contains((item.ParticipantId, item.SourceKey, item.SeasonIndex, item.Points))))
        {
            var code = milestone.SourceKey["automatic:".Length..];
            var definition = AchievementCatalog.All.Single(item => item.Code == code);
            foreach (var participant in participants)
                database.UserNotifications.Add(new UserNotification
                {
                    UserId = participant.UserId, ChallengeId = challengeId, Type = "achievement.automatic",
                    Title = "Career milestone unlocked",
                    Message = participant.Id == milestone.ParticipantId
                        ? $"You unlocked {definition.Name}."
                        : $"Your rival unlocked {definition.Name}.",
                    RelatedEntityId = code,
                });
        }
        var oldConfirmedAwards = oldEvents
            .Where(item => item.Status == "Confirmed" && item.SourceType is "FirstBonus" or "Checkpoint")
            .Select(item => (item.SourceType, item.SourceKey, item.ParticipantId, item.Points))
            .ToHashSet();
        var newlyConfirmedAwards = result.Events
            .Where(item => item.Status == "Confirmed" && item.SourceType is "FirstBonus" or "Checkpoint"
                && !oldConfirmedAwards.Contains((item.SourceType, item.SourceKey, item.ParticipantId, item.Points)))
            .GroupBy(item => (item.SourceType, item.SourceKey))
            .ToArray();

        foreach (var award in newlyConfirmedAwards)
        {
            var allWinners = result.Events
                .Where(item => item.Status == "Confirmed" && item.SourceType == award.Key.SourceType && item.SourceKey == award.Key.SourceKey)
                .Select(item => item.ParticipantId).ToHashSet();
            foreach (var participant in participants)
            {
                var won = allWinners.Contains(participant.Id);
                var isCheckpoint = award.Key.SourceType == "Checkpoint";
                database.UserNotifications.Add(new UserNotification
                {
                    UserId = participant.UserId,
                    ChallengeId = challengeId,
                    Type = isCheckpoint ? won ? "checkpoint.won" : "checkpoint.lost" : "bonus.confirmed",
                    Title = isCheckpoint ? "Checkpoint confirmed" : "First bonus confirmed",
                    Message = isCheckpoint
                        ? won ? $"You earned {award.Key.SourceKey.Replace("checkpoint:", string.Empty)}-point checkpoint rewards."
                            : $"Your rival earned the {award.Key.SourceKey.Replace("checkpoint:", string.Empty)}-point checkpoint."
                        : "A first achievement bonus has been confirmed. View the challenge for the result.",
                    RelatedEntityId = award.Key.SourceKey,
                });
            }
        }

        if (result.WinningParticipantId.HasValue && challenge.Status != ChallengeStatus.Completed && !challenge.IsCorrectionMode)
        {
            foreach (var participant in participants)
            {
                database.UserNotifications.Add(new UserNotification
                {
                    UserId = participant.UserId,
                    ChallengeId = challengeId,
                    Type = "challenge.completed",
                    Title = "Challenge complete",
                    Message = participant.Id == result.WinningParticipantId
                        ? "You won the challenge."
                        : "Your rival won the challenge.",
                    RelatedEntityId = challengeId.ToString(),
                });
            }
        }

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
            RuleVersion = item.RuleVersion,
        }));

        challenge.WinningParticipantId = challenge.IsCorrectionMode ? null : result.WinningParticipantId;
        challenge.WinningSeasonIndex = challenge.IsCorrectionMode ? null : result.WinningSeasonIndex;
        if (result.WinningParticipantId.HasValue && !challenge.IsCorrectionMode)
        {
            challenge.Status = ChallengeStatus.Completed;
            challenge.CompletedAt ??= DateTimeOffset.UtcNow;
            challenge.FinalRulesVersion = ruleVersions.Where(item => item.EffectiveSeasonIndex <= result.WinningSeasonIndex)
                .OrderByDescending(item => item.VersionNumber).Select(item => (int?)item.VersionNumber).FirstOrDefault() ?? 1;
            challenge.FinalScoreSnapshot = JsonSerializer.Serialize(new
            {
                result.WinningParticipantId,
                result.WinningSeasonIndex,
                result.Participants,
                result.Events,
            });
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
