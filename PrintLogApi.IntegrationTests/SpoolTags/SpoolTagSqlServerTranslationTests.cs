using Microsoft.EntityFrameworkCore;
using PrintLogApi.SpoolTags.Matching;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// Compiles the spool tag queries through the SQL Server provider. The suite runs on SQLite, which
/// accepts shapes SQL Server rejects, and no database is involved here: ToQueryString only
/// translates. A query EF cannot translate throws, and these assertions pin the SQL features each
/// one relies on (case-insensitive manufacturer match, COUNT(DISTINCT) per entry, EXISTS for tags).
/// </summary>
public class SpoolTagSqlServerTranslationTests
{
    private static PrintLogContext SqlServerContext() => new(new DbContextOptionsBuilder<PrintLogContext>()
        .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
        .Options);

    [Fact]
    public void CatalogCandidates_MatchManufacturerWithUpper()
    {
        using var context = SqlServerContext();

        var sql = FilamentCatalogMatcher.CandidatesQuery(context, "SUNLU").ToQueryString();

        Assert.Contains("UPPER(", sql);
        Assert.Contains("[RemovedAt] IS NULL", sql);
    }

    [Fact]
    public void SkuTally_GroupsByEntryWithDistinctUserCount()
    {
        using var context = SqlServerContext();

        var sql = FilamentCatalogMatcher.TallyQuery(context, "snapmaker", "12345").ToQueryString();

        Assert.Contains("GROUP BY", sql);
        Assert.Contains("COUNT(DISTINCT", sql);
    }

    [Fact]
    public void SuggestionCandidates_ReadTagsThroughExists()
    {
        using var context = SqlServerContext();

        var sql = TagSuggestionRanker.CandidatesQuery(context, 1).ToQueryString();

        Assert.Contains("EXISTS", sql);
        Assert.Contains("[Colors]", sql);
    }
}
