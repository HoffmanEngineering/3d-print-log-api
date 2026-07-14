using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using Xunit;

namespace PrintLogApi.IntegrationTests
{
    public class PrintMetricsTests
    {
        [Theory]
        // actual, estimated, expected value, expected isEstimated
        [InlineData(null, 6933, 6933, true)]   // production print 402378: never completed, real estimate
        [InlineData(0, 3600, 3600, true)]      // a webhook's coerced 0 must NOT suppress the estimate
        [InlineData(-5, 3600, 3600, true)]     // negative is corrupt, not a duration
        [InlineData(7200, 3600, 7200, false)]  // a real actual always wins
        [InlineData(7200, null, 7200, false)]
        [InlineData(null, null, 0, false)]     // nothing recorded: 0, and NOT flagged estimated
        [InlineData(null, 0, 0, false)]        // Moonraker's hardcoded 0 is not an estimate
        [InlineData(0, 0, 0, false)]
        [InlineData(0, -5, 0, false)]
        public void Resolve_AppliesTheRule(int? actual, int? estimated, int expectedValue, bool expectedIsEstimated)
        {
            Assert.Equal(expectedValue, PrintMetrics.Resolve(actual, estimated));
            Assert.Equal(expectedIsEstimated, PrintMetrics.IsEstimated(actual, estimated));
        }
    }

    /// <summary>
    /// Proves the shared expressions reach SQL. Never call .Compile() on them here: compiling forces
    /// client evaluation, so the test would pass even if translation were completely broken — which
    /// is the only thing these tests exist to prove. Passing the expression straight to
    /// SumAsync/CountAsync means EF must translate it or throw.
    /// </summary>
    public class PrintMetricsTranslationTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public PrintMetricsTranslationTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task DurationSecondsExpr_TranslatesToSql_AndSumsByTheRule()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // An untranslatable expression throws InvalidOperationException here rather than
            // client-evaluating. That throw IS the assertion this test exists for.
            var total = await db.Prints.AsNoTracking()
                .Where(p => p.CreatedById == Mcp.McpTestData.MetricsUserId)
                .SumAsync(PrintMetrics.DurationSecondsExpr);

            // Exact, not "> 0": the metrics user owns nothing but the matrix.
            Assert.Equal(Mcp.McpTestData.DurationMatrixTotalSeconds, total);
        }

        [Fact]
        public async Task DurationIsEstimatedExpr_TranslatesToSql_AndCountsOnlyRealEstimates()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var estimated = await db.Prints.AsNoTracking()
                .Where(p => p.CreatedById == Mcp.McpTestData.MetricsUserId)
                .CountAsync(PrintMetrics.DurationIsEstimatedExpr);

            // NoDuration must NOT count: it has no estimate to fall back to, so its 0 is not an estimate.
            Assert.Equal(Mcp.McpTestData.DurationMatrixEstimatedCount, estimated);
        }

        [Fact]
        public async Task InlinedRule_InPrinterStats_MatchesTheSharedExpression()
        {
            // McpStatisticsService inlines the ternary because EF cannot take the shared expression
            // in a group projection. If that copy ever drifts from PrintMetrics, this fails. A unit
            // theory over Resolve alone would NOT catch a divergent copy living inside a query.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var stats = scope.ServiceProvider.GetRequiredService<PrintLogApi.Services.IMcpStatisticsService>();

            var viaExpression = await db.Prints.AsNoTracking()
                .Where(p => p.CreatedById == Mcp.McpTestData.MetricsUserId)
                .SumAsync(PrintMetrics.DurationSecondsExpr);

            var page = await stats.GetPrinterStats(
                Mcp.McpTestData.MetricsUserId, null, null, null, 1, 100, default);
            var viaService = page.Items.Sum(i => i.TotalPrintTimeSeconds);

            Assert.Equal(viaExpression, viaService);
        }
    }

    /// <summary>
    /// The non-MCP readers. ProjectProfile had the SAME no-fallback defect as the MCP summary, and
    /// the UsersPrint totals carried the stored-zero hazard, so each needs its own regression: a
    /// unit theory over Resolve would not catch a divergent copy living inside a query or a mapping.
    /// </summary>
    public class RemainingMetricsReadersTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public RemainingMetricsReadersTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private const string FullRange = "?fromDate=2000-01-01T00:00:00Z&toDate=2100-01-01T00:00:00Z";

        [Fact]
        public async Task TotalPrintTime_TreatsAStoredZeroAsNotRecorded()
        {
            // Was: PrintTimeInSeconds.HasValue ? actual : estimated. A stored 0 IS HasValue, so the
            // webhook's coerced zero silently suppressed a perfectly good estimate.
            var client = _factory.CreateClient();
            var response = await client.GetAsync(
                $"/api/Users/{Mcp.McpTestData.MetricsUserId}/total-print-time{FullRange}");

            response.EnsureSuccessStatusCode();
            var stat = await response.Content.ReadFromJsonAsync<SinglePrintStat>();

            // Exact: the metrics user owns nothing but the matrix. 6933 + 1800 + 7200 + 0.
            Assert.Equal(Mcp.McpTestData.DurationMatrixTotalSeconds, int.Parse(stat!.Stat));
        }

        [Fact]
        public async Task TotalFilamentUsage_GuardsANegativeEstimate()
        {
            // The actual was guarded > 0 but EstimatedAmountMg was taken unguarded, so
            // NoDurationPrint's -500 estimate would subtract from the total.
            var client = _factory.CreateClient();
            var response = await client.GetAsync(
                $"/api/Users/{Mcp.McpTestData.MetricsUserId}/total-filament-usage{FullRange}");

            response.EnsureSuccessStatusCode();
            var stat = await response.Content.ReadFromJsonAsync<SinglePrintStat>();

            // 5000 + 3000 + 9000 + 4000 = 21000. The -500 must NOT be added, and the stored 0 must
            // fall through to 3000 rather than contributing nothing.
            Assert.Equal(Mcp.McpTestData.MaterialMatrixTotalMg, long.Parse(stat!.Stat));
        }

        [Fact]
        public async Task ProjectTotals_FallBackToTheEstimate()
        {
            using var scope = _factory.Services.CreateScope();
            var mapper = scope.ServiceProvider.GetRequiredService<AutoMapper.IMapper>();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // FilamentUsage must be included too: the profile also maps TotalFilamentWeightMg by
            // walking Prints -> FilamentUsage, and an unloaded collection is null, not empty.
            var project = await db.Projects.AsNoTracking()
                .Include(p => p.Prints)
                    .ThenInclude(p => p.FilamentUsage)
                .FirstAsync(p => p.Id == Mcp.McpTestData.MetricsProjectId);

            var dto = mapper.Map<ProjectSummaryDto>(project);

            // Was `?? 0` with no fallback at all, so this read 7200 — only the one measured print.
            Assert.Equal(Mcp.McpTestData.DurationMatrixTotalSeconds, dto.TotalPrintTimeInSeconds);

            // The estimate-only field keeps its OWN meaning ("what did the slicer predict?") and
            // must NOT become a resolved value: 6933 + 1800 + 3600 + 0.
            Assert.Equal(
                Mcp.McpTestData.DurationMatrixEstimateOnlyTotalSeconds,
                dto.TotalEstimatedPrintTimeInSeconds);
        }
    }
}
