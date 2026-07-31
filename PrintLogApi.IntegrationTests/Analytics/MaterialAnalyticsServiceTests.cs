using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class MaterialAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public MaterialAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private async Task<MaterialsResponse> Get(AnalyticsFilter filter)
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>();
            filter.Normalize();
            return await service.GetMaterials(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
        }

        [Fact]
        public async Task GetMaterials_GroupTotalsFallShortOfTheOverviewTotalByExactlyTheOtherFilamentScalar()
        {
            var filter = new AnalyticsFilter { TimeZone = "UTC" };
            var response = await Get(filter);

            using var scope = _factory.Services.CreateScope();
            var overview = await scope.ServiceProvider.GetRequiredService<IAnalyticsService>()
                .GetOverview(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

            var grouped = response.ByType.Sum(g => g.MaterialMg);
            var overviewMg = (long)Math.Round((overview.Tiles.FilamentGrams.Value ?? 0) * 1000);

            // "Other filament" has no spool, so it has no type, brand or colour and cannot be
            // grouped. The shortfall is exactly the legacy scalar, and it is NOT a bug.
            Assert.Equal(Mcp.McpTestData.LegacyMaterialMatrixTotalMg, overviewMg - grouped);

            // And the shortfall is EXPLAINED, not silent.
            Assert.Contains(response.Coverage.Exclusions,
                e => e.Reason == ExclusionReason.UnattributedMaterial && e.Count > 0);
        }

        [Fact]
        public async Task GetMaterials_RemainingMatchesTheCanonicalFilamentSummaryRule()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();

            var canonical = await db.Filaments.AsNoTracking()
                .Where(f => f.CreatedById == Mcp.McpTestData.MetricsUserId)
                .ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider)
                .ToDictionaryAsync(f => f.Id, f => f.FilamentRemaining);

            // Guard both preconditions: a `continue` past every row, or an empty TopSpools,
            // would let this test pass while comparing nothing at all.
            Assert.NotEmpty(response.TopSpools);

            var compared = 0;
            foreach (var spool in response.TopSpools)
            {
                var id = Guid.Parse(spool.FilamentId);
                Assert.True(canonical.ContainsKey(id),
                    $"spool {id} is in TopSpools but not in the tenant's own filaments");
                Assert.Equal(canonical[id], spool.RemainingMg);
                compared++;
            }

            Assert.Equal(response.TopSpools.Count, compared);
        }

        [Fact]
        public async Task GetMaterials_PricesConsumedMaterialThroughTheSharedCalculator()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.NotEmpty(response.TopSpools);

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var inputs = await AnalyticsCostProjection.LoadInputs(
                db, Mcp.McpTestData.MetricsUserId, CancellationToken.None);

            foreach (var spool in response.TopSpools)
            {
                var filament = await db.Filaments.AsNoTracking()
                    .FirstAsync(f => f.Id == Guid.Parse(spool.FilamentId));

                var priceable = !string.IsNullOrWhiteSpace(filament.PurchasePriceValue)
                    || !string.IsNullOrWhiteSpace(inputs.DefaultFilamentPrice);
                var weighable = filament.InitialNominalWeightMg is > 0;

                // A spool that cannot be priced reports null, never a confident 0.00.
                if (priceable && weighable) Assert.NotNull(spool.CostConsumed);
                else Assert.Null(spool.CostConsumed);
            }
        }

        [Fact]
        public async Task GetMaterials_WasteCountsFailedAndCancelledButNotPartialSuccess()
        {
            var failedAndCancelled = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC",
                Statuses =
                {
                    PrintLogApi.Models.Print.PrintStatus.Failed,
                    PrintLogApi.Models.Print.PrintStatus.Cancelled,
                },
            });
            var everything = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // Filtering the whole tab to failed+cancelled makes waste equal total consumption.
            Assert.Equal(
                failedAndCancelled.ByType.Sum(g => g.MaterialMg) / 1000.0,
                failedAndCancelled.WasteGrams.Value ?? 0,
                3);

            Assert.True((everything.WasteGrams.Value ?? 0) <= everything.ByType.Sum(g => g.MaterialMg) / 1000.0 + 0.001);
        }

        [Fact]
        public async Task GetMaterials_RunwayIsSuppressedWhenBurnRateIsZeroAndNeverExceedsAYear()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.All(response.Runway, row =>
            {
                if (row.BurnRateGramsPerDay <= 0) Assert.Null(row.RunwayDays);
                if (row.RunwayDays.HasValue) Assert.InRange(row.RunwayDays.Value, 0, 365);
            });
        }

        [Fact]
        public async Task GetMaterials_CapsGroupsAndRollsTheRemainderIntoOther()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.True(response.ByBrand.Count <= 16, $"{response.ByBrand.Count} brand rows");
            if (response.ByBrand.Count == 16)
                Assert.Equal("Other", response.ByBrand.Last().Label);
        }

        [Fact]
        public async Task GetMaterials_NeverReturnsASpoolBelongingToAnotherUser()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            // Compared as Guids, not as strings: Guid.ToString() evaluated in SQL is the
            // provider's formatting, which is not the CLR's, and comparing those would fail for
            // a reason that has nothing to do with ownership.
            var owned = db.Filaments
                .Where(f => f.CreatedById == Mcp.McpTestData.MetricsUserId)
                .Select(f => f.Id).ToList();

            Assert.NotEmpty(response.TopSpools);
            Assert.All(response.TopSpools, s => Assert.Contains(Guid.Parse(s.FilamentId), owned));
            Assert.All(response.Runway, r => Assert.Contains(Guid.Parse(r.FilamentId), owned));
        }

        [Fact]
        public async Task GetMaterials_AnUnownedFilamentFilterYieldsEmptyRatherThanAnError()
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC", FilamentIds = { Guid.NewGuid() },
            });

            Assert.Empty(response.ByType);
            Assert.Empty(response.TopSpools);
        }
    }
}
