namespace FmCareerChallenge.Api.Domain;

public sealed record ClaimInput(
    Guid Id,
    Guid ParticipantId,
    string Code,
    int SeasonIndex,
    string? ClubName,
    string? CountryCode,
    DateTimeOffset CreatedAt,
    string? StartingCareerClub = null,
    string? StartingCareerCountry = null,
    Guid? CompetitionId = null,
    Guid? ClubId = null,
    Guid? StartingCareerClubId = null,
    bool? FirstDivisionExperience = null,
    bool? HigherDivisionClubMove = null,
    int? PreviousBestDivisionLevel = null,
    int? ThirdDivisionStartSeason = null,
    bool? StayedWithClubFromThirdToFirst = null,
    int? ManagedCompetitionMatches = null,
    bool? FirstMajorClubTrophy = null,
    bool HasAttestedClubTrophy = false,
    bool? WonFirstDivisionTitle = null,
    bool? WonMajorNationalCup = null,
    bool ClubIdentityAmbiguous = false);

public sealed record SeasonInput(
    Guid ParticipantId,
    int SeasonIndex,
    bool IsCompleted,
    int LeagueMatches,
    int LeaguePoints,
    int GoalsFor,
    decimal LargestOutgoingTransfer,
    string Currency = "EUR",
    bool HasUncomparableTransfers = false);

public sealed record ClubInput(Guid ParticipantId, string ClubName, int OfficialMatches, int? EligibleSeasonIndex,
    string? CountryCode = null, int StartSeasonIndex = 0, int? EndSeasonIndex = null, int? CountryEligibleSeasonIndex = null,
    IReadOnlyList<SeasonMatchInput>? SeasonMatches = null, Guid? ClubId = null, int? InitialDivisionLevel = null);
public sealed record SeasonMatchInput(int SeasonIndex, int OfficialMatches, int? DivisionLevel = null);
public sealed record CountryMilestoneInput(Guid ParticipantId, string CountryCode, int SeasonIndex);
public sealed record GeographicStintInput(string? CountryCode, int OfficialMatches, int StartSeasonIndex, int? EndSeasonIndex,
    IReadOnlyList<SeasonMatchInput>? SeasonMatches = null);

public sealed record RuleSetting(int Points, bool IsEnabled, string? NameOverride = null,
    string? DescriptionOverride = null, string? CategoryOverride = null, AchievementFrequency? FrequencyOverride = null,
    int? MinimumCupMatchesOverride = null);
public sealed record RuleVersionInput(int VersionNumber, int EffectiveSeasonIndex, IReadOnlyDictionary<string, RuleSetting> Rules);

public sealed record ScoringInput(
    Guid ChallengeId,
    int TargetPoints,
    IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<ClaimInput> Claims,
    IReadOnlyList<SeasonInput> Seasons,
    IReadOnlyList<ClubInput> Clubs,
    IReadOnlyDictionary<string, int> RulePoints,
    IReadOnlyList<RuleVersionInput>? RuleVersions = null,
    IReadOnlyList<AchievementDefinition>? Definitions = null,
    IReadOnlyDictionary<Guid, int>? NationalTeamMilestones = null,
    IReadOnlyDictionary<Guid, int>? ThreeContinentsMilestones = null);

