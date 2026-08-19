using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Guards the remaining-material aggregate against SQL Server error 8124, which the rest of
/// this suite structurally cannot see.
///
/// "Multiple columns are specified in an aggregated expression containing an outer reference."
/// SQL Server refuses a correlated aggregate whose summed expression reads more than one column
/// from the outer query. Resolving a usage row needs the spool's density AND its diameter, so
/// writing those as `src.MaterialDensityGramPerCubicCm` / `src.DiameterMm` inside the Sum
/// produces SQL that EF translates happily, every test on SQLite passes, and every request to
/// GET /api/Filaments returns a 500 in production. That is not hypothetical - it shipped.
///
/// Reading them through the usage row's own navigation instead (`p.Filament!`) puts them behind
/// the subquery's own join, so the aggregate has no outer reference left to trip on.
///
/// No database is involved: ToQueryString compiles the query through the SQL Server provider.
/// It cannot execute the SQL, so it cannot catch 8124 by itself - hence the assertion is on the
/// shape that causes it, the outer alias appearing inside the aggregate.
/// </summary>
public class FilamentSqlServerTranslationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// The production mapping, compiled through the production provider. The mapper comes from
    /// the running app rather than a locally built configuration so this cannot pass against a
    /// copy of the profile that has drifted from the one that serves requests.
    /// </summary>
    private string SummarySql()
    {
        using var scope = factory.Services.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        return context.Filaments.AsNoTracking()
            .ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider)
            .ToQueryString();
    }

    [Fact]
    public void FilamentSummary_UsageAggregate_CarriesNoOuterReference()
    {
        var sql = SummarySql();

        var start = sql.IndexOf("SUM(CASE", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No usage aggregate found in the projection:\n{sql}");

        var end = sql.IndexOf("END)", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Usage aggregate is not closed as expected:\n{sql}");

        var aggregate = sql[start..end];

        // [f] is the outer filament. Anything of its inside the SUM is the 8124 shape.
        Assert.DoesNotContain("[f].", aggregate);

        // And the conversion factors must still be there, from the subquery's own join -
        // an aggregate that stopped converting would also pass the assertion above.
        Assert.Contains("MaterialDensityGramPerCubicCm", aggregate);
        Assert.Contains("DiameterMm", aggregate);
    }
}
