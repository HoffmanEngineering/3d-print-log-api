using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class CostAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public CostAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private async Task<CostsResponse> Get(AnalyticsFilter filter)
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ICostAnalyticsService>();
            filter.Normalize();
            return await service.GetCosts(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
        }

        [Fact]
        public async Task GetCosts_TotalEqualsTheSumOfItsThreeComponents()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // `?? 0m` on BOTH sides would collapse "nothing could be priced" into a passing
            // 0 == 0. Assert the null contract explicitly first.
            var anyComponent = response.FilamentSpend.Value.HasValue
                || response.ElectricitySpend.Value.HasValue
                || response.MaintenanceSpend.Value.HasValue;

            if (!anyComponent)
            {
                Assert.Null(response.TotalSpend.Value);
                return;
            }

            Assert.NotNull(response.TotalSpend.Value);
            Assert.Equal(
                response.TotalSpend.Value!.Value,
                (response.FilamentSpend.Value ?? 0m)
                    + (response.ElectricitySpend.Value ?? 0m)
                    + (response.MaintenanceSpend.Value ?? 0m));
        }

        [Fact]
        public async Task GetCosts_FilamentAndElectricityAgreeWithTheOverviewTotal()
        {
            var filter = new AnalyticsFilter { TimeZone = "UTC" };
            var response = await Get(filter);

            using var scope = _factory.Services.CreateScope();
            var overviewFilter = new AnalyticsFilter { TimeZone = "UTC" };
            overviewFilter.Normalize();
            var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
                .GetOverview(Mcp.McpTestData.MetricsUserId, overviewFilter, CancellationToken.None);

            // /overview's cost tile is filament + electricity. Maintenance is a Costs-tab
            // addition, so it is excluded from the comparison rather than hand-waved.
            //
            // Null is asserted as null on both sides: coercing with `?? 0m` would make the two
            // surfaces "agree" precisely in the case where neither can price anything, which is
            // the case most likely to hide a real divergence.
            var hasComponent = response.FilamentSpend.Value.HasValue
                || response.ElectricitySpend.Value.HasValue;

            Assert.Equal(overview.Tiles.TotalCost.Value.HasValue, hasComponent);

            if (hasComponent)
            {
                Assert.Equal(
                    overview.Tiles.TotalCost.Value!.Value,
                    (response.FilamentSpend.Value ?? 0m) + (response.ElectricitySpend.Value ?? 0m));
            }
        }

        [Fact]
        public async Task GetCosts_AlwaysReturnsAllEightCostBandsInOrder()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.Equal(
                new[] { "<1", "1–2", "2–5", "5–10", "10–25", "25–50", "50–100", "100+" },
                response.CostPerPrint.Select(b => b.Label).ToArray());
        }

        [Fact]
        public async Task GetCosts_FailureShareIsNullRatherThanZeroWhenNothingWasSpent()
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(1900, 2, 1, 0, 0, 0, TimeSpan.Zero),
            });

            Assert.Null(response.CostOfFailureSharePercent);
        }

        [Fact]
        public async Task GetCosts_ExtremesOnlyIncludePrintsWithAKnownCost()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.True(response.MostExpensive.Count <= 5);
            Assert.True(response.LeastExpensive.Count <= 5);
            Assert.All(response.MostExpensive, p => Assert.True(p.Amount >= 0m));

            if (response.MostExpensive.Count > 1)
                Assert.True(response.MostExpensive[0].Amount >= response.MostExpensive[1].Amount);
            if (response.LeastExpensive.Count > 1)
                Assert.True(response.LeastExpensive[0].Amount <= response.LeastExpensive[1].Amount);
        }

        [Fact]
        public async Task GetCosts_GroupsByBrandAndByMaterialTypeOnDifferentKeys()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var materialTypes = await db.Filaments
                .Where(f => f.CreatedById == Mcp.McpTestData.MetricsUserId)
                .Select(f => f.MaterialType ?? "Unknown").Distinct().ToListAsync();

            // Guard both preconditions: Assert.All over an empty list proves nothing, and this
            // bug produces two populated-but-identical charts, not empty ones.
            Assert.NotEmpty(response.ByMaterialType);
            Assert.NotEmpty(response.ByBrand);

            Assert.All(response.ByMaterialType, g => Assert.Contains(g.Key, materialTypes));
            // A brand key that is also a material type would make this vacuous, so assert the
            // brand groups are NOT drawn from the material-type vocabulary.
            Assert.All(response.ByBrand, g => Assert.DoesNotContain(g.Key, materialTypes));
        }

        [Fact]
        public async Task GetOverview_NowReportsAPriciestPrintHighlight()
        {
            using var scope = _factory.Services.CreateScope();
            var filter = new AnalyticsFilter { TimeZone = "UTC" };
            filter.Normalize();

            var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
                .GetOverview(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

            // Null is acceptable only when nothing in range has a knowable cost.
            if (overview.Tiles.TotalCost.Value.HasValue)
                Assert.NotNull(overview.Highlights.PriciestPrint);
        }
    }
}
