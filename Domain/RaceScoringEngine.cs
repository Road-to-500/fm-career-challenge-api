namespace FmCareerChallenge.Api.Domain;

public sealed record ClaimInput(
    Guid Id,
    Guid ParticipantId,
    string Code,
    int SeasonIndex,
    string? ClubName,
    string? CountryCode,
    DateTimeOffset CreatedAt);

public sealed record SeasonInput(
    Guid ParticipantId,
    int SeasonIndex,
    bool IsCompleted,
    int LeagueMatches,
    int LeaguePoints,
    int GoalsFor,
    decimal LargestOutgoingTransfer);

public sealed record ClubInput(Guid ParticipantId, string ClubName, int OfficialMatches);

public sealed record ScoringInput(
    Guid ChallengeId,
    int TargetPoints,
    IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<ClaimInput> Claims,
    IReadOnlyList<SeasonInput> Seasons,
    IReadOnlyList<ClubInput> Clubs,
    IReadOnlyDictionary<string, int> RulePoints);

public sealed record ScoreEventOutput(
    Guid ParticipantId,
    int SeasonIndex,
    string SourceType,
    string SourceKey,
    int Points,
    string Status);

public sealed record ParticipantScore(
    Guid ParticipantId,
    int BasePoints,
    int FirstBonusPoints,
    int DuelPoints,
    int CheckpointPoints,
    int ConfirmedPoints,
    int PotentialPoints,
    int EligibleClubCount);

public sealed record RaceScoringResult(
    IReadOnlyList<ParticipantScore> Participants,
    IReadOnlyList<ScoreEventOutput> Events,
    Guid? WinningParticipantId,
    int? WinningSeasonIndex,
    bool VictoryPending);

public static class RaceScoringEngine
{
    private static readonly (int Threshold, int Bonus)[] Checkpoints =
    [
        (50, 3), (100, 4), (150, 5), (200, 5), (250, 6),
        (300, 7), (350, 8), (400, 9), (450, 10),
    ];

    private static readonly (string Key, int Points, string[] Codes)[] FirstBonuses =
    [
        ("first-first-division-title", 8, ["G001"]),
        ("first-second-division-title", 4, ["G002"]),
        ("first-third-division-title", 3, ["G003", "G004"]),
        ("first-promotion", 4, ["G005", "G006", "G007"]),
        ("first-major-national-cup", 5, ["G008"]),
        ("first-continental-qualification", 5, ["G025", "G027"]),
        ("first-europa-or-conference-title", 8, ["G021", "G022"]),
        ("first-champions-league-title", 12, ["G020"]),
    ];

    private static readonly (string Key, int Points)[] DuelTypes =
    [
        ("league-points-per-game", 4),
        ("league-goals-per-game", 3),
        ("largest-outgoing-transfer", 4),
    ];

