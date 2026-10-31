using FmCareerChallenge.Api.Domain;

namespace FmCareerChallenge.UnitTests;

public class RaceScoringEngineTests
{
    private static readonly Guid PlayerA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid PlayerB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly IReadOnlyDictionary<string, int> Rules = AchievementCatalog.All
        .ToDictionary(item => item.Code, item => item.DefaultPoints);

    [Fact]
    public void NormalAchievementAwardsItsConfiguredBasePoints()
    {
        var result = Calculate([Claim(PlayerA, "G008", 0)]);

        Assert.Equal(10, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Theory]
    [InlineData("G003", 8)]
    [InlineData("G004", 5)]
    [InlineData("G006", 5)]
    [InlineData("G007", 3)]
    [InlineData("G010", 3)]
    [InlineData("G014", 10)]
    [InlineData("G015", 8)]
    [InlineData("G017", 8)]
    [InlineData("G018", 12)]
    [InlineData("G019", 10)]
    [InlineData("G021", 20)]
    [InlineData("G022", 15)]
    [InlineData("G023", 4)]
    [InlineData("G024", 12)]
    [InlineData("G026", 2)]
    [InlineData("G027", 4)]
    [InlineData("G031", 7)]
    [InlineData("G032", 10)]
    [InlineData("G034", 25)]
    [InlineData("G035", 18)]
    [InlineData("G036", 10)]
    [InlineData("G051", 4)]
    public void RemainingOfficialObjectivesAwardDefaultPointsAndRespectDisabledRules(string code, int points)
    {
        var claim = Claim(PlayerA, code, 0) with { ClubName = "Test Club", CountryCode = "PT" };
        var result = Calculate([claim]);
        var scoreEvent = Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N"));
        Assert.Equal(points, scoreEvent.Points);

        var disabled = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA], [claim], [], [], Rules,
            [new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { [code] = new(0, false) })]));
        Assert.DoesNotContain(disabled.Events, item => item.SourceKey == claim.Id.ToString("N"));
    }

    [Fact]
    public void FrequencyVersionsUseEarlierAcceptedClaimsAcrossRuleBoundaries()
    {
        var claims = new[]
        {
            Claim(PlayerA, "G008", 0) with { ClubName = "A", CountryCode = "PT" },
            Claim(PlayerA, "G008", 1) with { ClubName = "A", CountryCode = "PT" },
            Claim(PlayerA, "G008", 2) with { ClubName = "B", CountryCode = "PT" },
            Claim(PlayerA, "G008", 3) with { ClubName = "C", CountryCode = "PT" },
            Claim(PlayerA, "G008", 4) with { ClubName = "C", CountryCode = "PT" },
        };
        var versions = new[]
        {
            new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G008"] = new(10, true) }),
            new RuleVersionInput(2, 1, new Dictionary<string, RuleSetting> { ["G008"] = new(10, true, FrequencyOverride: AchievementFrequency.PerClub) }),
            new RuleVersionInput(3, 3, new Dictionary<string, RuleSetting> { ["G008"] = new(10, true, FrequencyOverride: AchievementFrequency.Once) }),
            new RuleVersionInput(4, 4, new Dictionary<string, RuleSetting> { ["G008"] = new(10, true, FrequencyOverride: AchievementFrequency.Season) }),
        };
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA], claims, [], [], Rules, versions);
        var events = RaceScoringEngine.Calculate(input).Events.Where(item => item.SourceType == "Achievement").ToArray();
        Assert.Equal(new[] { 0, 2, 4 }, events.Select(item => item.SeasonIndex));
        Assert.Equal(new[] { 1, 2, 4 }, events.Select(item => item.RuleVersion));
        versions[0] = versions[0] with { Rules = new Dictionary<string, RuleSetting> { ["G008"] = new(10, true, FrequencyOverride: AchievementFrequency.Once) } };
        versions[1] = versions[1] with { Rules = versions[0].Rules };
        versions[2] = versions[2] with { Rules = versions[0].Rules };
        versions[3] = versions[3] with { Rules = versions[0].Rules };
        Assert.Single(RaceScoringEngine.Calculate(input).Events, item => item.SourceType == "Achievement");
    }

    [Fact]
    public void CupMinimumRecalculatesWithoutConsumingFrequencyOrFirstBonus()
    {
        var first = Claim(PlayerA, "G008", 0) with { ClubName = "A", CountryCode = "PT", ManagedCompetitionMatches = 2 };
        var later = Claim(PlayerA, "G008", 1) with { ClubName = "A", CountryCode = "PT", ManagedCompetitionMatches = 4 };
        var versions = new[]
        {
            new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting>
            {
                ["G008"] = new(10, true, FrequencyOverride: AchievementFrequency.PerClub, MinimumCupMatchesOverride: 3),
            }),
        };
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [first, later],
            [Season(PlayerA, 1, true), Season(PlayerB, 1, true)], [], Rules, versions);
        var result = RaceScoringEngine.Calculate(input);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == first.Id.ToString("N")).Points);
        Assert.Equal(10, Assert.Single(result.Events, item => item.SourceKey == later.Id.ToString("N")).Points);
        Assert.Equal(1, Assert.Single(result.Events, item => item.SourceKey.StartsWith("first-major-national-cup")).SeasonIndex);
        var legacy = first with { ManagedCompetitionMatches = null };
        result = RaceScoringEngine.Calculate(input with { Claims = [legacy, later] });
        Assert.Equal(10, Assert.Single(result.Events, item => item.SourceKey == legacy.Id.ToString("N")).Points);
        Assert.DoesNotContain(result.Events, item => item.SourceKey == later.Id.ToString("N"));
    }

    [Fact]
    public void FirstMajorClubTrophyOnlyConflictsWithEarlierAttestedMatchingClubTrophies()
    {
        var claim = Claim(PlayerA, "G040", 2) with { ClubName = "United", CountryCode = "PT", FirstMajorClubTrophy = true };
        var trophy = Claim(PlayerA, "G008", 1) with { ClubName = "United", CountryCode = "PT", HasAttestedClubTrophy = true };
        Assert.NotNull(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy]));
        Assert.Null(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy with { SeasonIndex = 2 }]));
        Assert.Null(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy with { CountryCode = "ES" }]));
        Assert.Null(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy with { CountryCode = null }]));
        Assert.Null(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy with { HasAttestedClubTrophy = false }]));
        Assert.Null(RaceScoringEngine.ValidateFirstMajorClubTrophy(claim, [trophy with { ParticipantId = PlayerB }]));
        var result = Calculate([claim, trophy]);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        result = Calculate([claim with { FirstMajorClubTrophy = null }, trophy]);
        Assert.Equal(5, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
    }

    [Fact]
    public void PerClubFrequencyTreatsAmbiguousTextConservativelyUntilExplicitlyLinked()
    {
        var unresolved = Claim(PlayerA, "G040", 0) with { ClubName = "Shared Club", CountryCode = "PT" };
        var ambiguous = unresolved with { ClubIdentityAmbiguous = true };
        var firstIdentity = unresolved with { ClubId = Guid.NewGuid() };
        var secondIdentity = unresolved with { ClubId = Guid.NewGuid() };

        Assert.True(RaceScoringEngine.FrequencyMatches(AchievementFrequency.PerClub, ambiguous, unresolved));
        Assert.True(RaceScoringEngine.FrequencyMatches(AchievementFrequency.PerClub, ambiguous, firstIdentity));
        Assert.False(RaceScoringEngine.FrequencyMatches(AchievementFrequency.PerClub, firstIdentity, secondIdentity));
        Assert.True(RaceScoringEngine.FrequencyMatches(AchievementFrequency.PerClub,
            unresolved with { CountryCode = null }, firstIdentity));
    }

    [Fact]
    public void AmbiguousLegacyTitleDoesNotBecomeAnExtraClubAfterCatalogChanges()
    {
        var firstId = Guid.NewGuid();
        var first = Claim(PlayerA, "G001", 0) with { ClubName = "Shared Club", CountryCode = "PT", ClubId = firstId };
        var second = Claim(PlayerA, "G001", 1) with { ClubName = "Shared Club", CountryCode = "PT", ClubId = firstId };
        var before = Calculate([first, second]);
        var ambiguous = first with { ClubId = null, ClubIdentityAmbiguous = true };
        var after = Calculate([ambiguous, second]);
        Assert.DoesNotContain(before.Events, item => item.SourceKey == "automatic:G049");
        Assert.DoesNotContain(after.Events, item => item.SourceKey == "automatic:G049");
        Assert.Equal(before.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints,
            after.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);

        var distinct = Claim(PlayerA, "G001", 2) with
        {
            ClubName = "Shared Club", CountryCode = "PT", ClubId = Guid.NewGuid(),
        };
        var withDistinct = Calculate([ambiguous, second, distinct]);
        Assert.Equal(2, Assert.Single(withDistinct.Events, item => item.SourceKey == "automatic:G049").SeasonIndex);
    }

    [Fact]
    public void AmbiguousLegacyStartingClubDoesNotInvalidateAnExistingMove()
    {
        var startId = Guid.NewGuid();
        ClubInput[] clubs = [new(PlayerA, "Start", 20, 0, "PT", ClubId: startId),
            new(PlayerA, "New", 20, 1, "ES", StartSeasonIndex: 1)];
        var claim = Claim(PlayerA, "G037", 1) with
        {
            ClubName = "New", CountryCode = "ES", StartingCareerClub = "Start",
            StartingCareerCountry = "PT", StartingCareerClubId = startId,
        };
        Assert.Null(RaceScoringEngine.ValidateCareerMove(claim, clubs));
        Assert.Null(RaceScoringEngine.ValidateCareerMove(claim with
        {
            StartingCareerClubId = null,
        }, clubs));
    }

    [Fact]
    public void DomesticDoubleCrossChecksKnownSameSeasonClubAndCountryRecordsButPreservesLegacyClaims()
    {
        var domesticDouble = Claim(PlayerA, "G013", 1) with
        {
            ClubName = "United",
            CountryCode = "PT",
            WonFirstDivisionTitle = true,
            WonMajorNationalCup = true,
        };
        var title = Claim(PlayerA, "G001", 1) with { ClubName = "United", CountryCode = "PT" };
        var cup = Claim(PlayerA, "G008", 1) with { ClubName = "United", CountryCode = "PT" };

        Assert.Null(RaceScoringEngine.ValidateDomesticDoubleConsistency(domesticDouble, [title, cup]));
        var consistent = Calculate([domesticDouble, title, cup]);
        Assert.Equal(7, Assert.Single(consistent.Events, item => item.SourceKey == domesticDouble.Id.ToString("N")).Points);

        Assert.NotNull(RaceScoringEngine.ValidateDomesticDoubleConsistency(domesticDouble, [title, cup with { ClubName = "City" }]));
        var contradictory = Calculate([domesticDouble, title, cup with { ClubName = "City" }]);
        Assert.Equal(0, Assert.Single(contradictory.Events, item => item.SourceKey == domesticDouble.Id.ToString("N")).Points);

        Assert.NotNull(RaceScoringEngine.ValidateDomesticDoubleConsistency(domesticDouble, [title, cup with { CountryCode = "ES" }]));
        var distinctCatalogClub = cup with { ClubId = Guid.NewGuid(), ClubName = "United", CountryCode = "PT" };
        Assert.NotNull(RaceScoringEngine.ValidateDomesticDoubleConsistency(
            domesticDouble with { ClubId = Guid.NewGuid() }, [title, distinctCatalogClub]));

        var legacy = domesticDouble with { WonFirstDivisionTitle = null, WonMajorNationalCup = null };
        var legacyResult = Calculate([legacy, title, cup with { ClubName = "City" }]);
        Assert.Equal(7, Assert.Single(legacyResult.Events, item => item.SourceKey == legacy.Id.ToString("N")).Points);
        Assert.Null(RaceScoringEngine.ValidateDomesticDoubleConsistency(domesticDouble, []));
    }

    [Fact]
    public void FirstDivisionEvidenceOnlyConflictsWithEarlierOwnFirstDivisionStarts()
    {
        var claim = Claim(PlayerA, "G038", 2) with { FirstDivisionExperience = true };
        var earlier = new ClubInput(PlayerA, "Club", 0, null, StartSeasonIndex: 1, InitialDivisionLevel: 1);
        Assert.NotNull(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [earlier]));
        Assert.Null(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [earlier with { StartSeasonIndex = 2 }]));
        Assert.Null(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [earlier with { InitialDivisionLevel = 2 }]));
        Assert.Null(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [earlier with { ParticipantId = PlayerB }]));
        var detailed = earlier with { InitialDivisionLevel = 3, SeasonMatches = [new(1, 1, 1)] };
        Assert.NotNull(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [detailed]));
        Assert.Null(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [detailed with { SeasonMatches = [new(1, 0, 1)] }]));
        Assert.Null(RaceScoringEngine.ValidateFirstDivisionExperience(claim, [detailed with { SeasonMatches = [new(2, 1, 1)] }]));
        var result = Calculate([claim], clubs: [earlier]);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var legacy = claim with { FirstDivisionExperience = null };
        result = Calculate([legacy], clubs: [earlier]);
        Assert.Equal(6, Assert.Single(result.Events, item => item.SourceKey == legacy.Id.ToString("N")).Points);
    }

    [Fact]
    public void HigherDivisionEvidenceOnlyConflictsWithEarlierBetterOwnDivisionStarts()
    {
        var claim = Claim(PlayerA, "G039", 2) with { HigherDivisionClubMove = true, PreviousBestDivisionLevel = 3 };
        var earlier = new ClubInput(PlayerA, "Club", 0, null, StartSeasonIndex: 1, InitialDivisionLevel: 2);
        Assert.NotNull(RaceScoringEngine.ValidateHigherDivisionMove(claim, [earlier]));
        Assert.Null(RaceScoringEngine.ValidateHigherDivisionMove(claim, [earlier with { StartSeasonIndex = 2 }]));
        Assert.Null(RaceScoringEngine.ValidateHigherDivisionMove(claim, [earlier with { InitialDivisionLevel = 3 }]));
        Assert.Null(RaceScoringEngine.ValidateHigherDivisionMove(claim, [earlier with { ParticipantId = PlayerB }]));
        var detailed = earlier with { InitialDivisionLevel = 3, SeasonMatches = [new(1, 1, 2)] };
        Assert.NotNull(RaceScoringEngine.ValidateHigherDivisionMove(claim, [detailed]));
        Assert.Null(RaceScoringEngine.ValidateHigherDivisionMove(claim, [detailed with { SeasonMatches = [new(1, 0, 2)] }]));
        Assert.Null(RaceScoringEngine.ValidateHigherDivisionMove(claim, [detailed with { SeasonMatches = [new(2, 1, 2)] }]));
        var result = Calculate([claim], clubs: [earlier]);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var legacy = claim with { HigherDivisionClubMove = null };
        result = Calculate([legacy], clubs: [earlier]);
        Assert.Equal(4, Assert.Single(result.Events, item => item.SourceKey == legacy.Id.ToString("N")).Points);
    }

    [Fact]
    public void ThirdToFirstContinuityRejectsOnlyProvenInterveningClubMatches()
    {
        var claim = Claim(PlayerA, "G050", 4) with { ClubName = "Home", CountryCode = "PT",
            ThirdDivisionStartSeason = 0, StayedWithClubFromThirdToFirst = true };
        var other = new ClubInput(PlayerA, "Away", 5, null, CountryCode: "PT", StartSeasonIndex: 1, EndSeasonIndex: 2);
        Assert.NotNull(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other with { ClubName = "Home" }]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other with { ParticipantId = PlayerB }]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other with { OfficialMatches = 0 }]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other with { StartSeasonIndex = 0 }]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [other with { EndSeasonIndex = 4 }]));
        var detailed = other with { EndSeasonIndex = null, SeasonMatches = [new(2, 1)] };
        Assert.NotNull(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [detailed]));
        Assert.Null(RaceScoringEngine.ValidateThirdToFirstContinuity(claim, [detailed with { SeasonMatches = [new(4, 1)] }]));
        var result = Calculate([claim], clubs: [other]);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var legacy = claim with { StayedWithClubFromThirdToFirst = null };
        result = Calculate([legacy], clubs: [other]);
        Assert.Equal(12, Assert.Single(result.Events, item => item.SourceKey == legacy.Id.ToString("N")).Points);
        var later = claim with { Id = Guid.NewGuid(), ThirdDivisionStartSeason = 3, SeasonIndex = 5 };
        result = Calculate([claim, later], clubs: [other]);
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        Assert.Equal(12, Assert.Single(result.Events, item => item.SourceKey == later.Id.ToString("N")).Points);
    }

    [Fact]
    public void FirstBonusRemainsPendingUntilOpponentCompletesComparableSeason()
    {
        var result = Calculate(
            [Claim(PlayerA, "G008", 1)],
            [Season(PlayerA, 1, true), Season(PlayerB, 0, true)]);

        var bonus = Assert.Single(result.Events, item => item.SourceType == "FirstBonus");
        Assert.Equal("Pending", bonus.Status);
        Assert.Equal(5, bonus.Points);
        Assert.Equal(10, result.Participants.Single(item => item.ParticipantId == PlayerA).ConfirmedPoints);
    }

    [Fact]
    public void FirstBonusIsSplitForTheSameSeason()
    {
        var result = Calculate(
            [Claim(PlayerA, "G001", 0), Claim(PlayerB, "G001", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true)]);

        var bonuses = result.Events.Where(item => item.SourceType == "FirstBonus").ToArray();
        Assert.Equal(2, bonuses.Length);
        Assert.All(bonuses, bonus =>
        {
            Assert.Equal(4, bonus.Points);
            Assert.Equal("Confirmed", bonus.Status);
        });
    }

    [Fact]
    public void SameSeasonFirstBonusRemainsPendingUntilBothSeasonsAreCompleted()
    {
        var result = Calculate(
            [Claim(PlayerA, "G001", 0), Claim(PlayerB, "G001", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, false)]);

        var bonuses = result.Events.Where(item => item.SourceType == "FirstBonus").ToArray();
        Assert.Equal(2, bonuses.Length);
        Assert.All(bonuses, bonus => Assert.Equal("Pending", bonus.Status));
    }

    [Fact]
    public void SupplementalRulesUseTheirEffectiveSeasonAndCanBeDisabled()
    {
        var defaults = RaceScoringEngine.WithSupplementalDefaults(
            Rules.Select(item => new KeyValuePair<string, RuleSetting>(item.Key, new RuleSetting(item.Value, true))));
        var updated = new Dictionary<string, RuleSetting>(defaults)
        {
            ["bonus:first-major-national-cup"] = new(9, true),
            ["duel:league-points-per-game"] = new(11, true),
            ["duel:league-goals-per-game"] = new(3, false),
            ["duel:largest-outgoing-transfer"] = new(4, false),
            ["checkpoint-50"] = new(13, true),
        };
        ClaimInput[] claims = [Claim(PlayerA, "G001", 1), Claim(PlayerA, "G008", 1), Claim(PlayerA, "G020", 1)];
        SeasonInput[] seasons =
        [
            new(PlayerA, 0, true, 20, 45, 50, 0),
            new(PlayerB, 0, true, 20, 20, 20, 0),
            new(PlayerA, 1, true, 20, 45, 50, 100, "EUR"),
            new(PlayerB, 1, true, 20, 20, 20, 200, "GBP"),
        ];
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], claims, seasons, [], Rules,
            [new(1, 0, defaults), new(2, 1, updated)]);
        var events = RaceScoringEngine.Calculate(input).Events;
        Assert.Equal(4, Assert.Single(events, item => item.SourceKey == "league-points-per-game:0").Points);
        Assert.Equal(11, Assert.Single(events, item => item.SourceKey == "league-points-per-game:1").Points);
        Assert.DoesNotContain(events, item => item.SourceKey == "league-goals-per-game:1");
        Assert.DoesNotContain(events, item => item.SourceKey == "largest-outgoing-transfer:1");
        Assert.Equal(9, Assert.Single(events, item => item.SourceKey == "first-major-national-cup").Points);
        Assert.Equal(13, Assert.Single(events, item => item.SourceKey == "checkpoint:50").Points);
        Assert.Equal(2, Assert.Single(events, item => item.SourceKey == "checkpoint:50").RuleVersion);
        updated["bonus:first-major-national-cup"] = new(9, false);
        updated["checkpoint-50"] = new(13, false);
        events = RaceScoringEngine.Calculate(input).Events;
        Assert.DoesNotContain(events, item => item.SourceKey == "first-major-national-cup");
        Assert.DoesNotContain(events, item => item.SourceKey == "checkpoint:50");
    }

    [Fact]
    public void LeagueDuelsUseRatesNotTotalNumbers()
    {
        var result = Calculate([], [
            new SeasonInput(PlayerA, 0, true, 20, 45, 50, 0),
            new SeasonInput(PlayerB, 0, true, 30, 60, 60, 0),
        ]);

        Assert.Equal(7, result.Participants.Single(item => item.ParticipantId == PlayerA).DuelPoints);
        Assert.Equal(0, result.Participants.Single(item => item.ParticipantId == PlayerB).DuelPoints);
    }

    [Fact]
    public void MixedCurrenciesWithinOneSeasonKeepTransferDuelPending()
    {
        var result = Calculate([], [
            new SeasonInput(PlayerA, 0, true, 0, 0, 0, 0, "EUR", HasUncomparableTransfers: true),
            new SeasonInput(PlayerB, 0, true, 0, 0, 0, 100, "EUR"),
        ]);
        var duels = result.Events.Where(item => item.SourceType == "Duel").ToArray();
        Assert.Equal(2, duels.Length);
        Assert.All(duels, item => Assert.Equal("Pending", item.Status));
        Assert.All(result.Participants, item => Assert.Equal(0, item.DuelPoints));
    }

    [Fact]
    public void CheckpointStaysPendingIfOpponentSkippedTheRelevantSeason()
    {
        var result = Calculate(
            [Claim(PlayerA, "G001", 0), Claim(PlayerA, "G008", 0), Claim(PlayerA, "G020", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 1, true)]);

        var checkpoint = Assert.Single(result.Events, item => item.SourceKey == "checkpoint:50");
        Assert.Equal("Pending", checkpoint.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointRemainsPendingWhileComparableScoreEventsAreUnresolved(bool bothReachThreshold)
    {
        var claims = new List<ClaimInput>
        {
            Claim(PlayerA, "G001", 0), Claim(PlayerA, "G008", 0), Claim(PlayerA, "G020", 0),
        };
        if (bothReachThreshold)
            claims.AddRange([Claim(PlayerB, "G001", 0), Claim(PlayerB, "G008", 0), Claim(PlayerB, "G020", 0)]);
        SeasonInput[] seasons =
        [
            new(PlayerA, 0, true, 0, 0, 0, 100, "EUR"),
            new(PlayerB, 0, true, 0, 0, 0, 200, "GBP"),
        ];
        var pending = Calculate(claims.ToArray(), seasons).Events.Where(item => item.SourceKey == "checkpoint:50").ToArray();
        Assert.NotEmpty(pending);
        Assert.All(pending, item => Assert.Equal("Pending", item.Status));

        seasons[1] = seasons[1] with { Currency = "EUR" };
        var confirmed = Calculate(claims.ToArray(), seasons).Events.Where(item => item.SourceKey == "checkpoint:50").ToArray();
        Assert.NotEmpty(confirmed);
        Assert.All(confirmed, item => Assert.Equal("Confirmed", item.Status));
    }

    [Fact]
    public void EarlierPendingBonusPreventsLaterCheckpointConfirmation()
    {
        ClaimInput[] claims = [Claim(PlayerA, "G008", 0), Claim(PlayerA, "G001", 1), Claim(PlayerA, "G020", 1)];
        SeasonInput[] seasons =
        [
            Season(PlayerA, 0, true), Season(PlayerB, 0, false),
            Season(PlayerA, 1, true), Season(PlayerB, 1, true),
        ];
        var pending = Assert.Single(Calculate(claims, seasons).Events, item => item.SourceKey == "checkpoint:50");
        Assert.Equal("Pending", pending.Status);
        seasons[1] = seasons[1] with { IsCompleted = true };
        var confirmed = Assert.Single(Calculate(claims, seasons).Events, item => item.SourceKey == "checkpoint:50");
        Assert.Equal("Confirmed", confirmed.Status);
    }

    [Fact]
    public void LeagueDuelsRequireFifteenMatchesForEachPlayer()
    {
        var result = Calculate([], [
            new SeasonInput(PlayerA, 0, true, 14, 42, 30, 0),
            new SeasonInput(PlayerB, 0, true, 20, 20, 20, 0),
        ]);

        Assert.DoesNotContain(result.Events, item => item.SourceType == "Duel");
    }

    [Fact]
    public void TransferDuelRemainsPendingWhenCurrenciesDiffer()
    {
        var result = Calculate([], [
            new SeasonInput(PlayerA, 0, true, 0, 0, 0, 100, "EUR"),
            new SeasonInput(PlayerB, 0, true, 0, 0, 0, 200, "GBP"),
        ]);
        var duels = result.Events.Where(item => item.SourceType == "Duel").ToArray();
        Assert.Equal(2, duels.Length);
        Assert.All(duels, item => Assert.Equal("Pending", item.Status));
        Assert.All(result.Participants, item => Assert.Equal(0, item.DuelPoints));
    }

    [Fact]
    public void UnresolvedTransferDuelCannotPrematurelyDecideTheWinner()
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB], [Claim(PlayerA, "G008", 0)],
            [new(PlayerA, 0, true, 0, 0, 0, 100, "EUR"), new(PlayerB, 0, true, 0, 0, 0, 200, "GBP")],
            [new(PlayerA, "Club A", 20, 0), new(PlayerA, "Club B", 20, 0), new(PlayerA, "Club C", 20, 0)],
            Rules));
        Assert.Null(result.WinningParticipantId);
        Assert.True(result.VictoryPending);
    }

    [Fact]
    public void DuplicateOnceAchievementOnlyScoresOnce()
    {
        var result = Calculate([Claim(PlayerA, "G033", 0), Claim(PlayerA, "G033", 1)]);

        Assert.Equal(8, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void ConfirmedNationalTeamMilestoneReplacesHistoricalManualClaim()
    {
        var claim = Claim(PlayerA, "G033", 0);
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], [], Rules,
            NationalTeamMilestones: new Dictionary<Guid, int> { [PlayerA] = 2 });
        var result = RaceScoringEngine.Calculate(input);
        var milestone = Assert.Single(result.Events, item => item.SourceType == "Achievement");
        Assert.Equal("automatic:G033", milestone.SourceKey);
        Assert.Equal(2, milestone.SeasonIndex);
        Assert.Equal(8, milestone.Points);
        Assert.Equal(8, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        var fallback = RaceScoringEngine.Calculate(input with { NationalTeamMilestones = null });
        Assert.Equal(claim.Id.ToString("N"), Assert.Single(fallback.Events, item => item.SourceType == "Achievement").SourceKey);
    }

    [Fact]
    public void NationalTeamMilestoneRequiresEnoughMatchesAndAnActiveStint()
    {
        var teams = new[]
        {
            new NationalTeamStint { StartSeasonIndex = 0, EndSeasonIndex = 0, OfficialMatches = 12 },
            new NationalTeamStint { StartSeasonIndex = 2, EndSeasonIndex = 3, OfficialMatches = 8 },
        };
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(null, teams));
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(0, teams));
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(1, teams));
        Assert.True(RaceScoringEngine.QualifiesNationalTeamMilestone(2, teams));
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(4, teams));
    }

    [Fact]
    public void NationalTeamMilestoneCannotBePlacedInAStintWithoutMatches()
    {
        var teams = new[]
        {
            new NationalTeamStint { StartSeasonIndex = 0, EndSeasonIndex = 0, OfficialMatches = 20 },
            new NationalTeamStint { StartSeasonIndex = 2, EndSeasonIndex = 2, OfficialMatches = 0 },
        };
        Assert.True(RaceScoringEngine.QualifiesNationalTeamMilestone(0, teams));
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(2, teams));
        teams[1].OfficialMatches = 1;
        Assert.True(RaceScoringEngine.QualifiesNationalTeamMilestone(2, teams));
    }

    [Fact]
    public void CompleteNationalTeamAllocationsDetermineTheFirstTwentyMatchSeason()
    {
        NationalTeamStint[] teams =
        [
            new() { StartSeasonIndex = 0, EndSeasonIndex = 1, OfficialMatches = 12, SeasonMatches =
                [new() { SeasonIndex = 0, OfficialMatches = 4 }, new() { SeasonIndex = 1, OfficialMatches = 8 }] },
            new() { StartSeasonIndex = 1, OfficialMatches = 10, SeasonMatches =
                [new() { SeasonIndex = 1, OfficialMatches = 7 }, new() { SeasonIndex = 2, OfficialMatches = 3 }] },
        ];
        Assert.True(RaceScoringEngine.NationalTeamSeasonMatchesComplete(teams));
        Assert.Equal(2, RaceScoringEngine.CalculatedNationalTeamMilestone(teams));
        Assert.Equal(2, RaceScoringEngine.NationalTeamMilestone(1, teams));
        Assert.False(RaceScoringEngine.QualifiesNationalTeamMilestone(1, teams));
        Assert.True(RaceScoringEngine.QualifiesNationalTeamMilestone(2, teams));
        teams[1].SeasonMatches.RemoveAt(1);
        Assert.False(RaceScoringEngine.NationalTeamSeasonMatchesComplete(teams));
        Assert.Null(RaceScoringEngine.CalculatedNationalTeamMilestone(teams));
        Assert.Equal(1, RaceScoringEngine.NationalTeamMilestone(1, teams));
        Assert.Null(RaceScoringEngine.NationalTeamMilestone(null, teams));
    }

    [Fact]
    public void ThreeContinentsRequiresKnownDistinctContinentsAndAnActiveStintWithMatches()
    {
        var continents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PT"] = "EU", ["ES"] = "EU", ["BR"] = "SA", ["JP"] = "AS",
        };
        GeographicStintInput[] clubs =
        [
            new("pt", 1, 0, 0),
            new("ES", 1, 0, 0),
            new("BR", 1, 1, 1),
            new("JP", 1, 2, 2),
        ];
        Assert.False(RaceScoringEngine.QualifiesThreeContinentsMilestone(1, clubs, continents));
        Assert.True(RaceScoringEngine.QualifiesThreeContinentsMilestone(2, clubs, continents));
        Assert.False(RaceScoringEngine.QualifiesThreeContinentsMilestone(3, clubs, continents));
        clubs[3] = clubs[3] with { OfficialMatches = 0 };
        Assert.False(RaceScoringEngine.QualifiesThreeContinentsMilestone(2, clubs, continents));
        clubs[3] = clubs[3] with { OfficialMatches = 1, CountryCode = "UNKNOWN" };
        Assert.False(RaceScoringEngine.QualifiesThreeContinentsMilestone(2, clubs, continents));
    }

    [Fact]
    public void DetailedGeographyUsesActualFirstMatchesAcrossContinents()
    {
        var continents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["PT"] = "EU", ["BR"] = "SA", ["JP"] = "AS" };
        GeographicStintInput[] stints =
        [
            new("PT", 2, 0, 1, [new(0, 2)]),
            new("BR", 2, 0, 2, [new(1, 2)]),
            new("JP", 2, 0, 3, [new(3, 2)]),
        ];
        Assert.True(RaceScoringEngine.GeographicSeasonMatchesComplete(stints, continents));
        Assert.Equal(3, RaceScoringEngine.ThreeContinentsMilestone(1, stints, continents));
        Assert.False(RaceScoringEngine.QualifiesThreeContinentsMilestone(1, stints, continents));
        Assert.True(RaceScoringEngine.QualifiesThreeContinentsMilestone(3, stints, continents));
        stints[2] = stints[2] with { SeasonMatches = [new(3, 1)] };
        Assert.False(RaceScoringEngine.GeographicSeasonMatchesComplete(stints, continents));
        Assert.Null(RaceScoringEngine.CalculatedThreeContinentsMilestone(stints, continents));
        Assert.Equal(1, RaceScoringEngine.ThreeContinentsMilestone(1, stints, continents));
        stints[2] = stints[2] with { CountryCode = "UNKNOWN", SeasonMatches = [new(3, 2)] };
        Assert.False(RaceScoringEngine.GeographicSeasonMatchesComplete(stints, continents));
        Assert.Null(RaceScoringEngine.ThreeContinentsMilestone(3, stints, continents));
    }

    [Fact]
    public void ThreeContinentsMilestoneReplacesButDoesNotDoubleCountLegacyClaim()
    {
        var claim = Claim(PlayerA, "G053", 1);
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], [], Rules,
            ThreeContinentsMilestones: new Dictionary<Guid, int> { [PlayerA] = 2 });
        var milestone = Assert.Single(RaceScoringEngine.Calculate(input).Events, item => item.SourceType == "Achievement");
        Assert.Equal("automatic:G053", milestone.SourceKey);
        Assert.Equal(2, milestone.SeasonIndex);
        Assert.Equal(10, milestone.Points);
        var legacy = Assert.Single(RaceScoringEngine.Calculate(input with { ThreeContinentsMilestones = null }).Events,
            item => item.SourceType == "Achievement");
        Assert.Equal(claim.Id.ToString("N"), legacy.SourceKey);
    }

    [Fact]
    public void TenMajorTrophiesUseTheTenthRecordedSeasonAndPreserveEarlierManualMilestones()
    {
        var trophies = Enumerable.Range(0, 5).SelectMany(season => new[]
        {
            Claim(PlayerA, "G001", season), Claim(PlayerA, "G008", season),
        }).ToArray();
        Assert.DoesNotContain(Calculate(trophies[..9]).Events, item => item.SourceKey == "automatic:G048");
        var automatic = Assert.Single(Calculate(trophies).Events, item => item.SourceKey == "automatic:G048");
        Assert.Equal(4, automatic.SeasonIndex);
        Assert.Equal(8, automatic.Points);
        var earlier = Claim(PlayerA, "G048", 2);
        var earlierResult = Calculate([.. trophies, earlier]);
        Assert.DoesNotContain(earlierResult.Events, item => item.SourceKey == "automatic:G048");
        Assert.Contains(earlierResult.Events, item => item.SourceKey == earlier.Id.ToString("N"));
        var later = Claim(PlayerA, "G048", 6);
        var laterResult = Calculate([.. trophies, later]);
        Assert.Contains(laterResult.Events, item => item.SourceKey == "automatic:G048");
        Assert.DoesNotContain(laterResult.Events, item => item.SourceKey == later.Id.ToString("N"));
        var disabled = new ScoringInput(Guid.Empty, 500, [PlayerA], [.. trophies, later], [], [], Rules,
            RuleVersions: [new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G048"] = new(8, false) }),
                new RuleVersionInput(2, 5, new Dictionary<string, RuleSetting> { ["G048"] = new(8, true) })]);
        var disabledResult = RaceScoringEngine.Calculate(disabled);
        Assert.DoesNotContain(disabledResult.Events, item => item.SourceKey == "automatic:G048");
        Assert.Contains(disabledResult.Events, item => item.SourceKey == later.Id.ToString("N"));
    }

    [Fact]
    public void ContinentalStagesOnlyAwardTheBestResultInASeason()
    {
        var result = Calculate([
            Claim(PlayerA, "G028", 0), Claim(PlayerA, "G029", 0),
            Claim(PlayerA, "G030", 0), Claim(PlayerA, "G020", 0),
            Claim(PlayerA, "G028", 1),
        ]);
        Assert.Equal(34, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void ContinentalStageExclusionsRespectKnownCompetitionIdentity()
    {
        var firstCompetition = Guid.NewGuid();
        var secondCompetition = Guid.NewGuid();
        var winner = Claim(PlayerA, "G020", 0) with { CompetitionId = firstCompetition };
        var quarterFinal = Claim(PlayerA, "G028", 0) with { CompetitionId = secondCompetition };
        Assert.Equal(34, Calculate([winner, quarterFinal]).Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.Equal(30, Calculate([winner, quarterFinal with { CompetitionId = firstCompetition }]).Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.Equal(30, Calculate([winner, quarterFinal with { CompetitionId = null }]).Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void NewlyPromotedTopFourAndRunnerUpDoNotStackWithFirstDivisionTitle()
    {
        var result = Calculate([Claim(PlayerA, "G001", 0), Claim(PlayerA, "G012", 0), Claim(PlayerA, "G016", 0)]);
        Assert.Equal(18, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void PromotionIsNotExcludedByATitleForADifferentKnownClub()
    {
        var title = Claim(PlayerA, "G002", 0) with { ClubName = "Club A", CountryCode = "PT" };
        var promotion = Claim(PlayerA, "G005", 0) with { ClubName = "Club B", CountryCode = "PT" };
        var distinctClubs = Calculate([title, promotion]);
        Assert.Equal(7, Assert.Single(distinctClubs.Events, item => item.SourceKey == promotion.Id.ToString("N")).Points);

        var sameClub = Calculate([title, promotion with { ClubName = " club a " }]);
        Assert.Equal(0, Assert.Single(sameClub.Events, item => item.SourceKey == promotion.Id.ToString("N")).Points);
        var missingClub = Calculate([title, promotion with { ClubName = null }]);
        Assert.Equal(0, Assert.Single(missingClub.Events, item => item.SourceKey == promotion.Id.ToString("N")).Points);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    public void ExcludedPromotionCannotClaimFirstBonusEvenWhenItsConfiguredPointsAreZero(int promotionPoints)
    {
        var title = Claim(PlayerA, "G002", 0) with { ClubName = "Club A" };
        var excludedPromotion = Claim(PlayerA, "G005", 0) with { ClubName = "Club A" };
        var laterPromotion = Claim(PlayerB, "G005", 1) with { ClubName = "Club B" };
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA, PlayerB], [title, excludedPromotion, laterPromotion],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true), Season(PlayerA, 1, true), Season(PlayerB, 1, true)],
            [], Rules, [new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G005"] = new(promotionPoints, true) })]));
        var bonus = Assert.Single(result.Events, item => item.SourceType == "FirstBonus" && item.SourceKey == "first-promotion");
        Assert.Equal(PlayerB, bonus.ParticipantId);
        Assert.Equal(1, bonus.SeasonIndex);
        Assert.Equal("Confirmed", bonus.Status);
    }

    [Fact]
    public void ClubMilestonesUseDistinctClubsAndTheirEligibilitySeason()
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA, PlayerB], [Claim(PlayerA, "G042", 0)], [],
            [
                new(PlayerA, "Club A", 20, 0), new(PlayerA, "CLUB A", 30, 2),
                new(PlayerA, "Club B", 20, 1), new(PlayerA, "Club C", 20, 3),
                new(PlayerA, "Club D", 19, null), new(PlayerA, "Club E", 20, null),
            ], Rules));
        var milestone = Assert.Single(result.Events, item => item.SourceKey == "automatic:G042");
        Assert.Equal(3, milestone.SeasonIndex);
        Assert.Equal(8, milestone.Points);
        Assert.Equal(8, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.DoesNotContain(result.Events, item => item.SourceKey == "automatic:G043");
    }

    [Fact]
    public void AmbiguousCareerStintDoesNotCountAsAnAdditionalDistinctClub()
    {
        ClubInput[] clubs =
        [
            new(PlayerA, "Shared Club", 20, 0, "PT"),
            new(PlayerA, "Shared Club", 20, 1, "PT", ClubId: Guid.NewGuid()),
            new(PlayerA, "Shared Club", 20, 2, "PT", ClubId: Guid.NewGuid()),
        ];
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA], [], [], clubs, Rules);
        var result = RaceScoringEngine.Calculate(input);
        Assert.Equal(2, Assert.Single(result.Participants).EligibleClubCount);
        Assert.DoesNotContain(result.Events, item => item.SourceKey == "automatic:G042");

        result = RaceScoringEngine.Calculate(input with
        {
            Clubs = [.. clubs, new(PlayerA, "Other Club", 20, 3, "PT")],
        });
        Assert.Equal(3, Assert.Single(result.Participants).EligibleClubCount);
        Assert.Equal(3, Assert.Single(result.Events, item => item.SourceKey == "automatic:G042").SeasonIndex);
    }

    [Fact]
    public void ClubMilestonesCombineStintsWithoutCountingFutureOrDifferentClubs()
    {
        ClubInput[] clubs =
        [
            new(PlayerA, "Returning", 12, null, "PT", 0, 0),
            new(PlayerA, " returning ", 8, 2, "pt", 2),
            new(PlayerA, "Other", 20, 0, "PT"),
            new(PlayerA, "Third", 20, 0, "PT"),
            new(PlayerA, "Returning", 100, null, "ES", 0),
            new(PlayerB, "Returning", 100, null, "PT", 0),
        ];
        var claim = Claim(PlayerA, "G037", 2) with
        {
            ClubName = "Returning", CountryCode = "PT", StartingCareerClub = "Other", StartingCareerCountry = "PT",
        };
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], clubs, Rules);
        var result = RaceScoringEngine.Calculate(input);
        Assert.Equal(3, result.Participants.Single(item => item.ParticipantId == PlayerA).EligibleClubCount);
        Assert.Equal(2, Assert.Single(result.Events, item => item.SourceKey == "automatic:G042").SeasonIndex);
        Assert.Equal(5, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var corrected = clubs.ToArray();
        corrected[0] = corrected[0] with { OfficialMatches = 11 };
        var afterCorrection = RaceScoringEngine.Calculate(input with { Clubs = corrected });
        Assert.Equal(2, afterCorrection.Participants.Single(item => item.ParticipantId == PlayerA).EligibleClubCount);
        Assert.Equal(0, Assert.Single(afterCorrection.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        Assert.DoesNotContain(afterCorrection.Events, item => item.SourceKey == "automatic:G042");
        corrected[0] = corrected[0] with { OfficialMatches = 12, StartSeasonIndex = 3, EndSeasonIndex = 3 };
        Assert.DoesNotContain(RaceScoringEngine.EligibleClubs(corrected),
            item => item.ClubName.Trim().Equals("returning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VictoryWaitsForTheCombinedClubMilestoneSeason()
    {
        ClubInput[] clubs =
        [
            new(PlayerA, "One", 20, 0, "PT"), new(PlayerA, "Two", 20, 0, "PT"),
            new(PlayerA, "Three", 12, null, "PT", 0, 0), new(PlayerA, "Three", 8, 2, "PT", 2),
        ];
        var input = new ScoringInput(Guid.Empty, 10, [PlayerA, PlayerB], [Claim(PlayerA, "G008", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true), Season(PlayerA, 2, true), Season(PlayerB, 2, false)], clubs, Rules);
        var pending = RaceScoringEngine.Calculate(input);
        Assert.Null(pending.WinningParticipantId);
        Assert.Equal(2, pending.WinningSeasonIndex);
        Assert.True(pending.VictoryPending);
        var completed = RaceScoringEngine.Calculate(input with
        {
            Seasons = [Season(PlayerA, 0, true), Season(PlayerB, 0, true), Season(PlayerA, 2, true), Season(PlayerB, 2, true)],
        });
        Assert.Equal(PlayerA, completed.WinningParticipantId);
        Assert.Equal(2, completed.WinningSeasonIndex);
    }

    [Fact]
    public void InvalidatedMoveDoesNotSuppressAnEligibleMoveInTheSameSeason()
    {
        var invalid = Claim(PlayerA, "G037", 2) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ClubName = "Invalid", CountryCode = "PT", StartingCareerClub = "Start", StartingCareerCountry = "PT",
        };
        var valid = invalid with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), ClubName = "Valid" };
        ClubInput[] clubs = [new(PlayerA, "Start", 20, 0, "PT"), new(PlayerA, "Invalid", 19, null, "PT", 1),
            new(PlayerA, "Valid", 20, 2, "PT", 2)];
        var result = RaceScoringEngine.Calculate(new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [invalid, valid], [], clubs, Rules));
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == invalid.Id.ToString("N")).Points);
        Assert.Equal(5, Assert.Single(result.Events, item => item.SourceKey == valid.Id.ToString("N")).Points);
    }

    [Fact]
    public void CountryMilestonesCombineDifferentClubsAndRecalculateAfterCorrections()
    {
        ClubInput[] clubs =
        [
            new(PlayerA, "Start", 20, 0, "PT", 0),
            new(PlayerA, "First abroad", 12, null, "ES", 0, 0),
            new(PlayerA, "Second abroad", 8, null, "es", 2, null, 2),
            new(PlayerA, "France", 20, 1, "FR", 1),
            new(PlayerA, "Germany", 20, 1, "DE", 1),
            new(PlayerB, "Other manager", 100, null, "ES", 0),
        ];
        var claim = Claim(PlayerA, "G041", 2) with
        {
            CountryCode = "ES", StartingCareerClub = "Start", StartingCareerCountry = "PT",
        };
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], clubs, Rules);
        var result = RaceScoringEngine.Calculate(input);
        Assert.Equal(5, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        Assert.Equal(2, Assert.Single(result.Events, item => item.SourceKey == "automatic:G047").SeasonIndex);
        var corrected = clubs.ToArray();
        corrected[1] = corrected[1] with { OfficialMatches = 11 };
        result = RaceScoringEngine.Calculate(input with { Clubs = corrected });
        Assert.Equal(0, Assert.Single(result.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        Assert.DoesNotContain(result.Events, item => item.SourceKey == "automatic:G047");
        corrected[1] = corrected[1] with { OfficialMatches = 12, StartSeasonIndex = 3, EndSeasonIndex = 3 };
        Assert.DoesNotContain(RaceScoringEngine.EligibleCountries(corrected), item => item.ParticipantId == PlayerA && item.CountryCode == "es");
    }

    [Fact]
    public void ManualClubMilestonesCannotSubstituteForMissingEligibility()
    {
        var result = Calculate([Claim(PlayerA, "G042", 0)]);
        Assert.Equal(0, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void ClubsWithTheSameNameInDifferentCountriesAreDistinct()
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA, PlayerB], [], [],
            [new(PlayerA, "United", 20, 0, "PT"), new(PlayerA, "United", 20, 1, "GB"),
             new(PlayerA, "United", 20, 2, "US"), new(PlayerA, "united", 30, 3, "us")], Rules));
        Assert.Equal(3, result.Participants.Single(item => item.ParticipantId == PlayerA).EligibleClubCount);
        Assert.Equal(2, Assert.Single(result.Events, item => item.SourceKey == "automatic:G042").SeasonIndex);
    }

    [Fact]
    public void FourCountryMilestoneUsesEarliestEligibleSeasonPerCountry()
    {
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [Claim(PlayerA, "G047", 0)], [],
        [
            new(PlayerA, "A", 20, 0, "PT"), new(PlayerA, "B", 25, 4, " pt "),
            new(PlayerA, "C", 20, 2, "ES"), new(PlayerA, "D", 19, null, "DE"),
            new(PlayerA, "E", 20, 1, "FR"), new(PlayerA, "F", 20, 3, "GB"),
            new(PlayerA, "G", 20, null, "IT"), new(PlayerA, "H", 20, 0, null),
            new(PlayerB, "I", 20, 0, "DE"),
        ], Rules);
        var result = RaceScoringEngine.Calculate(input);
        var milestone = Assert.Single(result.Events, item => item.SourceKey == "automatic:G047");
        Assert.Equal(PlayerA, milestone.ParticipantId);
        Assert.Equal(3, milestone.SeasonIndex);
        Assert.Equal(8, milestone.Points);
        Assert.DoesNotContain(result.Events, item => item.SourceType == "Achievement" && item.SourceKey != "automatic:G047"
            && input.Claims.Any(claim => claim.Code == "G047" && item.SourceKey == claim.Id.ToString("N")));

        var withoutFourthCountry = RaceScoringEngine.Calculate(input with
        {
            Clubs = input.Clubs.Where(item => item.CountryCode != "GB").ToArray(),
        });
        Assert.DoesNotContain(withoutFourthCountry.Events, item => item.SourceKey == "automatic:G047");
    }

    [Fact]
    public void FourCountryMilestoneUsesRuleVersionInEligibilitySeason()
    {
        var versions = new[]
        {
            new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G047"] = new(8, true) }),
            new RuleVersionInput(2, 3, new Dictionary<string, RuleSetting> { ["G047"] = new(13, true) }),
        };
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [], [],
            [new(PlayerA, "A", 20, 0, "PT"), new(PlayerA, "B", 20, 1, "ES"),
             new(PlayerA, "C", 20, 2, "FR"), new(PlayerA, "D", 20, 3, "GB")], Rules, versions);
        var milestone = Assert.Single(RaceScoringEngine.Calculate(input).Events, item => item.SourceKey == "automatic:G047");
        Assert.Equal(13, milestone.Points);
        Assert.Equal(2, milestone.RuleVersion);
        versions[1] = versions[1] with { Rules = new Dictionary<string, RuleSetting> { ["G047"] = new(13, false) } };
        Assert.DoesNotContain(RaceScoringEngine.Calculate(input).Events, item => item.SourceKey == "automatic:G047");
    }

    [Fact]
    public void LeagueTitleMilestonesUseKnownDistinctCountriesAndClubs()
    {
        var titles = new[]
        {
            new ClaimInput(Guid.NewGuid(), PlayerA, "G001", 0, "Club A", "PT", DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.NewGuid(), PlayerA, "G001", 1, "Club A", "PT", DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.NewGuid(), PlayerA, "G001", 2, "Club B", "ES", DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.NewGuid(), PlayerA, "G001", 3, null, null, DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.NewGuid(), PlayerA, "G001", 4, "Club C", "FR", DateTimeOffset.UnixEpoch),
        };
        var result = Calculate(titles);
        Assert.Equal(2, Assert.Single(result.Events, item => item.SourceKey == "automatic:G045").SeasonIndex);
        Assert.Equal(4, Assert.Single(result.Events, item => item.SourceKey == "automatic:G046").SeasonIndex);
        Assert.Equal(2, Assert.Single(result.Events, item => item.SourceKey == "automatic:G049").SeasonIndex);
        Assert.Equal(4, Assert.Single(result.Events, item => item.SourceKey == "automatic:G052").SeasonIndex);
    }

    [Fact]
    public void ClubMovesScoreAtMostOncePerSeasonWithoutLosingLaterEligibleMoves()
    {
        var claims = new[]
        {
            new ClaimInput(Guid.Parse("00000000-0000-0000-0000-000000000001"), PlayerA, "G037", 0, "Club A", "PT", DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.Parse("00000000-0000-0000-0000-000000000002"), PlayerA, "G037", 0, "Club B", "PT", DateTimeOffset.UnixEpoch),
            new ClaimInput(Guid.Parse("00000000-0000-0000-0000-000000000003"), PlayerA, "G037", 1, "Club B", "PT", DateTimeOffset.UnixEpoch),
        };
        var result = Calculate(claims);
        Assert.Equal(10, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void CompleteClubSeasonRecordsDetermineExactClubAndCountryMilestones()
    {
        ClubInput[] stints =
        [
            new(PlayerA, "Club A", 20, 0, "PT", 0, 1, 0,
                [new SeasonMatchInput(0, 12), new SeasonMatchInput(1, 8)]),
            new(PlayerA, "Club B", 10, null, "ES", 0, 0, null,
                [new SeasonMatchInput(0, 10)]),
            new(PlayerA, "Club C", 10, null, "ES", 1, 1, null,
                [new SeasonMatchInput(1, 10)]),
        ];
        var club = Assert.Single(RaceScoringEngine.EligibleClubs(stints));
        Assert.Equal(1, club.EligibleSeasonIndex);
        Assert.False(RaceScoringEngine.QualifiesClubMilestone(stints[0], stints));
        Assert.False(RaceScoringEngine.QualifiesCountryMilestone(stints[0], stints));
        Assert.True(RaceScoringEngine.QualifiesClubMilestone(stints[0] with { EligibleSeasonIndex = 1 }, stints));
        var countries = RaceScoringEngine.EligibleCountries(stints);
        Assert.Equal(1, Assert.Single(countries, item => item.CountryCode == "PT").SeasonIndex);
        Assert.Equal(1, Assert.Single(countries, item => item.CountryCode == "ES").SeasonIndex);
        var returnStint = new ClubInput(PlayerA, "Club A", 20, 2, "PT", 2, 2, 2,
            [new SeasonMatchInput(2, 20)]);
        var withReturn = stints.Append(returnStint).ToArray();
        Assert.True(RaceScoringEngine.QualifiesClubMilestone(returnStint, withReturn));
        Assert.True(RaceScoringEngine.QualifiesCountryMilestone(returnStint, withReturn));
        Assert.Equal(1, Assert.Single(RaceScoringEngine.EligibleClubs(withReturn)).EligibleSeasonIndex);

        stints[0] = stints[0] with { SeasonMatches = [new SeasonMatchInput(0, 12)] };
        Assert.Equal(0, Assert.Single(RaceScoringEngine.EligibleClubs(stints)).EligibleSeasonIndex);
        Assert.True(RaceScoringEngine.QualifiesClubMilestone(stints[0], stints));
    }

    [Fact]
    public void DetailedMatchChronologyControlsCareerMovesAndWinningSeason()
    {
        ClubInput[] stints =
        [
            new(PlayerA, "First", 20, 0, "PT", 0, 0, null, [new SeasonMatchInput(0, 20)]),
            new(PlayerA, "Second", 20, 0, "ES", 0, 1, null,
                [new SeasonMatchInput(0, 12), new SeasonMatchInput(1, 8)]),
            new(PlayerA, "Third", 20, 0, "FR", 0, 0, null, [new SeasonMatchInput(0, 20)]),
        ];
        var clubMove = Claim(PlayerA, "G037", 0) with
        {
            ClubName = "Second", CountryCode = "ES", StartingCareerClub = "First", StartingCareerCountry = "PT",
        };
        var countryMove = Claim(PlayerA, "G041", 0) with
        {
            CountryCode = "ES", StartingCareerClub = "First", StartingCareerCountry = "PT",
        };
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(clubMove, stints));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(countryMove, stints));
        Assert.Null(RaceScoringEngine.ValidateCareerMove(clubMove with { SeasonIndex = 1 }, stints));
        Assert.Null(RaceScoringEngine.ValidateCareerMove(countryMove with { SeasonIndex = 1 }, stints));

        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB], [Claim(PlayerA, "G008", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true), Season(PlayerA, 1, true), Season(PlayerB, 1, true)],
            stints, Rules));
        Assert.Equal(PlayerA, result.WinningParticipantId);
        Assert.Equal(1, result.WinningSeasonIndex);
        Assert.Equal(1, Assert.Single(result.Events, item => item.SourceKey == "automatic:G042").SeasonIndex);
    }

    [Fact]
    public void DetailedMatchChronologyControlsClubAndCountryMilestoneAwards()
    {
        var stints = Enumerable.Range(0, 8).Select(index =>
            new ClubInput(PlayerA, $"Club {index}", 20, 0, $"C{index}", 0, 1, null,
                [new SeasonMatchInput(0, index < 2 ? 20 : 10),
                 new SeasonMatchInput(1, index < 2 ? 0 : 10)])).ToArray();
        var result = RaceScoringEngine.Calculate(new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [], [], stints, Rules));
        foreach (var code in new[] { "G042", "G043", "G044", "G047" })
            Assert.Equal(1, Assert.Single(result.Events, item => item.SourceKey == $"automatic:{code}").SeasonIndex);
    }

    [Fact]
    public void CareerClubMilestonesUseStableIdsAcrossDifferentRecordedNames()
    {
        var clubId = Guid.NewGuid();
        ClubInput[] stints =
        [
            new(PlayerA, "Old club name", 12, null, "PT", 0, 0, ClubId: clubId),
            new(PlayerA, "New club name", 8, 1, "PT", 1, 1, ClubId: clubId),
        ];
        Assert.True(RaceScoringEngine.SameCareerClub(stints[0], stints[1]));
        var eligible = Assert.Single(RaceScoringEngine.EligibleClubs(stints));
        Assert.Equal(1, eligible.EligibleSeasonIndex);
        Assert.Equal(clubId, eligible.ClubId);
        Assert.False(RaceScoringEngine.SameCareerClub(stints[0], stints[1] with { ClubId = Guid.NewGuid() }));
        stints[0] = stints[0] with { ClubId = null, ClubName = "New club name" };
        Assert.Equal(1, Assert.Single(RaceScoringEngine.EligibleClubs(stints)).EligibleSeasonIndex);
    }

    [Fact]
    public void ClubClaimFrequencyAndTitleMilestonesUseResolvedClubIdentity()
    {
        var clubId = Guid.NewGuid();
        ClaimInput[] claims =
        [
            Claim(PlayerA, "G025", 0) with { ClubName = "Old name", CountryCode = "PT", ClubId = clubId },
            Claim(PlayerA, "G025", 1) with { ClubName = "New name", CountryCode = "PT", ClubId = clubId },
            Claim(PlayerA, "G001", 0) with { ClubName = "Old name", CountryCode = "PT", ClubId = clubId },
            Claim(PlayerA, "G001", 1) with { ClubName = "New name", CountryCode = "PT", ClubId = clubId },
            Claim(PlayerA, "G001", 2) with { ClubName = "New name", CountryCode = "PT" },
        ];
        var result = Calculate(claims);
        Assert.Single(result.Events, item => item.SourceType == "Achievement" && item.SourceKey == claims[0].Id.ToString("N"));
        Assert.DoesNotContain(result.Events, item => item.SourceKey == claims[1].Id.ToString("N"));
        Assert.DoesNotContain(result.Events, item => item.SourceKey == "automatic:G049");
    }

    [Fact]
    public void ClubFrequencyDistinguishesCountriesButTreatsMissingCountryConservatively()
    {
        var claims = new[]
        {
            Claim(PlayerA, "G025", 0) with { ClubName = "United", CountryCode = "PT" },
            Claim(PlayerA, "G025", 1) with { ClubName = " united ", CountryCode = "ES" },
            Claim(PlayerA, "G025", 2) with { ClubName = "United", CountryCode = null },
            Claim(PlayerA, "G025", 3) with { ClubName = "UNITED", CountryCode = "pt" },
            Claim(PlayerA, "G011", 0) with { ClubName = "United", CountryCode = "PT" },
            Claim(PlayerA, "G011", 0) with { ClubName = "United", CountryCode = "ES" },
            Claim(PlayerA, "G011", 0) with { ClubName = "United", CountryCode = null, CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) },
            Claim(PlayerA, "G011", 1) with { ClubName = "United", CountryCode = "PT" },
        };
        var result = Calculate(claims);
        Assert.Equal(24, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.DoesNotContain(result.Events, item => item.SourceKey == claims[2].Id.ToString("N"));
        Assert.DoesNotContain(result.Events, item => item.SourceKey == claims[3].Id.ToString("N"));
        Assert.DoesNotContain(result.Events, item => item.SourceKey == claims[6].Id.ToString("N"));

        var legacyFirst = Calculate([
            claims[2] with { SeasonIndex = 0 },
            claims[0] with { SeasonIndex = 1 },
            claims[1] with { SeasonIndex = 2 },
        ]);
        Assert.Equal(6, legacyFirst.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void CareerMoveUsesStartingClubAndActualEligibilitySeason()
    {
        var claim = Claim(PlayerA, "G037", 2) with
        {
            ClubName = "Second", CountryCode = "PT", StartingCareerClub = "First", StartingCareerCountry = "PT",
        };
        ClubInput[] clubs =
        [
            new(PlayerA, "First", 40, 0, "PT", 0),
            new(PlayerA, "Second", 20, 2, "PT", 1),
        ];
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], clubs, Rules);
        Assert.Equal(5, Assert.Single(RaceScoringEngine.Calculate(input).Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim with { ClubName = "First", SeasonIndex = 0 }, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim with { SeasonIndex = 1 }, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim with { StartingCareerClub = "Second" }, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim, [clubs[0], clubs[1] with { OfficialMatches = 19, EligibleSeasonIndex = null }]));
        var corrected = RaceScoringEngine.Calculate(input with { Clubs = [clubs[0], clubs[1] with { OfficialMatches = 19, EligibleSeasonIndex = null }] });
        Assert.Equal(0, Assert.Single(corrected.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var legacy = RaceScoringEngine.Calculate(input with { Claims = [claim with { StartingCareerClub = null, StartingCareerCountry = null }], Clubs = [] });
        Assert.Equal(5, Assert.Single(legacy.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
    }

    [Fact]
    public void CareerMovesUseStartingClubIdentityAndSupportLegacyText()
    {
        var startId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        ClubInput[] clubs =
        [
            new(PlayerA, "Original name", 20, 0, "PT", 0, ClubId: startId),
            new(PlayerA, "Next", 20, 1, "ES", 1, ClubId: nextId),
        ];
        var clubMove = Claim(PlayerA, "G037", 1) with
        {
            ClubName = "Next", CountryCode = "ES", ClubId = nextId,
            StartingCareerClub = "Changed name", StartingCareerCountry = "PT", StartingCareerClubId = startId,
        };
        Assert.Null(RaceScoringEngine.ValidateCareerMove(clubMove, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(clubMove with { StartingCareerClubId = Guid.NewGuid() }, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(clubMove with { ClubId = startId, ClubName = "Original name", CountryCode = "PT" }, clubs));
        Assert.Null(RaceScoringEngine.ValidateCareerMove(clubMove with
        {
            StartingCareerClubId = null, StartingCareerClub = "Original name",
        }, clubs));
        var countryMove = clubMove with { Code = "G041", ClubName = null, ClubId = null };
        Assert.Null(RaceScoringEngine.ValidateCareerMove(countryMove, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(countryMove with { StartingCareerClubId = Guid.NewGuid() }, clubs));
    }

    [Fact]
    public void CountryMoveExcludesStartingCountryAndRecalculatesAfterCareerChanges()
    {
        var claim = Claim(PlayerA, "G041", 2) with
        {
            CountryCode = "ES", StartingCareerClub = "First", StartingCareerCountry = "PT",
        };
        ClubInput[] clubs = [new(PlayerA, "First", 20, 0, "PT", 0), new(PlayerA, "Second", 20, 2, "ES", 1)];
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], [claim], [], clubs, Rules);
        Assert.Null(RaceScoringEngine.ValidateCareerMove(claim, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim with { CountryCode = "PT", SeasonIndex = 0 }, clubs));
        Assert.NotNull(RaceScoringEngine.ValidateCareerMove(claim with { SeasonIndex = 1 }, clubs));
        Assert.Equal(5, Assert.Single(RaceScoringEngine.Calculate(input).Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var corrected = RaceScoringEngine.Calculate(input with { Clubs = [clubs[0], clubs[1] with { OfficialMatches = 19, EligibleSeasonIndex = null }] });
        Assert.Equal(0, Assert.Single(corrected.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
        var legacy = RaceScoringEngine.Calculate(input with { Claims = [claim with { StartingCareerClub = null, StartingCareerCountry = null }], Clubs = [] });
        Assert.Equal(5, Assert.Single(legacy.Events, item => item.SourceKey == claim.Id.ToString("N")).Points);
    }

    [Fact]
    public void RuleVersionsApplyBySeasonAndRetroactiveChangesSupersedeEarlierVersions()
    {
        var claims = new[] { Claim(PlayerA, "G008", 0), Claim(PlayerA, "G008", 1) };
        var v1 = new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G008"] = new(10, true) });
        var v2 = new RuleVersionInput(2, 1, new Dictionary<string, RuleSetting> { ["G008"] = new(20, true) });
        var v3 = new RuleVersionInput(3, 0, new Dictionary<string, RuleSetting> { ["G008"] = new(5, true) });
        var input = new ScoringInput(Guid.Empty, 500, [PlayerA, PlayerB], claims, [], [], Rules, [v1, v2]);
        var effective = RaceScoringEngine.Calculate(input);
        Assert.Equal(30, effective.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.Contains(effective.Events, item => item.SourceType == "Achievement" && item.SeasonIndex == 0 && item.RuleVersion == 1 && item.Points == 10);
        Assert.Contains(effective.Events, item => item.SourceType == "Achievement" && item.SeasonIndex == 1 && item.RuleVersion == 2 && item.Points == 20);
        var retroactive = RaceScoringEngine.Calculate(input with { RuleVersions = [v1, v2, v3] });
        Assert.Equal(10, retroactive.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
        Assert.All(retroactive.Events.Where(item => item.SourceType == "Achievement"), item => Assert.Equal(3, item.RuleVersion));
    }

    [Fact]
    public void ZeroPointEnabledRuleCanStillQualifyForFirstBonus()
    {
        var version = new RuleVersionInput(1, 0, new Dictionary<string, RuleSetting> { ["G008"] = new(0, true) });
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA, PlayerB], [Claim(PlayerA, "G008", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true)], [], Rules, [version]));
        Assert.Equal(5, result.Participants.Single(item => item.ParticipantId == PlayerA).FirstBonusPoints);
        Assert.Equal(0, result.Participants.Single(item => item.ParticipantId == PlayerA).BasePoints);
    }

    [Fact]
    public void VictoryRemainsPendingUntilBothPlayersCompleteTheWinningSeason()
    {
        var result = VictoryScenario([Season(PlayerA, 0, true)]);

        Assert.Null(result.WinningParticipantId);
        Assert.True(result.VictoryPending);
    }

    [Fact]
    public void VictoryIsConfirmedWhenBothPlayersCompleteTheWinningSeason()
    {
        var result = VictoryScenario([Season(PlayerA, 0, true), Season(PlayerB, 0, true)]);

        Assert.Equal(PlayerA, result.WinningParticipantId);
        Assert.False(result.VictoryPending);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TiedVictoryWaitsForANewComparableSeason(bool rivalCompleted, bool expectedPending)
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB],
            [Claim(PlayerA, "G008", 0), Claim(PlayerB, "G008", 0), Claim(PlayerA, "G009", 1)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true),
             Season(PlayerA, 1, true), Season(PlayerB, 1, rivalCompleted)],
            [new(PlayerA, "A1", 20, 0), new(PlayerA, "A2", 20, 0), new(PlayerA, "A3", 20, 0),
             new(PlayerB, "B1", 20, 0), new(PlayerB, "B2", 20, 0), new(PlayerB, "B3", 20, 0)],
            Rules));
        Assert.Equal(expectedPending, result.VictoryPending);
        Assert.Equal(expectedPending ? null : PlayerA, result.WinningParticipantId);
        if (!expectedPending) Assert.Equal(1, result.WinningSeasonIndex);
    }

    [Fact]
    public void FutureClubEligibilityCannotProduceAnEarlierVictory()
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB],
            [Claim(PlayerA, "G008", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true), Season(PlayerA, 2, true)],
            [new(PlayerA, "Club A", 20, 0), new(PlayerA, "Club B", 20, 0), new(PlayerA, "Club C", 20, 2)],
            Rules));

        Assert.Null(result.WinningParticipantId);
        Assert.True(result.VictoryPending);
        Assert.Equal(2, result.WinningSeasonIndex);
    }

    [Fact]
    public void MissingMilestoneSeasonDoesNotCountTowardVictory()
    {
        var result = RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB],
            [Claim(PlayerA, "G008", 0)],
            [Season(PlayerA, 0, true), Season(PlayerB, 0, true)],
            [new(PlayerA, "Club A", 20, 0), new(PlayerA, "Club B", 20, 0), new(PlayerA, "Club C", 20, null)],
            Rules));

        Assert.Null(result.WinningParticipantId);
        Assert.Equal(2, result.Participants.Single(item => item.ParticipantId == PlayerA).EligibleClubCount);
    }

    private static RaceScoringResult VictoryScenario(SeasonInput[] seasons) =>
        RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 10, [PlayerA, PlayerB],
            [Claim(PlayerA, "G008", 0)], seasons,
            [new(PlayerA, "Club A", 20, 0), new(PlayerA, "Club B", 20, 0), new(PlayerA, "Club C", 20, 0)],
            Rules));

    private static RaceScoringResult Calculate(ClaimInput[] claims, SeasonInput[]? seasons = null, ClubInput[]? clubs = null) =>
        RaceScoringEngine.Calculate(new ScoringInput(
            Guid.Empty, 500, [PlayerA, PlayerB], claims, seasons ?? [], clubs ?? [], Rules));

    private static ClaimInput Claim(Guid player, string code, int season) =>
        new(Guid.NewGuid(), player, code, season, null, null, DateTimeOffset.UnixEpoch);

    private static SeasonInput Season(Guid player, int index, bool completed) =>
        new(player, index, completed, 0, 0, 0, 0);
}
