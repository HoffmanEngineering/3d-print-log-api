using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
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
    }
}
