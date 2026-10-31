using System.Text.Json;

namespace FmCareerChallenge.Api.Domain;

public static class GeographyCatalog
{
    public static readonly Continent[] Continents =
    [
        new() { Code = "AF", Name = "Africa" },
        new() { Code = "AN", Name = "Antarctica" },
        new() { Code = "AS", Name = "Asia" },
        new() { Code = "EU", Name = "Europe" },
        new() { Code = "NA", Name = "North America" },
        new() { Code = "OC", Name = "Oceania" },
        new() { Code = "SA", Name = "South America" },
    ];

    public static readonly Country[] Countries = LoadCountries();

    private static Country[] LoadCountries()
    {
        using var stream = typeof(GeographyCatalog).Assembly
            .GetManifestResourceStream("FmCareerChallenge.Api.Domain.countries.json")
            ?? throw new InvalidOperationException("The country catalog resource is missing.");
        return JsonSerializer.Deserialize<Country[]>(stream)
            ?? throw new InvalidOperationException("The country catalog resource is empty.");
    }
}
