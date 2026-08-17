using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// The legacy scalar Print.FilamentUsageMg is "other filament" — material used on the print
/// that was never attached to a tracked spool — so adding it to the per-row sum is correct.
/// What was wrong is that it was added UNGUARDED: a corrupt negative subtracted from the
/// user's totals instead of being treated as "not recorded".
///
/// The actual and estimated columns stay SEPARATE here. PrintStatistic exposes them as a
/// parallel pair and consumers resolve between them; collapsing them into one resolved value
/// would destroy the estimate-accuracy comparison the analytics redesign is built on.
/// </summary>
public class PrintProfileMaterialTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;

    public PrintProfileMaterialTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PrintStatistic_GuardsNegativeLegacyScalars_InBothColumns()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        var stats = await db.Prints.AsNoTracking()
            .Where(p => p.CreatedById == Mcp.McpTestData.MetricsUserId)
            .ProjectTo<PrintStatistic>(mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // ActualWins carries FilamentUsageMg = -1 and NoDuration EstimatedFilamentUsageMg = -500.
        // Unguarded, they subtract: 12999 and 9500.
        Assert.Equal(
            Mcp.McpTestData.StatisticActualMaterialTotalMg,
            stats.Sum(s => (long)(s.FilamentUsageMg ?? 0)));
        Assert.Equal(
            Mcp.McpTestData.StatisticEstimatedMaterialTotalMg,
            stats.Sum(s => (long)(s.EstimatedFilamentUsageMg ?? 0)));
    }

    [Fact]
    public async Task PrintDetailReport_GuardsNegativeLegacyScalars_SoCsvMatchesStats()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        var rows = await db.Prints.AsNoTracking()
            .Where(p => p.CreatedById == Mcp.McpTestData.MetricsUserId)
            .ProjectTo<PrintDetailReport>(mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            Mcp.McpTestData.StatisticActualMaterialTotalMg / 1000.0,
            rows.Sum(r => r.FilamentUsageG ?? 0), 3);
        Assert.Equal(
            Mcp.McpTestData.StatisticEstimatedMaterialTotalMg / 1000.0,
            rows.Sum(r => r.EstimatedFilamentUsageG ?? 0), 3);
    }

    [Fact]
    public async Task PrintStatistic_UndatedPrint_SurfacesAsNull_NotYearOne()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

        var stats = await db.Prints.AsNoTracking()
            .Where(p => p.StartDate == null)
            .ProjectTo<PrintStatistic>(mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(stats); // the seeder has undated prints
        Assert.All(stats, s => Assert.Null(s.StartDate));
    }
}