    public static RaceScoringResult Calculate(ScoringInput input)
    {
        var participantIds = input.ParticipantIds.Distinct().ToArray();
        if (participantIds.Length is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Race to 500 requires one or two accepted participants.");
        }

        var events = new List<ScoreEventOutput>();
        var definitions = AchievementCatalog.All.ToDictionary(item => item.Code, StringComparer.Ordinal);
        var acceptedClaims = SelectFrequencyClaims(input.Claims, definitions, input.RulePoints);

        foreach (var claim in acceptedClaims)
        {
            var points = input.RulePoints.GetValueOrDefault(claim.Code, definitions[claim.Code].DefaultPoints);
            if (points > 0)
            {
                events.Add(new ScoreEventOutput(
                    claim.ParticipantId,
                    claim.SeasonIndex,
                    "Achievement",
                    claim.Id.ToString("N"),
                    points,
                    "Confirmed"));
            }
        }

        ApplyMutuallyExclusiveGoals(acceptedClaims, events);
        if (participantIds.Length == 2)
        {
            ApplyDuels(input.Seasons, participantIds, events);
            ApplyFirstBonuses(acceptedClaims, input.Seasons, participantIds, input.RulePoints, definitions, events);
        }

        var seasonTotals = SumByParticipantSeason(events);
        var checkpointEvents = ApplyCheckpoints(
            seasonTotals,
            input.Seasons,
            participantIds,
            input.RulePoints,
            events);
        events.AddRange(checkpointEvents);

        var confirmedTotals = SumParticipantPoints(events, includePending: false);
        var potentialTotals = SumParticipantPoints(events, includePending: true);
        var eligibleClubCounts = input.Clubs
            .Where(item => item.OfficialMatches >= 20)
            .GroupBy(item => item.ParticipantId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ClubName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var scores = participantIds.Select(participantId =>
        {
            var own = events.Where(item => item.ParticipantId == participantId).ToArray();
            return new ParticipantScore(
                participantId,
                own.Where(item => item.SourceType == "Achievement" && item.Status == "Confirmed").Sum(item => item.Points),
                own.Where(item => item.SourceType == "FirstBonus" && item.Status == "Confirmed").Sum(item => item.Points),
                own.Where(item => item.SourceType == "Duel" && item.Status == "Confirmed").Sum(item => item.Points),
                own.Where(item => item.SourceType == "Checkpoint" && item.Status == "Confirmed").Sum(item => item.Points),
                confirmedTotals.GetValueOrDefault(participantId),
                potentialTotals.GetValueOrDefault(participantId),
                eligibleClubCounts.GetValueOrDefault(participantId));
        }).ToArray();

        var (winnerId, winnerSeason, pending) = DetermineWinner(
            input,
            participantIds,
            acceptedClaims,
            events,
            eligibleClubCounts);

        return new RaceScoringResult(scores, events, winnerId, winnerSeason, pending);
    }

    private static List<ClaimInput> SelectFrequencyClaims(
        IReadOnlyList<ClaimInput> claims,
        IReadOnlyDictionary<string, AchievementDefinition> definitions,
        IReadOnlyDictionary<string, int> rulePoints)
    {
        var selected = new List<ClaimInput>();
        foreach (var group in claims
                     .Where(claim => definitions.ContainsKey(claim.Code)
                         && rulePoints.GetValueOrDefault(claim.Code, definitions.GetValueOrDefault(claim.Code)?.DefaultPoints ?? 0) > 0)
                     .GroupBy(claim => (claim.ParticipantId, claim.Code)))
        {
            var definition = definitions[group.Key.Code];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in group.OrderBy(item => item.SeasonIndex).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id))
            {
                var context = definition.Frequency switch
                {
                    AchievementFrequency.Once => "once",
                    AchievementFrequency.Season => $"season:{claim.SeasonIndex}",
                    AchievementFrequency.PerClub => $"club:{Normalize(claim.ClubName)}",
                    AchievementFrequency.PerCountry => $"country:{Normalize(claim.CountryCode)}",
                    AchievementFrequency.ClubSeason => $"club-season:{Normalize(claim.ClubName)}:{claim.SeasonIndex}",
                    _ => throw new ArgumentOutOfRangeException(),
                };
                if (!seen.Add(context)) continue;
                if (definition.Frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason
                    && string.IsNullOrWhiteSpace(claim.ClubName)) continue;
                if (definition.Frequency == AchievementFrequency.PerCountry && string.IsNullOrWhiteSpace(claim.CountryCode)) continue;
                selected.Add(claim);
            }
        }

