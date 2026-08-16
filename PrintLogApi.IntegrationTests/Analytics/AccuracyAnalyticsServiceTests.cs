using System;
using System.Globalization;
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
    public class AccuracyAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public AccuracyAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private async Task<AccuracyResponse> Get(AnalyticsFilter filter)
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAccuracyAnalyticsService>();
            filter.Normalize();
            return await service.GetAccuracy(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
        }

        [Fact]
        public async Task GetAccuracy_SuppressesAGroupBelowTheMinimumSampleSize()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // Assert.All over an empty — or entirely large-sample — collection proves nothing, so
            // pin that the fixture actually contains a group below the threshold first.
            Assert.NotEmpty(response.ByPrinter);
            Assert.Contains(response.ByPrinter, g => g.SampleSize < AccuracyStats.MinSampleSize);

            Assert.All(response.ByPrinter, group =>
            {
                if (group.SampleSize < AccuracyStats.MinSampleSize)
                {
                    Assert.Null(group.MedianRatio);
                    Assert.True(group.SuppressedForSmallSample);
                }
            });
        }

        [Fact]
        public async Task GetAccuracy_ReportsSuppressedGroupsAndOutliersInCoverage()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            var suppressed = response.ByPrinter.Count(g => g.SuppressedForSmallSample)
                + response.ByMaterial.Count(g => g.SuppressedForSmallSample);

            if (suppressed > 0)
                Assert.Contains(response.Coverage.Exclusions,
                    e => e.Reason == ExclusionReason.SampleTooSmall);
        }

        [Fact]
        public async Task GetAccuracy_MaterialSampleIncludesOtherFilamentScalars()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // A print whose ONLY material figures are the legacy scalars still belongs to the
            // material accuracy sample; counting rows alone would drop it.
            var scalarOnly = await db.Prints.CountAsync(p =>
                p.CreatedById == Mcp.McpTestData.MetricsUserId &&
                p.FilamentUsageMg > 0 && p.EstimatedFilamentUsageMg > 0 &&
                !p.FilamentUsage!.Any(pf => pf.AmountMg > 0));

            Assert.True(scalarOnly > 0,
                "seed a print whose material is recorded only as the legacy scalars");

            // Row-only eligibility, counted independently, so the assertion below is an exact
            // total rather than a lower bound that passes even if the scalars were dropped.
            var rowOnly = await db.Prints.CountAsync(p =>
                p.CreatedById == Mcp.McpTestData.MetricsUserId &&
                !(p.FilamentUsageMg > 0 && p.EstimatedFilamentUsageMg > 0) &&
                p.FilamentUsage!.Any(pf => pf.AmountMg > 0) &&
                p.FilamentUsage!.Any(pf => pf.EstimatedAmountMg > 0));

            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            var outliers = response.MaterialAccuracyMedian.Coverage.Exclusions
                .FirstOrDefault(e => e.Reason == ExclusionReason.OutlierExcluded)?.Count ?? 0;

            Assert.Equal(
                scalarOnly + rowOnly,
                response.MaterialAccuracyMedian.Coverage.Counted + outliers);
        }

        [Fact]
        public async Task GetAccuracy_ScatterIsBinnedAndNeverShipsRawPoints()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // 24 x 24 grid, so the payload is bounded regardless of library size.
            Assert.True(response.TimeScatter.Count <= 24 * 24);
            Assert.All(response.TimeScatter, bin => Assert.True(bin.Count >= 1));
        }

        [Fact]
        public async Task GetAccuracy_TimeAndMaterialAreSeparatePopulations()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // Count the two populations independently from the fixture. A print qualifies for
            // the TIME sample on its own duration columns and for the MATERIAL sample on its own
            // material columns; requiring both would silently shrink each one.
            var timeEligible = await db.Prints.CountAsync(p =>
                p.CreatedById == Mcp.McpTestData.MetricsUserId &&
                p.EstimatedPrintTimeInSeconds > 0 && p.PrintTimeInSeconds > 0);

            Assert.True(timeEligible > 0, "the fixture must contain a print with both durations");

            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // Counted plus the outliers it dropped accounts for every eligible print, and does
            // so without reference to the material population.
            var timeOutliers = response.TimeAccuracyMedian.Coverage.Exclusions
                .FirstOrDefault(e => e.Reason == ExclusionReason.OutlierExcluded)?.Count ?? 0;

            Assert.Equal(timeEligible, response.TimeAccuracyMedian.Coverage.Counted + timeOutliers);
        }

        [Fact]
        public async Task GetAccuracy_OnlyCalloutsGroupsThatAreBothLargeEnoughAndFarEnoughFromOne()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.All(response.Callouts, callout =>
            {
                Assert.True(callout.SampleSize >= AccuracyStats.MinSampleSize);
                Assert.True(Math.Abs(callout.MedianRatio - 1.0) >= 0.1);
            });
        }

        [Fact]
        public async Task GetAccuracy_CalloutsCarryStructuredFactsNotProse()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            Assert.All(response.Callouts, callout =>
            {
                Assert.Contains(callout.Scope, new[] { "printer", "material" });
                Assert.Contains(callout.Dimension, new[] { "time", "material" });
            });
        }

        /// <summary>
        /// The unowned-FILTER test below proves ids the caller supplies match nothing. This proves
        /// the other direction: a print the caller genuinely owns that REFERENCES someone else's
        /// printer must not surface that machine's name or its id as a clickable group.
        /// </summary>
        [Fact]
        public async Task GetAccuracy_NeverNamesAPrinterTheCallerDoesNotOwn()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var foreignPrinter = await db.Printers
                .FirstAsync(p => p.UserId != Mcp.McpTestData.MetricsUserId);

            var trespass = new PrintLogApi.Models.Print
            {
                CreatedById = Mcp.McpTestData.MetricsUserId,
                PrinterId = foreignPrinter.Id,
                Title = "Cross-tenant printer reference",
                Status = PrintLogApi.Models.Print.PrintStatus.Success,
                ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
                StartDate = DateTimeOffset.UtcNow.AddDays(-1),
                PrintTimeInSeconds = 3600,
                EstimatedPrintTimeInSeconds = 3000,
                CreatedDate = DateTime.UtcNow,
                UpdatedById = Mcp.McpTestData.MetricsUserId,
                UpdatedDate = DateTime.UtcNow,
            };
            db.Prints.Add(trespass);
            await db.SaveChangesAsync();

            try
            {
                var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

                Assert.DoesNotContain(
                    response.ByPrinter,
                    g => g.Key == foreignPrinter.Id.ToString(CultureInfo.InvariantCulture));
                Assert.All(response.ByPrinter, g =>
                    Assert.NotEqual(foreignPrinter.Name, g.Label));
                Assert.All(response.Callouts, c =>
                    Assert.NotEqual(foreignPrinter.Name, c.Label));
            }
            finally
            {
                db.Remove(trespass);
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task GetAccuracy_AnUnownedPrinterFilterYieldsEmptyRatherThanAnError()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC", PrinterIds = { long.MaxValue } });

            Assert.Empty(response.ByPrinter);
            Assert.Null(response.TimeAccuracyMedian.Value);
        }
    }
}
