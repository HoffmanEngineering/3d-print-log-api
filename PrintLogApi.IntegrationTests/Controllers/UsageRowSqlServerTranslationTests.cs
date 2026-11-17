using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The per-print usage totals (#229), compiled through the SQL Server provider, guarding the
/// error 8124 shape the way <see cref="FilamentSqlServerTranslationTests"/> does for the spool.
///
/// A per-print total now resolves each row through <c>FilamentProfile.UsageRowMg</c>, which reads
/// density and diameter. Read off the row's own <c>Filament</c> navigation they sit behind the
/// subquery's own join; read off anything outside the aggregate they would be the multi-column
/// outer reference SQL Server refuses. These assert that every converted aggregate converts
/// (density and diameter present) and carries no reference to the outer print.
///
/// ToQueryString cannot execute anything, so it cannot raise 8124 itself.
/// <c>SpoolFiguresSqlServerTests</c> runs the same figures, and every service-internal site, on a
/// real server.
/// </summary>
public class UsageRowSqlServerTranslationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private static PrintLogContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

    private IMapper Mapper()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IMapper>();
    }

    [Fact]
    public void MaterialMgExpr_ConvertsEachRow_WithoutAnOuterReference()
    {
        using var context = SqlServerContext();
        var sql = context.Prints.AsNoTracking().Select(PrintMetrics.MaterialMgExpr).ToQueryString();

        AssertUsageAggregatesConvert(sql, expected: 1);
    }

    /// <summary>
    /// The resin totals (#294) choose a row's side through UsageRowActualMg, so they convert as the
    /// gram totals do and must keep density and diameter inside the aggregate too.
    /// </summary>
    [Fact]
    public void MaterialMlExpr_AndItsOwnedVariant_ConvertWithoutAnOuterReference()
    {
        using var context = SqlServerContext();

        foreach (var expression in new[] { PrintMetrics.MaterialMlExpr, PrintMetrics.OwnedMaterialMlExpr(42) })
        {
            var sql = context.Prints.AsNoTracking().Select(expression).ToQueryString();

            Assert.Contains("[VolumeMl]", sql);
            Assert.Contains("[EstimatedVolumeMl]", sql);
            AssertUsageAggregatesConvert(sql, expected: 1);
        }
    }

    [Fact]
    public void MaterialIsEstimatedExpr_ReadsTheRowsActualMeasures()
    {
        using var context = SqlServerContext();
        var sql = context.Prints.AsNoTracking().Where(PrintMetrics.MaterialIsEstimatedExpr).ToQueryString();

        Assert.Contains("[LengthInM]", sql);
        Assert.Contains("[VolumeMl]", sql);
        Assert.Contains("MaterialDensityGramPerCubicCm", sql);
    }

    [Fact]
    public void PrintStatistic_ActualAndEstimatedSums_Convert()
    {
        using var context = SqlServerContext();
        var sql = context.Prints.AsNoTracking().ProjectTo<PrintStatistic>(Mapper().ConfigurationProvider).ToQueryString();

        AssertUsageAggregatesConvert(sql, expected: 2);
    }

    [Fact]
    public void PrintDetailReport_ActualAndEstimatedSums_Convert()
    {
        using var context = SqlServerContext();
        var sql = context.Prints.AsNoTracking().ProjectTo<PrintDetailReport>(Mapper().ConfigurationProvider).ToQueryString();

        AssertUsageAggregatesConvert(sql, expected: 2);
    }

    /// <summary>
    /// Every <c>SUM(</c> whose argument converts a usage row must read density and diameter and
    /// nothing of the outer print (<c>[p].</c>); at least <paramref name="expected"/> must exist,
    /// so an aggregate that stopped converting fails rather than passing vacuously.
    /// </summary>
    private static void AssertUsageAggregatesConvert(string sql, int expected)
    {
        var aggregates = Aggregates(sql).Where(a => a.Contains("[LengthInM]") || a.Contains("[EstimatedLengthInM]")).ToList();
        Assert.True(aggregates.Count >= expected, $"Expected {expected} converting usage aggregate(s):\n{sql}");
        foreach (var aggregate in aggregates)
        {
            Assert.Contains("MaterialDensityGramPerCubicCm", aggregate);
            Assert.Contains("DiameterMm", aggregate);
            Assert.DoesNotContain("[p].", aggregate);
        }
    }

    /// <summary>The text of each <c>SUM(...)</c>, parentheses matched.</summary>
    private static IEnumerable<string> Aggregates(string sql)
    {
        for (var start = sql.IndexOf("SUM(", StringComparison.Ordinal); start >= 0;
             start = sql.IndexOf("SUM(", start + 4, StringComparison.Ordinal))
        {
            var depth = 0;
            for (var i = start + 3; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')' && --depth == 0)
                {
                    yield return sql[start..(i + 1)];
                    break;
                }
            }
        }
    }

    [Fact]
    public void Aggregates_MatchesNestedParentheses()
    {
        var found = Aggregates("SELECT SUM(CASE WHEN (a) > 0 THEN ROUND(b, 0) END) FROM t").ToList();

        Assert.Equal(new[] { "SUM(CASE WHEN (a) > 0 THEN ROUND(b, 0) END)" }, found);
    }
}
