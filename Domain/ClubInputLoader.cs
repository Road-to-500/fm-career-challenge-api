using FmCareerChallenge.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FmCareerChallenge.Api.Domain;

public static class ClubInputLoader
{
    public static async Task<ClubInput[]> LoadAsync(
        ChallengeDbContext database,
        IReadOnlyCollection<CareerStint> stints,
        IReadOnlyDictionary<Guid, Guid> careerParticipants,
        CancellationToken cancellationToken)
    {
        var stintIds = stints.Select(item => item.Id).ToArray();
        var catalogIds = stints.Where(item => item.ClubId.HasValue).Select(item => item.ClubId!.Value).ToArray();
        var countries = stints.Select(item => item.CountryCode.Trim().ToUpperInvariant()).Distinct().ToArray();
        var names = stints.Select(item => item.ClubName.Trim().ToUpperInvariant()).Distinct().ToArray();
        var catalog = await database.Clubs.AsNoTracking()
            .Where(item => catalogIds.Contains(item.Id) || countries.Contains(item.CountryCode) && names.Contains(item.NormalizedName))
            .ToListAsync(cancellationToken);
        var byIdentity = catalog.GroupBy(item => (item.CountryCode, item.NormalizedName))
            .Where(group => group.Select(item => item.Id).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Id);
        var details = await (
            from entry in database.ClubSeasonStatistics.AsNoTracking()
            join season in database.CareerSeasons.AsNoTracking() on entry.CareerSeasonId equals season.Id
            where stintIds.Contains(entry.CareerStintId) && season.UsesClubBreakdown
            select new { entry.CareerStintId, season.CareerId, season.SeasonIndex, entry.OfficialMatches, entry.DivisionLevel }
        ).ToListAsync(cancellationToken);
        var byStint = details.ToLookup(item => item.CareerStintId);
        return stints.Select(stint =>
        {
            var matches = byStint[stint.Id].Where(item => item.CareerId == stint.CareerId)
                .Select(item => new SeasonMatchInput(item.SeasonIndex, item.OfficialMatches, item.DivisionLevel)).ToArray();
            return new ClubInput(careerParticipants[stint.CareerId], stint.ClubName, stint.OfficialMatches,
                stint.EligibleSeasonIndex, stint.CountryCode, stint.StartSeasonIndex, stint.EndSeasonIndex,
                stint.CountryEligibleSeasonIndex, matches,
                stint.ClubId ?? (byIdentity.TryGetValue((stint.CountryCode.Trim().ToUpperInvariant(),
                    stint.ClubName.Trim().ToUpperInvariant()), out var clubId) ? clubId : null),
                stint.InitialDivisionLevel);
        }).ToArray();
    }
}