public sealed record ScoreEventOutput(
    Guid ParticipantId,
    int SeasonIndex,
    string SourceType,
    string SourceKey,
    int Points,
    string Status,
    int RuleVersion = 1);

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
    public static readonly IReadOnlySet<string> AutomaticMilestones = new HashSet<string>(StringComparer.Ordinal)
    {
        "G042", "G043", "G044", "G045", "G046", "G047", "G049", "G052",
    };

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

    public static IReadOnlyDictionary<string, RuleSetting> SupplementalRuleDefaults { get; } =
        FirstBonuses.ToDictionary(item => $"bonus:{item.Key}", item => new RuleSetting(item.Points, true))
            .Concat(DuelTypes.Select(item => new KeyValuePair<string, RuleSetting>($"duel:{item.Key}", new RuleSetting(item.Points, true))))
            .Concat(Checkpoints.Select(item => new KeyValuePair<string, RuleSetting>($"checkpoint-{item.Threshold}", new RuleSetting(item.Bonus, true))))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    public static Dictionary<string, RuleSetting> WithSupplementalDefaults(IEnumerable<KeyValuePair<string, RuleSetting>> rules)
    {
        var result = SupplementalRuleDefaults.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var rule in rules) result[rule.Key] = rule.Value;
        return result;
    }

    private static readonly IReadOnlySet<string> MajorTrophyCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "G001", "G002", "G003", "G004", "G008", "G009", "G020", "G021", "G022", "G024", "G034", "G035",
    };

    public static RaceScoringResult Calculate(ScoringInput input)
    {
        var participantIds = input.ParticipantIds.Distinct().ToArray();
        if (participantIds.Length is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Race to 500 requires one or two accepted participants.");
        }

        var events = new List<ScoreEventOutput>();
        var definitions = (input.Definitions ?? AchievementCatalog.All).ToDictionary(item => item.Code, StringComparer.Ordinal);
        input = input with { Claims = ResolveClaimClubIds(input.Claims, input.Clubs) };
        var acceptedClaims = SelectFrequencyClaims(input, definitions);
        var trophyMilestones = acceptedClaims.Where(item => MajorTrophyCodes.Contains(item.Code) && !FailsCupMinimum(input, item))
            .GroupBy(item => item.ParticipantId)
            .Where(group => group.Count() >= 10)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SeasonIndex)
                .ThenBy(item => item.CreatedAt).ThenBy(item => item.Id).Skip(9).First().SeasonIndex);
        foreach (var (participantId, season) in trophyMilestones.ToArray())
        {
            var manual = acceptedClaims.FirstOrDefault(item => item.ParticipantId == participantId && item.Code == "G048");
            if (!RuleFor(input, "G048", season, definitions["G048"].DefaultPoints).IsEnabled
                || manual is not null && manual.SeasonIndex < season)
                trophyMilestones.Remove(participantId);
            else
                acceptedClaims.RemoveAll(item => item.ParticipantId == participantId && item.Code == "G048");
        }

        foreach (var claim in acceptedClaims)
        {
            var points = RuleFor(input, claim.Code, claim.SeasonIndex, definitions[claim.Code].DefaultPoints).Points;
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

        var eligibleClubs = EligibleClubs(input.Clubs);
        ApplyClubMilestones(input, eligibleClubs, participantIds, definitions, events);
        ApplyLeagueTitleMilestones(input, acceptedClaims, participantIds, definitions, events);
        foreach (var (participantId, season) in trophyMilestones)
        {
            if (!participantIds.Contains(participantId)) continue;
            var rule = RuleFor(input, "G048", season, definitions["G048"].DefaultPoints);
            if (rule.IsEnabled && rule.Points > 0)
                events.Add(new ScoreEventOutput(participantId, season, "Achievement", "automatic:G048", rule.Points, "Confirmed"));
        }
        foreach (var (participantId, season) in input.NationalTeamMilestones ?? new Dictionary<Guid, int>())
        {
            if (!participantIds.Contains(participantId)) continue;
            var rule = RuleFor(input, "G033", season, definitions["G033"].DefaultPoints);
            if (rule.IsEnabled && rule.Points > 0)
                events.Add(new ScoreEventOutput(participantId, season, "Achievement", "automatic:G033", rule.Points, "Confirmed"));
        }
        foreach (var (participantId, season) in input.ThreeContinentsMilestones ?? new Dictionary<Guid, int>())
        {
            if (!participantIds.Contains(participantId)) continue;
            var rule = RuleFor(input, "G053", season, definitions["G053"].DefaultPoints);
            if (rule.IsEnabled && rule.Points > 0)
                events.Add(new ScoreEventOutput(participantId, season, "Achievement", "automatic:G053", rule.Points, "Confirmed"));
        }
        var excludedClaims = ApplyMutuallyExclusiveGoals(acceptedClaims, events);
        foreach (var claim in acceptedClaims.Where(item => FailsCupMinimum(input, item)))
        {
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code == "G040" && item.FirstMajorClubTrophy == true))
        {
            if (ValidateFirstMajorClubTrophy(claim, input.Claims) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code == "G013"
                     && item.WonFirstDivisionTitle == true && item.WonMajorNationalCup == true))
        {
            if (ValidateDomesticDoubleConsistency(claim, input.Claims) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code is "G037" or "G041" && item.StartingCareerClub is not null))
        {
            if (ValidateCareerMove(claim, input.Clubs) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code == "G038" && item.FirstDivisionExperience == true))
        {
            if (ValidateFirstDivisionExperience(claim, input.Clubs) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code == "G039" && item.HigherDivisionClubMove == true))
        {
            if (ValidateHigherDivisionMove(claim, input.Clubs) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        foreach (var claim in acceptedClaims.Where(item => item.Code == "G050" && item.StayedWithClubFromThirdToFirst == true))
        {
            if (ValidateThirdToFirstContinuity(claim, input.Clubs) is null) continue;
            excludedClaims.Add(claim.Id);
            var index = events.FindIndex(item => item.SourceType == "Achievement" && item.SourceKey == claim.Id.ToString("N"));
            if (index >= 0) events[index] = events[index] with { Points = 0 };
        }
        if (participantIds.Length == 2)
        {
            ApplyDuels(input, participantIds, events);
            ApplyFirstBonuses(input, acceptedClaims.Where(claim => !excludedClaims.Contains(claim.Id)).ToArray(), input.Seasons, participantIds, events);
        }

        var seasonTotals = SumByParticipantSeason(events);
        var checkpointEvents = ApplyCheckpoints(
            seasonTotals,
            input.Seasons,
            participantIds,
            input,
            events);
        events.AddRange(checkpointEvents);
        events = events.Select(item => item with { RuleVersion = VersionFor(input, item.SeasonIndex)?.VersionNumber ?? 1 }).ToList();

        var confirmedTotals = SumParticipantPoints(events, includePending: false);
        var potentialTotals = SumParticipantPoints(events, includePending: true);
        var eligibleClubCounts = eligibleClubs
            .GroupBy(item => item.ParticipantId)
            .ToDictionary(group => group.Key, group => DistinctClubMilestoneSeasons(group.Select(item =>
                (item.ClubId, item.ClubName, item.CountryCode, item.EligibleSeasonIndex!.Value))).Length);

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
            input with { Clubs = eligibleClubs },
            participantIds,
            acceptedClaims.Where(claim => !excludedClaims.Contains(claim.Id)).ToArray(),
            events,
            eligibleClubCounts);

        return new RaceScoringResult(scores, events, winnerId, winnerSeason, pending);
    }

    public static string? ValidateFirstDivisionExperience(ClaimInput claim, IEnumerable<ClubInput> clubs) =>
        clubs.Any(stint => stint.ParticipantId == claim.ParticipantId
            && (stint.InitialDivisionLevel == 1 && stint.StartSeasonIndex < claim.SeasonIndex
                || (stint.SeasonMatches ?? []).Any(season => season.SeasonIndex < claim.SeasonIndex
                    && season.OfficialMatches > 0 && season.DivisionLevel == 1)))
            ? "A first-division contract or match is already recorded in an earlier season."
            : null;

    public static string? ValidateHigherDivisionMove(ClaimInput claim, IEnumerable<ClubInput> clubs) =>
        claim.PreviousBestDivisionLevel.HasValue && clubs.Any(stint => stint.ParticipantId == claim.ParticipantId
            && (stint.StartSeasonIndex < claim.SeasonIndex && stint.InitialDivisionLevel < claim.PreviousBestDivisionLevel
                || (stint.SeasonMatches ?? []).Any(season => season.SeasonIndex < claim.SeasonIndex
                    && season.OfficialMatches > 0 && season.DivisionLevel < claim.PreviousBestDivisionLevel)))
            ? "An earlier recorded contract or match is in a higher division than your stated previous best."
            : null;

    public static string? ValidateThirdToFirstContinuity(ClaimInput claim, IEnumerable<ClubInput> clubs)
    {
        if (claim.ThirdDivisionStartSeason is not int start || string.IsNullOrWhiteSpace(claim.ClubName)) return null;
        var interruption = clubs.Any(stint => stint.ParticipantId == claim.ParticipantId
            && !(claim.ClubId.HasValue && stint.ClubId.HasValue
                ? claim.ClubId == stint.ClubId
                : SameRecordedClub(claim.ClubName, claim.CountryCode, stint.ClubName, stint.CountryCode))
            && (stint.OfficialMatches > 0 && stint.StartSeasonIndex > start
                && stint.EndSeasonIndex is int end && end < claim.SeasonIndex
                || (stint.SeasonMatches ?? []).Any(season => season.OfficialMatches > 0
                    && season.SeasonIndex > start && season.SeasonIndex < claim.SeasonIndex)));
        return interruption ? "Matches at another club are recorded between the third-division and first-division seasons." : null;
    }

    public static bool HasAttestedClubTrophy(AchievementClaim claim) => claim.AchievementCode switch
    {
        "G001" or "G002" or "G003" or "G004" => claim.ManagedCompetitionMatches > 0 && claim.InChargeAtCompetitionEnd == true,
        "G008" or "G009" or "G015" => claim.ManagedCompetitionMatches > 0 && claim.ManagedDecidingMatch == true,
        "G020" or "G021" or "G022" or "G024" => claim.CompetitionResultKind is not null && claim.CompetitionResultStage == "Winner",
        _ => false,
    };

    public static string? ValidateFirstMajorClubTrophy(ClaimInput claim, IEnumerable<ClaimInput> claims) =>
        claims.Any(prior => prior.ParticipantId == claim.ParticipantId && prior.HasAttestedClubTrophy
            && prior.SeasonIndex < claim.SeasonIndex
            && (claim.ClubId.HasValue && prior.ClubId.HasValue ? claim.ClubId == prior.ClubId
                : !string.IsNullOrWhiteSpace(claim.CountryCode) && !string.IsNullOrWhiteSpace(prior.CountryCode)
                    && SameRecordedClub(claim, prior)))
            ? "An earlier major trophy is recorded for this club." : null;

    public static string? ValidateDomesticDoubleConsistency(ClaimInput claim, IEnumerable<ClaimInput> claims)
    {
        if (claim.Code != "G013" || claim.WonFirstDivisionTitle != true || claim.WonMajorNationalCup != true)
            return null;

        var records = claims.Where(item => item.ParticipantId == claim.ParticipantId
            && item.SeasonIndex == claim.SeasonIndex && item.Id != claim.Id).ToArray();
        var titles = records.Where(item => item.Code == "G001").ToArray();
        var cups = records.Where(item => item.Code == "G008").ToArray();
        var matchingTitles = titles.Where(item => !HasKnownClubOrCountryConflict(claim, item)).ToArray();
        var matchingCups = cups.Where(item => !HasKnownClubOrCountryConflict(claim, item)).ToArray();
        if (titles.Length > 0 && matchingTitles.Length == 0 || cups.Length > 0 && matchingCups.Length == 0)
            return "This domestic double conflicts with a recorded league title or national cup for another club or country.";
        if (matchingTitles.Length > 0 && matchingCups.Length > 0
            && !matchingTitles.Any(title => matchingCups.Any(cup => !HasKnownClubOrCountryConflict(title, cup))))
            return "The recorded league title and national cup do not identify the same club and country.";

        return null;
    }

    private static bool HasKnownClubOrCountryConflict(ClaimInput first, ClaimInput second)
    {
        if (first.ClubId.HasValue && second.ClubId.HasValue)
            return first.ClubId != second.ClubId;
        if (!string.IsNullOrWhiteSpace(first.ClubName) && !string.IsNullOrWhiteSpace(second.ClubName)
            && !string.Equals(Normalize(first.ClubName), Normalize(second.ClubName), StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(first.CountryCode) && !string.IsNullOrWhiteSpace(second.CountryCode)
            && !string.Equals(Normalize(first.CountryCode), Normalize(second.CountryCode), StringComparison.OrdinalIgnoreCase);
    }

    private static List<ClaimInput> SelectFrequencyClaims(
        ScoringInput input,
        IReadOnlyDictionary<string, AchievementDefinition> definitions)
    {
        var selected = new List<ClaimInput>();
        foreach (var group in input.Claims
                     .Where(claim => definitions.ContainsKey(claim.Code) && !AutomaticMilestones.Contains(claim.Code)
                         && !(claim.Code == "G033" && (input.NationalTeamMilestones?.ContainsKey(claim.ParticipantId) ?? false))
                         && !(claim.Code == "G053" && (input.ThreeContinentsMilestones?.ContainsKey(claim.ParticipantId) ?? false))
                         && RuleFor(input, claim.Code, claim.SeasonIndex, definitions[claim.Code].DefaultPoints).IsEnabled)
                     .GroupBy(claim => (claim.ParticipantId, claim.Code)))
        {
            var definition = definitions[group.Key.Code];
            var seenClaims = new List<ClaimInput>();
            var clubMoveSeasons = new HashSet<int>();
            foreach (var claim in group.OrderBy(item => item.SeasonIndex).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id))
            {
                var frequency = RuleFor(input, claim.Code, claim.SeasonIndex, definition.DefaultPoints).FrequencyOverride
                    ?? definition.Frequency;
                if (seenClaims.Any(existing => FrequencyMatches(frequency, existing, claim))) continue;
                if (frequency is AchievementFrequency.PerClub or AchievementFrequency.ClubSeason
                    && string.IsNullOrWhiteSpace(claim.ClubName)) continue;
                if (frequency == AchievementFrequency.PerCountry && string.IsNullOrWhiteSpace(claim.CountryCode)) continue;
                if (FailsCupMinimum(input, claim))
                {
                    selected.Add(claim);
                    continue;
                }
                if (claim.Code == "G040" && claim.FirstMajorClubTrophy == true
                    && ValidateFirstMajorClubTrophy(claim, input.Claims) is not null)
                {
                    selected.Add(claim);
                    continue;
                }
                if (claim.Code == "G013" && claim.WonFirstDivisionTitle == true && claim.WonMajorNationalCup == true
                    && ValidateDomesticDoubleConsistency(claim, input.Claims) is not null)
                {
                    selected.Add(claim);
                    continue;
                }
                if (claim.Code == "G037" && claim.StartingCareerClub is not null
                    && ValidateCareerMove(claim, input.Clubs) is not null)
                {
                    selected.Add(claim);
                    continue;
                }
                if (claim.Code == "G050" && claim.StayedWithClubFromThirdToFirst == true
                    && ValidateThirdToFirstContinuity(claim, input.Clubs) is not null)
                {
                    selected.Add(claim);
                    continue;
                }
                if (claim.Code == "G037" && clubMoveSeasons.Contains(claim.SeasonIndex)) continue;
                seenClaims.Add(claim);
                if (claim.Code == "G037") clubMoveSeasons.Add(claim.SeasonIndex);
                selected.Add(claim);
            }
        }

        return selected;
    }

    public static bool FrequencyMatches(AchievementFrequency frequency, ClaimInput earlier, ClaimInput later) => frequency switch
    {
        AchievementFrequency.Once => true,
        AchievementFrequency.Season => earlier.SeasonIndex == later.SeasonIndex,
        AchievementFrequency.PerClub => !string.IsNullOrWhiteSpace(earlier.ClubName)
            && !string.IsNullOrWhiteSpace(later.ClubName) && SameRecordedClub(earlier, later),
        AchievementFrequency.PerCountry => !string.IsNullOrWhiteSpace(earlier.CountryCode)
            && !string.IsNullOrWhiteSpace(later.CountryCode)
            && string.Equals(earlier.CountryCode.Trim(), later.CountryCode.Trim(), StringComparison.OrdinalIgnoreCase),
        AchievementFrequency.ClubSeason => earlier.SeasonIndex == later.SeasonIndex
            && !string.IsNullOrWhiteSpace(earlier.ClubName) && !string.IsNullOrWhiteSpace(later.ClubName)
            && SameRecordedClub(earlier, later),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };

    private static bool FailsCupMinimum(ScoringInput input, ClaimInput claim) =>
        claim.Code is "G008" or "G009" or "G015"
        && claim.ManagedCompetitionMatches is int managed
        && managed < (RuleFor(input, claim.Code, claim.SeasonIndex, 0).MinimumCupMatchesOverride ?? 2);

    private static void ApplyClubMilestones(
        ScoringInput input,
        IReadOnlyList<ClubInput> eligibleClubs,
        IReadOnlyList<Guid> participantIds,
        IReadOnlyDictionary<string, AchievementDefinition> definitions,
        List<ScoreEventOutput> events)
    {
        foreach (var participantId in participantIds)
        {
            var clubSeasons = eligibleClubs
                .Where(item => item.ParticipantId == participantId && item.OfficialMatches >= 20
                    && item.EligibleSeasonIndex.HasValue && !string.IsNullOrWhiteSpace(item.ClubName))
                .Select(item => (item.ClubId, item.ClubName, item.CountryCode, item.EligibleSeasonIndex!.Value));
            var distinctClubSeasons = DistinctClubMilestoneSeasons(clubSeasons);
            foreach (var (code, threshold) in new[] { ("G042", 3), ("G043", 5), ("G044", 8) })
            {
                if (distinctClubSeasons.Length < threshold) continue;
                var rule = RuleFor(input, code, distinctClubSeasons[threshold - 1], definitions[code].DefaultPoints);
                if (rule.IsEnabled && rule.Points > 0)
                    events.Add(new ScoreEventOutput(participantId, distinctClubSeasons[threshold - 1],
                        "Achievement", $"automatic:{code}", rule.Points, "Confirmed"));
            }
            var countrySeasons = EligibleCountries(input.Clubs)
                .Where(item => item.ParticipantId == participantId)
                .Select(item => item.SeasonIndex)
                .Order().ToArray();
            if (countrySeasons.Length >= 4)
            {
                var season = countrySeasons[3];
                var rule = RuleFor(input, "G047", season, definitions["G047"].DefaultPoints);
                if (rule.IsEnabled && rule.Points > 0)
                    events.Add(new ScoreEventOutput(participantId, season,
                        "Achievement", "automatic:G047", rule.Points, "Confirmed"));
            }
        }
    }

    private static void ApplyLeagueTitleMilestones(
        ScoringInput input,
        IReadOnlyList<ClaimInput> claims,
        IReadOnlyList<Guid> participantIds,
        IReadOnlyDictionary<string, AchievementDefinition> definitions,
        List<ScoreEventOutput> events)
    {
        foreach (var participantId in participantIds)
        {
            var titles = claims.Where(item => item.ParticipantId == participantId && item.Code == "G001")
                .OrderBy(item => item.SeasonIndex).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
            var countrySeasons = titles.Where(item => !string.IsNullOrWhiteSpace(item.CountryCode))
                .GroupBy(item => item.CountryCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Min(item => item.SeasonIndex)).Order().ToArray();
            var clubSeasons = titles.Where(item => !string.IsNullOrWhiteSpace(item.ClubName) && !string.IsNullOrWhiteSpace(item.CountryCode))
                .Select(item => (item.ClubId, item.ClubName!, item.CountryCode, item.SeasonIndex));
            Award("G045", countrySeasons, 2);
            Award("G046", countrySeasons, 3);
            Award("G049", DistinctClubMilestoneSeasons(clubSeasons), 2);
            Award("G052", titles.Select(item => item.SeasonIndex).ToArray(), 5);

            void Award(string code, int[] seasons, int threshold)
            {
                if (seasons.Length < threshold) return;
                var rule = RuleFor(input, code, seasons[threshold - 1], definitions[code].DefaultPoints);
                if (rule.IsEnabled && rule.Points > 0)
                    events.Add(new ScoreEventOutput(participantId, seasons[threshold - 1],
                        "Achievement", $"automatic:{code}", rule.Points, "Confirmed"));
            }
        }
    }

    private static int[] DistinctClubMilestoneSeasons(
        IEnumerable<(Guid? ClubId, string ClubName, string? CountryCode, int Season)> clubs)
    {
        var records = clubs.ToArray();
        var identified = records.Where(item => item.ClubId.HasValue)
            .GroupBy(item => item.ClubId!.Value)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Season));
        var identifiedByName = records.Where(item => item.ClubId.HasValue)
            .GroupBy(item => (Name: Normalize(item.ClubName).ToUpperInvariant(),
                Country: Normalize(item.CountryCode).ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => group.Select(item => item.ClubId!.Value).Distinct().ToArray());
        var unlinkedSeasons = new List<int>();
        foreach (var group in records.Where(item => !item.ClubId.HasValue)
            .GroupBy(item => (Name: Normalize(item.ClubName).ToUpperInvariant(),
                Country: Normalize(item.CountryCode).ToUpperInvariant())))
        {
            var season = group.Min(item => item.Season);
            if (!identifiedByName.TryGetValue(group.Key, out var possibleIds))
            {
                unlinkedSeasons.Add(season);
                continue;
            }
            // Text-only records may describe any matching identity and cannot prove an additional club.
            var earliestId = possibleIds.OrderBy(id => identified[id]).ThenBy(id => id).First();
            identified[earliestId] = Math.Min(identified[earliestId], season);
        }
        return identified.Values.Concat(unlinkedSeasons).Order().ToArray();
    }

    private static HashSet<Guid> ApplyMutuallyExclusiveGoals(
        IReadOnlyCollection<ClaimInput> claims,
        List<ScoreEventOutput> events)
    {
        var titles = claims.Where(claim => claim.Code is "G001" or "G002" or "G003" or "G004").ToArray();
        var excluded = new HashSet<Guid>();
        foreach (var claim in claims)
        {
            if (claim.Code is "G012" or "G016"
                && claims.Any(item => item.ParticipantId == claim.ParticipantId
                    && item.SeasonIndex == claim.SeasonIndex
                    && item.Code == "G001"))
            {
                excluded.Add(claim.Id);
            }

            var continentalRank = claim.Code switch
            {
                "G028" => 1,
                "G029" => 2,
                "G030" => 3,
                "G020" => 4,
                _ => 0,
            };
            if (continentalRank > 0 && claims.Any(item =>
                    item.ParticipantId == claim.ParticipantId
                    && item.SeasonIndex == claim.SeasonIndex
                    && (!claim.CompetitionId.HasValue || !item.CompetitionId.HasValue || claim.CompetitionId == item.CompetitionId)
                    && (item.Code switch { "G028" => 1, "G029" => 2, "G030" => 3, "G020" => 4, _ => 0 }) > continentalRank))
            {
                excluded.Add(claim.Id);
            }

            if (claim.Code is "G005" or "G006" or "G007"
                && titles.Any(title => title.ParticipantId == claim.ParticipantId
                    && title.SeasonIndex == claim.SeasonIndex && CouldReferToSameClub(title, claim)))
            {
                excluded.Add(claim.Id);
            }
        }
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            if (item.SourceType == "Achievement" && Guid.TryParseExact(item.SourceKey, "N", out var id) && excluded.Contains(id))
                events[index] = item with { Points = 0 };
        }
        return excluded;
    }

    private static bool CouldReferToSameClub(ClaimInput first, ClaimInput second)
    {
        if (first.ClubId.HasValue && second.ClubId.HasValue)
            return first.ClubId == second.ClubId;
        if (!string.IsNullOrWhiteSpace(first.ClubName) && !string.IsNullOrWhiteSpace(second.ClubName)
            && !string.Equals(Normalize(first.ClubName), Normalize(second.ClubName), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(first.CountryCode) && !string.IsNullOrWhiteSpace(second.CountryCode)
            && !string.Equals(Normalize(first.CountryCode), Normalize(second.CountryCode), StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool SameRecordedClub(ClaimInput first, ClaimInput second)
    {
        if (first.ClubId.HasValue && second.ClubId.HasValue) return first.ClubId == second.ClubId;
        return SameRecordedClub(first.ClubName, first.CountryCode, second.ClubName, second.CountryCode);
    }

    private static IReadOnlyList<ClaimInput> ResolveClaimClubIds(IReadOnlyList<ClaimInput> claims, IReadOnlyList<ClubInput> clubs)
    {
        var identified = clubs.Where(item => item.ClubId.HasValue && !string.IsNullOrWhiteSpace(item.CountryCode))
            .Select(item => (item.ParticipantId, Name: Normalize(item.ClubName).ToUpperInvariant(),
                Country: Normalize(item.CountryCode).ToUpperInvariant(), Id: item.ClubId!.Value))
            .Concat(claims.Where(item => item.ClubId.HasValue && !string.IsNullOrWhiteSpace(item.CountryCode)
                    && !string.IsNullOrWhiteSpace(item.ClubName))
                .Select(item => (item.ParticipantId, Name: Normalize(item.ClubName).ToUpperInvariant(),
                    Country: Normalize(item.CountryCode).ToUpperInvariant(), Id: item.ClubId!.Value)))
            .GroupBy(item => (item.ParticipantId, item.Name, item.Country))
            .Where(group => group.Select(item => item.Id).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Id);
        return claims.Select(item => item.ClubId.HasValue || item.ClubIdentityAmbiguous || string.IsNullOrWhiteSpace(item.CountryCode)
                || string.IsNullOrWhiteSpace(item.ClubName)
            ? item
            : identified.TryGetValue((item.ParticipantId, Normalize(item.ClubName).ToUpperInvariant(),
                    Normalize(item.CountryCode).ToUpperInvariant()), out var clubId)
                ? item with { ClubId = clubId }
                : item).ToArray();
    }

    public static bool SameRecordedClub(string? firstName, string? firstCountry, string? secondName, string? secondCountry) =>
        !string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(secondName)
        && string.Equals(Normalize(firstName), Normalize(secondName), StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(firstCountry) || string.IsNullOrWhiteSpace(secondCountry)
            || string.Equals(Normalize(firstCountry), Normalize(secondCountry), StringComparison.OrdinalIgnoreCase));

    public static string? ValidateCareerMove(ClaimInput claim, IReadOnlyCollection<ClubInput> clubs)
    {
        var own = clubs.Where(item => item.ParticipantId == claim.ParticipantId).ToArray();
        if ((claim.Code == "G037" && string.IsNullOrWhiteSpace(claim.ClubName)) || string.IsNullOrWhiteSpace(claim.CountryCode)
            || string.IsNullOrWhiteSpace(claim.StartingCareerClub) || string.IsNullOrWhiteSpace(claim.StartingCareerCountry))
            return claim.Code == "G037"
                ? "Enter the starting club and country, and the new club and country."
                : "Enter the starting club and country, and the new country.";
        if (own.Length == 0)
            return "Record the starting club and the new club in your career first.";
        var firstSeason = own.Min(item => item.StartSeasonIndex);
        var startingClubs = own.Where(item => item.StartSeasonIndex == firstSeason
            && (claim.StartingCareerClubId.HasValue && item.ClubId.HasValue
                ? claim.StartingCareerClubId == item.ClubId
                : SameIdentity(item.ClubName, item.CountryCode, claim.StartingCareerClub, claim.StartingCareerCountry))).ToArray();
        if (startingClubs.Length == 0)
            return "The starting club must match a club in the earliest recorded career season.";
        if (claim.Code == "G037" && startingClubs.Any(item =>
            claim.ClubId.HasValue && item.ClubId.HasValue
                ? claim.ClubId == item.ClubId
                : SameIdentity(claim.ClubName, claim.CountryCode, item.ClubName, item.CountryCode)))
            return "The starting club does not count as a club move.";
        if (claim.Code == "G041" && string.Equals(Normalize(claim.CountryCode), Normalize(claim.StartingCareerCountry), StringComparison.OrdinalIgnoreCase))
            return "The starting country does not count as a new country.";
        var eligible = claim.Code == "G037"
            ? EligibleClubs(own).Where(item => claim.ClubId.HasValue && item.ClubId.HasValue
                    ? claim.ClubId == item.ClubId
                    : SameIdentity(item.ClubName, item.CountryCode, claim.ClubName, claim.CountryCode))
                .Select(item => item.EligibleSeasonIndex!.Value).Order().ToArray()
            : EligibleCountries(own).Where(item => string.Equals(Normalize(item.CountryCode), Normalize(claim.CountryCode), StringComparison.OrdinalIgnoreCase))
                .Select(item => item.SeasonIndex).Order().ToArray();
        if (eligible.Length == 0 || eligible[0] != claim.SeasonIndex)
            return claim.Code == "G037"
                ? "Record at least 20 matches across the new club's stints and use the season when that milestone was reached."
                : "Record at least 20 matches across your stints in the new country and use the season when that milestone was reached.";
        return null;
    }

    private static bool SameIdentity(string? name, string? country, string? otherName, string? otherCountry) =>
        !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(country)
        && string.Equals(Normalize(name), Normalize(otherName), StringComparison.OrdinalIgnoreCase)
        && string.Equals(Normalize(country), Normalize(otherCountry), StringComparison.OrdinalIgnoreCase);

    private static void ApplyDuels(
        ScoringInput input,
        IReadOnlyList<Guid> participantIds,
        List<ScoreEventOutput> events)
    {
        var playerA = participantIds[0];
        var playerB = participantIds[1];
        var completedA = input.Seasons.Where(item => item.ParticipantId == playerA && item.IsCompleted)
            .ToDictionary(item => item.SeasonIndex);
        var completedB = input.Seasons.Where(item => item.ParticipantId == playerB && item.IsCompleted)
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
                    AddDuel(input, "league-points-per-game", pointsComparison > 0 ? playerA : playerB, seasonIndex, events);
                }

                var goalsComparison = CompareRates(a.GoalsFor, a.LeagueMatches, b.GoalsFor, b.LeagueMatches);
                if (goalsComparison != 0)
                {
                    AddDuel(input, "league-goals-per-game", goalsComparison > 0 ? playerA : playerB, seasonIndex, events);
                }
            }

            if (a.HasUncomparableTransfers || b.HasUncomparableTransfers
                || a.LargestOutgoingTransfer > 0 && b.LargestOutgoingTransfer > 0
                && !string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase))
            {
                var rule = RuleFor(input, "duel:largest-outgoing-transfer", seasonIndex, 4);
                if (rule.IsEnabled)
                    foreach (var player in participantIds)
                        events.Add(new ScoreEventOutput(player, seasonIndex, "Duel", $"largest-outgoing-transfer:{seasonIndex}", rule.Points, "Pending"));
            }
            else if (a.LargestOutgoingTransfer > b.LargestOutgoingTransfer)
            {
                AddDuel(input, "largest-outgoing-transfer", playerA, seasonIndex, events);
            }
            else if (b.LargestOutgoingTransfer > a.LargestOutgoingTransfer)
            {
                AddDuel(input, "largest-outgoing-transfer", playerB, seasonIndex, events);
            }
        }
    }

    private static void AddDuel(
        ScoringInput input,
        string type,
        Guid? winner,
        int seasonIndex,
        List<ScoreEventOutput> events)
    {
        if (winner is null) return;
        var rule = RuleFor(input, $"duel:{type}", seasonIndex, DuelTypes.Single(item => item.Key == type).Points);
        if (rule.IsEnabled)
            events.Add(new ScoreEventOutput(winner.Value, seasonIndex, "Duel", $"{type}:{seasonIndex}", rule.Points, "Confirmed"));
    }

    private static int CompareRates(int aValue, int aMatches, int bValue, int bMatches) =>
        ((long)aValue * bMatches).CompareTo((long)bValue * aMatches);

    private static void ApplyFirstBonuses(
        ScoringInput input,
        IReadOnlyList<ClaimInput> claims,
        IReadOnlyList<SeasonInput> seasons,
        IReadOnlyList<Guid> participantIds,
        List<ScoreEventOutput> events)
    {
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

            var firstSeason = hits[0].SeasonIndex!.Value;
            var rule = RuleFor(input, $"bonus:{bonus.Key}", firstSeason, bonus.Points);
            if (!rule.IsEnabled) continue;
            var points = rule.Points;
            var firstHits = hits.Where(item => item.SeasonIndex == firstSeason).ToArray();
            var canConfirm = participantIds.All(id => seasons.Any(item =>
                item.ParticipantId == id && item.SeasonIndex == firstSeason && item.IsCompleted));
            if (firstHits.Length > 1)
            {
                var sharedPoints = points / 2;
                foreach (var hit in firstHits)
                {
                    events.Add(new ScoreEventOutput(hit.ParticipantId, firstSeason, "FirstBonus", bonus.Key, sharedPoints, canConfirm ? "Confirmed" : "Pending"));
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
        ScoringInput input,
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
            var rule = RuleFor(input, $"checkpoint-{threshold}", firstSeason, defaultPoints);
            if (!rule.IsEnabled) continue;
            var points = rule.Points;
            var comparisonIsSettled = !events.Any(item => item.Status == "Pending" && item.SeasonIndex <= firstSeason);

            if (sameSeason.Length == 2)
            {
                var aScore = EligibleScoreThrough(participantIds[0], firstSeason, seasonTotals);
                var bScore = EligibleScoreThrough(participantIds[1], firstSeason, seasonTotals);
                if (aScore == bScore)
                {
                    var sharedPoints = points / 2;
                    generated.Add(new ScoreEventOutput(participantIds[0], firstSeason, "Checkpoint", awardKey, sharedPoints, comparisonIsSettled ? "Confirmed" : "Pending"));
                    generated.Add(new ScoreEventOutput(participantIds[1], firstSeason, "Checkpoint", awardKey, sharedPoints, comparisonIsSettled ? "Confirmed" : "Pending"));
                }
                else
                {
                    var winner = aScore > bScore ? participantIds[0] : participantIds[1];
                    generated.Add(new ScoreEventOutput(winner, firstSeason, "Checkpoint", awardKey, points, comparisonIsSettled ? "Confirmed" : "Pending"));
                }
                continue;
            }

            var firstParticipant = sameSeason[0].Key;
            var other = participantIds.Single(id => id != firstParticipant);
            var hasCompletedComparableSeason = orderedSeasons[other].Contains(firstSeason);
            generated.Add(new ScoreEventOutput(
                firstParticipant,
                firstSeason,
                "Checkpoint",
                awardKey,
                points,
                hasCompletedComparableSeason && comparisonIsSettled ? "Confirmed" : "Pending"));
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
        var candidates = new List<(Guid Id, int Season)>();
        foreach (var participantId in participantIds)
        {
            if (eligibleClubCounts.GetValueOrDefault(participantId) < 3) continue;
            var cumulative = 0;
            var indices = events.Where(item => item.ParticipantId == participantId)
                .Select(item => item.SeasonIndex)
                .Concat(input.Seasons.Where(item => item.ParticipantId == participantId).Select(item => item.SeasonIndex))
                .Concat(input.Clubs.Where(item => item.ParticipantId == participantId && item.EligibleSeasonIndex.HasValue)
                    .Select(item => item.EligibleSeasonIndex!.Value))
                .Distinct().Order();
            foreach (var index in indices)
            {
                cumulative += confirmedBySeason.GetValueOrDefault((participantId, index));
                if (cumulative < input.TargetPoints) continue;
                var eligibleClubsAtSeason = input.Clubs
                    .Where(item => item.ParticipantId == participantId && item.OfficialMatches >= 20
                        && item.EligibleSeasonIndex.HasValue && item.EligibleSeasonIndex.Value <= index)
                    .Select(item => (item.ClubId, item.ClubName, item.CountryCode, item.EligibleSeasonIndex!.Value));
                if (DistinctClubMilestoneSeasons(eligibleClubsAtSeason).Length < 3) continue;
                candidates.Add((participantId, index));
                break;
            }
        }

        if (candidates.Count == 0) return (null, null, false);
        var earliest = candidates.Min(item => item.Season);
        if (participantIds.Count != 2 || participantIds.Any(participantId =>
                !input.Seasons.Any(season => season.ParticipantId == participantId
                    && season.SeasonIndex == earliest && season.IsCompleted)))
        {
            return (null, earliest, true);
        }
        var sameSeason = candidates.Where(item => item.Season == earliest).ToArray();
        if (events.Any(item => item.Status == "Pending" && item.SeasonIndex <= earliest))
            return (null, earliest, true);
        if (sameSeason.Length == 1) return (sameSeason[0].Id, earliest, false);
        var comparableSeasons = input.Seasons
            .Where(item => item.IsCompleted && item.SeasonIndex >= earliest && participantIds.Contains(item.ParticipantId))
            .GroupBy(item => item.SeasonIndex)
            .Where(group => group.Select(item => item.ParticipantId).Distinct().Count() == participantIds.Count)
            .Select(group => group.Key).Order();
        foreach (var seasonIndex in comparableSeasons)
        {
            if (events.Any(item => item.Status == "Pending" && item.SeasonIndex <= seasonIndex))
                return (null, seasonIndex, true);
            var ranking = sameSeason.Select(candidate => new
            {
                candidate.Id,
                Score = confirmedBySeason.Where(item => item.Key.ParticipantId == candidate.Id && item.Key.SeasonIndex <= seasonIndex)
                    .Sum(item => item.Value),
                Trophies = claims.Count(claim => claim.ParticipantId == candidate.Id && claim.SeasonIndex <= seasonIndex
                    && MajorTrophyCodes.Contains(claim.Code)),
            }).OrderByDescending(item => item.Score).ThenByDescending(item => item.Trophies).ToArray();
            if (ranking[0].Score != ranking[1].Score || ranking[0].Trophies != ranking[1].Trophies)
                return (ranking[0].Id, seasonIndex, false);
        }
        return (null, earliest, true);
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
    public static IReadOnlyList<ClubInput> EligibleClubs(IEnumerable<ClubInput> stints)
    {
        var result = new List<ClubInput>();
        foreach (var participant in ResolveCareerClubIds(stints).Where(item => !string.IsNullOrWhiteSpace(item.ClubName)).GroupBy(item => item.ParticipantId))
        {
            foreach (var club in participant.GroupBy(ClubIdentity, StringComparer.OrdinalIgnoreCase))
            {
                var records = club.ToArray();
                if (HasCompleteSeasonMatches(records))
                {
                    var season = DetailedMilestoneSeason(records);
                    if (season.HasValue)
                    {
                        var stint = records.First(item => item.SeasonMatches!.Any(match => match.SeasonIndex == season && match.OfficialMatches > 0));
                        result.Add(stint with { OfficialMatches = 20, EligibleSeasonIndex = season });
                    }
                    continue;
                }
                var milestone = records.Where(item => QualifiesClubMilestone(item, records))
                    .OrderBy(item => item.EligibleSeasonIndex).FirstOrDefault();
                if (milestone is not null) result.Add(milestone with { OfficialMatches = 20 });
            }
        }
        return result;
    }
    public static bool QualifiesClubMilestone(ClubInput milestone, IEnumerable<ClubInput> stints)
    {
        var resolved = ResolveCareerClubIds(stints.Append(milestone));
        milestone = resolved[^1];
        var records = resolved[..^1].Where(stint => stint.ParticipantId == milestone.ParticipantId
            && string.Equals(ClubIdentity(stint), ClubIdentity(milestone), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (HasCompleteSeasonMatches(records))
            return milestone.EligibleSeasonIndex.HasValue && DetailedMilestoneSeason(records) is int reached
                && reached <= milestone.EligibleSeasonIndex
                && (milestone.SeasonMatches?.Any(item => item.SeasonIndex == milestone.EligibleSeasonIndex && item.OfficialMatches > 0) ?? false);
        return milestone.OfficialMatches > 0 && milestone.EligibleSeasonIndex.HasValue
        && milestone.StartSeasonIndex <= milestone.EligibleSeasonIndex
        && (!milestone.EndSeasonIndex.HasValue || milestone.EligibleSeasonIndex <= milestone.EndSeasonIndex)
        && records.Where(stint => stint.StartSeasonIndex <= milestone.EligibleSeasonIndex)
            .Sum(stint => (long)stint.OfficialMatches) >= 20;
    }

    public static ClubInput[] ResolveCareerClubIds(IEnumerable<ClubInput> stints)
    {
        var records = stints.ToArray();
        var identified = records.Where(item => item.ClubId.HasValue && !string.IsNullOrWhiteSpace(item.CountryCode))
            .GroupBy(item => (item.ParticipantId, Name: Normalize(item.ClubName).ToUpperInvariant(),
                Country: Normalize(item.CountryCode).ToUpperInvariant()))
            .Where(group => group.Select(item => item.ClubId).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().ClubId!.Value);
        return records.Select(item => item.ClubId.HasValue || string.IsNullOrWhiteSpace(item.CountryCode)
            ? item
            : identified.TryGetValue((item.ParticipantId, Normalize(item.ClubName).ToUpperInvariant(),
                    Normalize(item.CountryCode).ToUpperInvariant()), out var clubId)
                ? item with { ClubId = clubId }
                : item).ToArray();
    }

    public static bool QualifiesCountryMilestone(ClubInput milestone, IEnumerable<ClubInput> stints)
    {
        if (string.IsNullOrWhiteSpace(milestone.CountryCode)) return false;
        var records = stints.Where(stint => stint.ParticipantId == milestone.ParticipantId
            && string.Equals(Normalize(stint.CountryCode), Normalize(milestone.CountryCode), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (HasCompleteSeasonMatches(records))
            return milestone.CountryEligibleSeasonIndex.HasValue && DetailedMilestoneSeason(records) is int reached
                && reached <= milestone.CountryEligibleSeasonIndex
                && (milestone.SeasonMatches?.Any(item => item.SeasonIndex == milestone.CountryEligibleSeasonIndex && item.OfficialMatches > 0) ?? false);
        return milestone.OfficialMatches > 0
        && milestone.CountryEligibleSeasonIndex.HasValue
        && milestone.StartSeasonIndex <= milestone.CountryEligibleSeasonIndex
        && (!milestone.EndSeasonIndex.HasValue || milestone.CountryEligibleSeasonIndex <= milestone.EndSeasonIndex)
        && records.Where(stint => stint.StartSeasonIndex <= milestone.CountryEligibleSeasonIndex)
            .Sum(stint => (long)stint.OfficialMatches) >= 20;
    }

    private static bool HasCompleteSeasonMatches(IReadOnlyCollection<ClubInput> records) =>
        records.Count > 0 && records.All(stint => stint.SeasonMatches is not null
            && stint.SeasonMatches.All(item => item.OfficialMatches >= 0 && item.SeasonIndex >= stint.StartSeasonIndex
                && item.SeasonIndex <= (stint.EndSeasonIndex ?? 100))
            && stint.SeasonMatches.Select(item => item.SeasonIndex).Distinct().Count() == stint.SeasonMatches.Count
            && stint.SeasonMatches.Sum(item => (long)item.OfficialMatches) == stint.OfficialMatches);

    private static int? DetailedMilestoneSeason(IReadOnlyCollection<ClubInput> records)
    {
        var total = 0L;
        foreach (var season in records.SelectMany(item => item.SeasonMatches!).GroupBy(item => item.SeasonIndex).OrderBy(item => item.Key))
        {
            total += season.Sum(item => (long)item.OfficialMatches);
            if (total >= 20) return season.Key;
        }
        return null;
    }

    public static IReadOnlyList<CountryMilestoneInput> EligibleCountries(IEnumerable<ClubInput> stints)
    {
        var records = stints.ToArray();
        var clubMilestones = EligibleClubs(records).Where(item => !string.IsNullOrWhiteSpace(item.CountryCode)).ToArray();
        var result = new List<CountryMilestoneInput>();
        foreach (var participant in records.Where(item => !string.IsNullOrWhiteSpace(item.CountryCode)).GroupBy(item => item.ParticipantId))
        {
            foreach (var country in participant.GroupBy(item => Normalize(item.CountryCode), StringComparer.OrdinalIgnoreCase))
            {
                var countryRecords = country.ToArray();
                if (HasCompleteSeasonMatches(countryRecords))
                {
                    var season = DetailedMilestoneSeason(countryRecords);
                    if (season.HasValue) result.Add(new CountryMilestoneInput(participant.Key, country.Key, season.Value));
                    continue;
                }
                var seasons = countryRecords.Where(item => QualifiesCountryMilestone(item, countryRecords))
                    .Select(item => item.CountryEligibleSeasonIndex!.Value)
                    .Concat(clubMilestones.Where(item => item.ParticipantId == participant.Key
                        && string.Equals(Normalize(item.CountryCode), country.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.EligibleSeasonIndex!.Value)).Order().ToArray();
                if (seasons.Length > 0) result.Add(new CountryMilestoneInput(participant.Key, country.Key, seasons[0]));
            }
        }
        return result;
    }
    public static bool QualifiesNationalTeamMilestone(int? season, IEnumerable<NationalTeamStint> stints)
    {
        if (season is null) return false;
        var teams = stints.ToArray();
        if (NationalTeamSeasonMatchesComplete(teams))
            return CalculatedNationalTeamMilestone(teams) == season;
        return teams.Any(item => item.OfficialMatches > 0 && item.StartSeasonIndex <= season
                && (item.EndSeasonIndex is null || item.EndSeasonIndex >= season))
            && teams.Where(item => item.StartSeasonIndex <= season).Sum(item => (long)item.OfficialMatches) >= 20;
    }
    public static bool NationalTeamSeasonMatchesComplete(IEnumerable<NationalTeamStint> stints) =>
        stints.All(item => item.SeasonMatches.Sum(match => (long)match.OfficialMatches) == item.OfficialMatches);

    public static int? CalculatedNationalTeamMilestone(IEnumerable<NationalTeamStint> stints)
    {
        var teams = stints.ToArray();
        if (!NationalTeamSeasonMatchesComplete(teams)) return null;
        long total = 0;
        foreach (var group in teams.SelectMany(item => item.SeasonMatches).GroupBy(item => item.SeasonIndex).OrderBy(item => item.Key))
        {
            total += group.Sum(item => (long)item.OfficialMatches);
            if (total >= 20) return group.Key;
        }
        return null;
    }

    public static int? NationalTeamMilestone(int? attestedSeason, IEnumerable<NationalTeamStint> stints)
    {
        var teams = stints.ToArray();
        return NationalTeamSeasonMatchesComplete(teams)
            ? CalculatedNationalTeamMilestone(teams)
            : QualifiesNationalTeamMilestone(attestedSeason, teams) ? attestedSeason : null;
    }
    public static bool QualifiesThreeContinentsMilestone(int? season, IEnumerable<GeographicStintInput> stints,
        IReadOnlyDictionary<string, string> countryContinents)
    {
        if (season is null) return false;
        var all = stints.ToArray();
        if (GeographicSeasonMatchesComplete(all, countryContinents))
            return CalculatedThreeContinentsMilestone(all, countryContinents) == season;
        var records = all.Where(item => item.OfficialMatches > 0 && item.StartSeasonIndex <= season
            && !string.IsNullOrWhiteSpace(item.CountryCode) && countryContinents.ContainsKey(Normalize(item.CountryCode))).ToArray();
        return records.Any(item => item.EndSeasonIndex is null || item.EndSeasonIndex >= season)
            && records.Select(item => countryContinents[Normalize(item.CountryCode)]).Distinct(StringComparer.Ordinal).Count() >= 3;
    }
    public static bool GeographicSeasonMatchesComplete(IEnumerable<GeographicStintInput> stints,
        IReadOnlyDictionary<string, string> countryContinents) =>
        stints.All(item => (item.SeasonMatches?.Sum(match => (long)match.OfficialMatches) ?? 0) == item.OfficialMatches
            && (item.OfficialMatches == 0 || !string.IsNullOrWhiteSpace(item.CountryCode)
                && countryContinents.ContainsKey(Normalize(item.CountryCode))));

    public static int? CalculatedThreeContinentsMilestone(IEnumerable<GeographicStintInput> stints,
        IReadOnlyDictionary<string, string> countryContinents)
    {
        var all = stints.ToArray();
        if (!GeographicSeasonMatchesComplete(all, countryContinents)) return null;
        var records = all.Where(item => item.OfficialMatches > 0)
            .SelectMany(item => item.SeasonMatches!.Where(match => match.OfficialMatches > 0)
                .Select(match => (match.SeasonIndex, Continent: countryContinents[Normalize(item.CountryCode)])));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in records.GroupBy(item => item.SeasonIndex).OrderBy(item => item.Key))
        {
            foreach (var item in group) seen.Add(item.Continent);
            if (seen.Count >= 3) return group.Key;
        }
        return null;
    }

    public static int? ThreeContinentsMilestone(int? attestedSeason, IEnumerable<GeographicStintInput> stints,
        IReadOnlyDictionary<string, string> countryContinents)
    {
        var all = stints.ToArray();
        return GeographicSeasonMatchesComplete(all, countryContinents)
            ? CalculatedThreeContinentsMilestone(all, countryContinents)
            : QualifiesThreeContinentsMilestone(attestedSeason, all, countryContinents) ? attestedSeason : null;
    }

    public static GeographicStintInput[] GeographicStints(IEnumerable<ClubInput> clubs, IEnumerable<NationalTeamStint> teams) =>
        clubs.Select(item => new GeographicStintInput(item.CountryCode, item.OfficialMatches, item.StartSeasonIndex,
                item.EndSeasonIndex, item.SeasonMatches))
            .Concat(teams.Select(item => new GeographicStintInput(item.CountryCode, item.OfficialMatches, item.StartSeasonIndex,
                item.EndSeasonIndex, item.SeasonMatches.Select(match => new SeasonMatchInput(match.SeasonIndex, match.OfficialMatches)).ToArray())))
            .ToArray();
    public static bool SameCareerClub(ClubInput first, ClubInput second) =>
        string.Equals(ClubIdentity(first), ClubIdentity(second), StringComparison.OrdinalIgnoreCase);

    private static string ClubIdentity(ClubInput club) => club.ClubId.HasValue
        ? $"id:{club.ClubId.Value:N}"
        : $"text:{Normalize(club.CountryCode)}\u001f{Normalize(club.ClubName)}";
    private static RuleVersionInput? VersionFor(ScoringInput input, int seasonIndex) =>
        input.RuleVersions?.Where(version => version.EffectiveSeasonIndex <= seasonIndex)
            .OrderByDescending(version => version.VersionNumber).FirstOrDefault();

    private static RuleSetting RuleFor(ScoringInput input, string code, int seasonIndex, int defaultPoints)
    {
        var version = VersionFor(input, seasonIndex);
        if (version is not null && version.Rules.TryGetValue(code, out var rule)) return rule;
        if (version is not null && code.StartsWith("C", StringComparison.Ordinal)) return new RuleSetting(0, false);
        var points = input.RulePoints.GetValueOrDefault(code, defaultPoints);
        return new RuleSetting(points, points > 0);
    }
}
