using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class AnalyticsServiceOverviewTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public AnalyticsServiceOverviewTests(Mcp.McpDataWebApplicationFactory factory)
        {
            _factory = factory;

            // Touching Services builds the host, which is what runs the seeder that assigns
            // McpTestData's static ids. Without this, a test reading one of those ids in an
            // ARGUMENT position (e.g. Run(AllTime(), userId: McpTestData.MetricsUserId)) evaluates
            // it before Run's own first use of _factory.Services, and silently passes 0 — a user
            // that owns nothing, so every tile reads zero and the failure looks like a query bug.
            _ = _factory.Services;
        }

        private async Task<OverviewResponse> Run(AnalyticsFilter filter, long? userId = null)
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
            filter.Normalize();
            return await svc.GetOverview(userId ?? Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
        }

        private static AnalyticsFilter AllTime() => new() { TimeZone = "UTC" };

        [Fact]
        public async Task Overview_MaterialTotalUsesTheCanonicalAdditiveRule()
        {
            var result = await Run(AllTime());

            // Rows PLUS "other filament" — the same total /api/Users/{id}/total-filament-usage reports.
            Assert.Equal(
                Mcp.McpTestData.UsersEndpointMaterialTotalMg / 1000.0,
                result.Tiles.FilamentGrams.Value!.Value, 3);
        }

        [Fact]
        public async Task Overview_AllStatusKeysArePresentEvenAtZero()
        {
            var result = await Run(AllTime());

            foreach (var name in Enum.GetNames<Print.PrintStatus>())
                Assert.Contains(result.StatusBreakdown, s => s.Status == name);
        }

        [Fact]
        public async Task Overview_SuccessRateExcludesUnresolvedStatuses()
        {
            var result = await Run(AllTime());
            var counts = result.StatusBreakdown.ToDictionary(s => s.Status, s => s.Count);

            var denominator = counts["Success"] + counts["PartialSuccess"] + counts["Failed"] + counts["Cancelled"];
            Assert.True(denominator > 0, "the metrics fixture must have resolved prints for this to be meaningful");

            var expected = 100.0 * counts["Success"] / denominator;
            Assert.Equal(expected, result.Tiles.SuccessRatePercent.Value!.Value, 3);
        }

        [Fact]
        public async Task Overview_SuccessRateIsNullWhenNothingHasResolved()
        {
            // A user with no prints at all has no denominator; a rate of 0% would be a lie.
            var result = await Run(AllTime(), userId: Mcp.McpTestData.EmptyUserId);

            Assert.Null(result.Tiles.SuccessRatePercent.Value);
            Assert.Equal(0, result.Tiles.PrintCount.Value);
        }

        [Fact]
        public async Task Overview_RangedQueryExcludesUndatedPrints()
        {
            var filter = new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };

            var result = await Run(filter);

            Assert.Equal(0, result.Tiles.PrintCount.Coverage.UndatedCount);
        }

        [Fact]
        public async Task Overview_RangeIsHalfOpenSoAdjacentWindowsDoNotDoubleCount()
        {
            var mid = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            var first = new AnalyticsFilter { TimeZone = "UTC", FromDate = mid.AddDays(-30), ToDate = mid };
            var second = new AnalyticsFilter { TimeZone = "UTC", FromDate = mid, ToDate = mid.AddDays(30) };
            var whole = new AnalyticsFilter { TimeZone = "UTC", FromDate = mid.AddDays(-30), ToDate = mid.AddDays(30) };

            var a = await Run(first);
            var b = await Run(second);
            var all = await Run(whole);

            Assert.Equal(all.Tiles.PrintCount.Value, a.Tiles.PrintCount.Value + b.Tiles.PrintCount.Value);
        }

        [Fact]
        public async Task Overview_ComparePreviousPopulatesPreviousValues()
        {
            var to = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            var filter = new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = to.AddDays(-30),
                ToDate = to,
                ComparePrevious = true,
            };

            var result = await Run(filter);

            Assert.NotNull(result.Tiles.PrintCount.Previous);
        }

        [Fact]
        public async Task Overview_GranularityIsEchoedResolvedNeverAuto()
        {
            var result = await Run(AllTime());
            Assert.NotEqual("Auto", result.Granularity);
        }

        [Fact]
        public async Task Overview_OtherUsersPrintsAreNeverIncluded()
        {
            var mine = await Run(AllTime(), userId: Mcp.McpTestData.MetricsUserId);
            var theirs = await Run(AllTime(), userId: Mcp.McpTestData.EmptyUserId);

            Assert.True(mine.Tiles.PrintCount.Value > 0);
            Assert.Equal(0, theirs.Tiles.PrintCount.Value);
        }

        [Fact]
        public async Task Overview_UnownedPrinterFilterIsIgnoredNotErrored()
        {
            var filter = AllTime();
            filter.PrinterIds = new() { 999_999 }; // exists for nobody

            var result = await Run(filter);

            // Silently yields nothing rather than leaking whether that id exists.
            Assert.Equal(0, result.Tiles.PrintCount.Value);
        }

        [Fact]
        public async Task Overview_CostCoverageCountsPrints_NotDistinctReasons()
        {
            // The metrics user's prints have no priced spools, so every one of them is excluded
            // for the same reason. A coverage list that reports "PriceMissing: 1" for four
            // excluded prints is not a smaller truth, it is a false one — Coverage exists so the
            // reader can weigh how much of the total is missing.
            var result = await Run(AllTime());

            var exclusions = result.Tiles.TotalCost.Coverage.Exclusions;
            Assert.All(exclusions, e => Assert.True(
                e.Count > 0, $"exclusion '{e.Reason}' reported a non-positive count"));

            // The metrics user has no electricity rate configured, so every print that HAS a
            // duration is excluded from the cost for that reason. That is 3 of their 4 prints:
            // NoDurationPrint records neither an actual nor an estimate, so it never reaches the
            // rate check at all (an unrecorded duration yields no cost rather than a zero one).
            //
            // The exact number is the point. A count of 1 here would mean the reasons had been
            // collapsed into a distinct list, which is what this test exists to prevent.
            var rateMissing = Assert.Single(
                exclusions.Where(e => e.Reason == ExclusionReason.RateMissing));

            Assert.Equal(3, rateMissing.Count);
        }

        [Fact]
        public async Task Highlights_NestedFilamentAggregate_TranslatesToSql()
        {
            // The printer tie-breaker sums a child collection INSIDE a group aggregate. If the
            // provider cannot translate that, this call throws — that throw IS the assertion.
            // Client evaluation here would load every print in range into memory.
            var result = await Run(AllTime());

            Assert.NotNull(result.Highlights);
            Assert.NotNull(result.Highlights.MostUsedPrinter);
        }

        [Fact]
        public async Task Series_PutsBothHalvesOfARepeatedLocalHour_InTheSameLocalDay()
        {
            // TimeBucketerTests proves the bucketer in isolation. This proves the SQL grouping
            // grain and the in-memory bucket assignment still agree once a real provider has
            // stored and returned the DateTimeOffset. Both fixtures are 01:30 local on 1 Nov 2026
            // — one EDT, one EST — the repeated hour of a 25-hour day.
            var filter = new AnalyticsFilter
            {
                TimeZone = "America/New_York",
                FromDate = new DateTimeOffset(2026, 10, 31, 4, 0, 0, TimeSpan.Zero),
                ToDate = new DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero),
                Granularity = AnalyticsGranularity.Day,
            };

            var result = await Run(filter, Mcp.McpTestData.DstUserId);

            var nov1 = result.Series.Single(b => b.LocalStart == new DateOnly(2026, 11, 1));
            Assert.Equal(2, nov1.CountsByStatus.Values.Sum());
            Assert.All(
                result.Series.Where(b => b.LocalStart != new DateOnly(2026, 11, 1)),
                b => Assert.Equal(0, b.CountsByStatus.Values.Sum()));
        }
    }
}