        return selected;
    }

    private static void ApplyMutuallyExclusiveGoals(
        IReadOnlyCollection<ClaimInput> claims,
        List<ScoreEventOutput> events)
    {
        var titleSeasons = claims.Where(claim => claim.Code is "G001" or "G002" or "G003" or "G004")
            .Select(claim => (claim.ParticipantId, claim.SeasonIndex))
            .ToHashSet();
        foreach (var eventIndex in events.Select((item, index) => (item, index))
                     .Where(pair => pair.item.SourceType == "Achievement")
                     .ToArray())
        {
            var sourceId = Guid.TryParseExact(eventIndex.item.SourceKey, "N", out var id) ? id : Guid.Empty;
            var claim = claims.FirstOrDefault(item => item.Id == sourceId);
            if (claim is null) continue;

            if (claim.Code == "G012"
                && claims.Any(item => item.ParticipantId == claim.ParticipantId
                    && item.SeasonIndex == claim.SeasonIndex
                    && item.Code == "G001"))
            {
                events[eventIndex.index] = eventIndex.item with { Points = 0 };
            }

            if (claim.Code is "G005" or "G006" or "G007"
                && titleSeasons.Contains((claim.ParticipantId, claim.SeasonIndex)))
            {
                events[eventIndex.index] = eventIndex.item with { Points = 0 };
            }
        }
    }

    private static void ApplyDuels(
        IReadOnlyList<SeasonInput> seasons,
        IReadOnlyList<Guid> participantIds,
        List<ScoreEventOutput> events)
    {
        var playerA = participantIds[0];
        var playerB = participantIds[1];
        var completedA = seasons.Where(item => item.ParticipantId == playerA && item.IsCompleted)
            .ToDictionary(item => item.SeasonIndex);
        var completedB = seasons.Where(item => item.ParticipantId == playerB && item.IsCompleted)
            .ToDictionary(item => item.SeasonIndex);

        foreach (var seasonIndex in completedA.Keys.Intersect(completedB.Keys))
        {
            var a = completedA[seasonIndex];
            var b = completedB[seasonIndex];
            if (a.LeagueMatches >= 15 && b.LeagueMatches >= 15)
            {
                var pointsComparison = CompareRates(a.LeaguePoints, a.LeagueMatches, b.LeaguePoints, b.LeagueMatches);
                if (pointsComparison != 0)
                {
                    AddDuel("league-points-per-game", pointsComparison > 0 ? playerA : playerB, seasonIndex, playerA, playerB, events);
                }

                var goalsComparison = CompareRates(a.GoalsFor, a.LeagueMatches, b.GoalsFor, b.LeagueMatches);
                if (goalsComparison != 0)
                {
                    AddDuel("league-goals-per-game", goalsComparison > 0 ? playerA : playerB, seasonIndex, playerA, playerB, events);
                }
            }

            if (a.LargestOutgoingTransfer > b.LargestOutgoingTransfer)
            {
                AddDuel("largest-outgoing-transfer", playerA, seasonIndex, playerA, playerB, events);
            }
            else if (b.LargestOutgoingTransfer > a.LargestOutgoingTransfer)
            {
                AddDuel("largest-outgoing-transfer", playerB, seasonIndex, playerA, playerB, events);
            }
        }
    }

    private static void AddDuel(
        string type,
        Guid? winner,
        int seasonIndex,
        Guid playerA,
        Guid playerB,
        List<ScoreEventOutput> events)
    {
        if (winner is null) return;
        var points = DuelTypes.Single(item => item.Key == type).Points;
        events.Add(new ScoreEventOutput(winner.Value, seasonIndex, "Duel", $"{type}:{seasonIndex}", points, "Confirmed"));
    }

    private static int CompareRates(int aValue, int aMatches, int bValue, int bMatches) =>
        ((long)aValue * bMatches).CompareTo((long)bValue * aMatches);

    private static void ApplyFirstBonuses(
        IReadOnlyList<ClaimInput> claims,
        IReadOnlyList<SeasonInput> seasons,
        IReadOnlyList<Guid> participantIds,
        IReadOnlyDictionary<string, int> rules,
        IReadOnlyDictionary<string, AchievementDefinition> definitions,
        List<ScoreEventOutput> events)
    {
        var completedThrough = participantIds.ToDictionary(
            id => id,
            id => seasons.Where(item => item.ParticipantId == id && item.IsCompleted)
                .Select(item => (int?)item.SeasonIndex).Max() ?? -1);

        foreach (var bonus in FirstBonuses)
        {
            var hits = participantIds
                .Select(id => new
                {
                    ParticipantId = id,
                    SeasonIndex = claims
                        .Where(claim => claim.ParticipantId == id && bonus.Codes.Contains(claim.Code, StringComparer.Ordinal))
                        .Select(claim => (int?)claim.SeasonIndex)
                        .Min(),
                })
                .Where(item => item.SeasonIndex.HasValue)
                .OrderBy(item => item.SeasonIndex)
                .ToArray();
            if (hits.Length == 0) continue;

            var points = rules.GetValueOrDefault(bonus.Codes[0],
                definitions.GetValueOrDefault(bonus.Codes[0])?.DefaultPoints ?? bonus.Points) > 0
                ? bonus.Points
                : 0;
            var firstSeason = hits[0].SeasonIndex!.Value;
            var firstHits = hits.Where(item => item.SeasonIndex == firstSeason).ToArray();
            var canConfirm = hits.Length > 1 || participantIds
                .Where(id => id != hits[0].ParticipantId)
                .All(id => completedThrough[id] >= firstSeason);
            if (firstHits.Length > 1)
            {
                var sharedPoints = points / 2;
                foreach (var hit in firstHits)
                {
                    events.Add(new ScoreEventOutput(hit.ParticipantId, firstSeason, "FirstBonus", bonus.Key, sharedPoints, "Confirmed"));
                }
            }
            else
            {
                events.Add(new ScoreEventOutput(
                    firstHits[0].ParticipantId,
                    firstSeason,
                    "FirstBonus",
                    bonus.Key,
                    points,
                    canConfirm ? "Confirmed" : "Pending"));
            }
        }
    }

    private static List<ScoreEventOutput> ApplyCheckpoints(
        IReadOnlyDictionary<(Guid ParticipantId, int SeasonIndex), int> seasonTotals,
        IReadOnlyList<SeasonInput> seasons,
        IReadOnlyList<Guid> participantIds,
        IReadOnlyDictionary<string, int> rules,
        List<ScoreEventOutput> events)
    {
        var generated = new List<ScoreEventOutput>();
        if (participantIds.Count != 2) return generated;
        var orderedSeasons = participantIds.ToDictionary(
            participantId => participantId,
            participantId => seasons.Where(item => item.ParticipantId == participantId && item.IsCompleted)
                .Select(item => item.SeasonIndex).Distinct().Order().ToArray());

        foreach (var (threshold, defaultPoints) in Checkpoints)
        {
            var hits = participantIds.ToDictionary(
                id => id,
                id => FirstSeasonAtThreshold(id, threshold, seasonTotals, orderedSeasons[id]));
            var reached = hits.Where(item => item.Value.HasValue)
                .OrderBy(item => item.Value)
                .ToArray();
            if (reached.Length == 0) continue;
            var firstSeason = reached[0].Value!.Value;
            var sameSeason = reached.Where(item => item.Value == firstSeason).ToArray();
            var awardKey = $"checkpoint:{threshold}";
            var points = rules.GetValueOrDefault($"checkpoint-{threshold}", defaultPoints);

            if (sameSeason.Length == 2)
            {
                var aScore = EligibleScoreThrough(participantIds[0], firstSeason, seasonTotals);
                var bScore = EligibleScoreThrough(participantIds[1], firstSeason, seasonTotals);
                if (aScore == bScore)
                {
                    var sharedPoints = points / 2;
                    generated.Add(new ScoreEventOutput(participantIds[0], firstSeason, "Checkpoint", awardKey, sharedPoints, "Confirmed"));
                    generated.Add(new ScoreEventOutput(participantIds[1], firstSeason, "Checkpoint", awardKey, sharedPoints, "Confirmed"));
                }
                else
                {
                    var winner = aScore > bScore ? participantIds[0] : participantIds[1];
                    generated.Add(new ScoreEventOutput(winner, firstSeason, "Checkpoint", awardKey, points, "Confirmed"));
                }
                continue;
            }

            var firstParticipant = sameSeason[0].Key;
            var other = participantIds.Single(id => id != firstParticipant);
            var hasCompletedComparableSeason = orderedSeasons[other].Any(index => index >= firstSeason);
            generated.Add(new ScoreEventOutput(
                firstParticipant,
                firstSeason,
                "Checkpoint",
                awardKey,
                points,
                hasCompletedComparableSeason ? "Confirmed" : "Pending"));
        }

        return generated;
    }

    private static int? FirstSeasonAtThreshold(
        Guid participantId,
        int threshold,
        IReadOnlyDictionary<(Guid ParticipantId, int SeasonIndex), int> seasonTotals,
        IReadOnlyList<int> completedSeasons)
    {
        var total = 0;
        foreach (var seasonIndex in completedSeasons.Order())
        {
            total += seasonTotals.GetValueOrDefault((participantId, seasonIndex));
            if (total >= threshold) return seasonIndex;
        }
        return null;
    }

    private static int EligibleScoreThrough(
        Guid participantId,
        int seasonIndex,
        IReadOnlyDictionary<(Guid ParticipantId, int SeasonIndex), int> seasonTotals) =>
        seasonTotals.Where(item => item.Key.ParticipantId == participantId && item.Key.SeasonIndex <= seasonIndex)
            .Sum(item => item.Value);

    private static (Guid? WinnerId, int? SeasonIndex, bool Pending) DetermineWinner(
        ScoringInput input,
        IReadOnlyList<Guid> participantIds,
        IReadOnlyList<ClaimInput> claims,
        IReadOnlyList<ScoreEventOutput> events,
        IReadOnlyDictionary<Guid, int> eligibleClubCounts)
    {
        var confirmedBySeason = events.Where(item => item.Status == "Confirmed")
            .GroupBy(item => (item.ParticipantId, item.SeasonIndex))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Points));
        var candidates = new List<(Guid Id, int Season, int Score, int Trophies)>();
        foreach (var participantId in participantIds)
        {
            if (eligibleClubCounts.GetValueOrDefault(participantId) < 3) continue;
            var cumulative = 0;
            var indices = events.Where(item => item.ParticipantId == participantId)
                .Select(item => item.SeasonIndex)
                .Concat(input.Seasons.Where(item => item.ParticipantId == participantId).Select(item => item.SeasonIndex))
                .Distinct().Order();
            foreach (var index in indices)
            {
                cumulative += confirmedBySeason.GetValueOrDefault((participantId, index));
                if (cumulative < input.TargetPoints) continue;
                var trophies = claims.Count(claim => claim.ParticipantId == participantId
                    && claim.SeasonIndex <= index
                    && claim.Code is "G001" or "G002" or "G003" or "G004" or "G008" or "G009" or "G020" or "G021" or "G022" or "G024" or "G034" or "G035");
                candidates.Add((participantId, index, cumulative, trophies));
                break;
            }
        }

        if (candidates.Count == 0) return (null, null, false);
        var earliest = candidates.Min(item => item.Season);
        var sameSeason = candidates.Where(item => item.Season == earliest).ToArray();
        if (sameSeason.Length == 1) return (sameSeason[0].Id, earliest, false);
        var ranked = sameSeason.OrderByDescending(item => item.Score).ThenByDescending(item => item.Trophies).ToArray();
        if (ranked.Length > 1 && ranked[0].Score == ranked[1].Score && ranked[0].Trophies == ranked[1].Trophies)
        {
            return (null, earliest, true);
        }

        return (ranked[0].Id, earliest, false);
    }

    private static Dictionary<(Guid ParticipantId, int SeasonIndex), int> SumByParticipantSeason(
        IEnumerable<ScoreEventOutput> events) =>
        events.Where(item => item.Status == "Confirmed")
            .GroupBy(item => (item.ParticipantId, item.SeasonIndex))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Points));

    private static Dictionary<Guid, int> SumParticipantPoints(
        IEnumerable<ScoreEventOutput> events,
        bool includePending) =>
        events.Where(item => includePending || item.Status == "Confirmed")
            .GroupBy(item => item.ParticipantId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Points));

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
