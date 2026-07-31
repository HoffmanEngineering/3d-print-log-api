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
    public class ActivityAnalyticsServiceTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public ActivityAnalyticsServiceTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private async Task<ActivityResponse> Get(AnalyticsFilter filter)
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IActivityAnalyticsService>();
            filter.Normalize();
            return await service.GetActivity(Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);
        }

        [Fact]
        public async Task GetActivity_AlwaysReturnsAllEightHistogramBucketsAndAllMatrixCells()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "America/Chicago" });

            Assert.Equal(8, response.DurationHistogram.Count);
            Assert.Equal(168, response.StartTimeMatrix.Count);
        }

        [Fact]
        public async Task GetActivity_SeriesCountsAgreeWithTheCalendar()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "America/Chicago" });

            // Both are derived from the same dated prints, so they cannot disagree unless the
            // calendar window was truncated.
            if (!response.Coverage.Exclusions.Any(e => e.Reason == ExclusionReason.WindowTruncated))
                Assert.Equal(response.Series.Sum(b => b.Count), response.Calendar.Sum(d => d.Count));
        }

        [Fact]
        public async Task GetActivity_UndatedPrintsAreCountedSeparatelyAndNeverBucketed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var undated = await db.Prints.CountAsync(p =>
                p.CreatedById == Mcp.McpTestData.MetricsUserId && p.StartDate == null);
            var dated = await db.Prints.CountAsync(p =>
                p.CreatedById == Mcp.McpTestData.MetricsUserId && p.StartDate != null);

            Assert.True(undated > 0, "the fixture must contain an undated print or this asserts nothing");

            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            // The exact count, not ">= 0": an undated print is reported as a number and is
            // absent from every bucketed widget.
            Assert.Equal(undated, response.Coverage.UndatedCount);
            Assert.Equal(dated, response.Series.Sum(b => b.Count));
            Assert.Equal(dated, response.Calendar.Sum(d => d.Count));
        }

        [Fact]
        public async Task GetActivity_PrintsWithNoResolvableDurationAreExcludedFromTheHistogram()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "UTC" });

            var histogramTotal = response.DurationHistogram.Sum(b => b.Count);
            var missing = response.Coverage.Exclusions
                .FirstOrDefault(e => e.Reason == ExclusionReason.DurationMissing)?.Count ?? 0;

            Assert.Equal(response.Series.Sum(b => b.Count), histogramTotal + missing);
        }

        [Fact]
        public async Task GetActivity_ARangedQueryUsesHalfOpenBoundaries()
        {
            var boundary = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

            var before = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC", FromDate = boundary.AddDays(-30), ToDate = boundary,
            });
            var after = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC", FromDate = boundary, ToDate = boundary.AddDays(30),
            });
            var whole = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC", FromDate = boundary.AddDays(-30), ToDate = boundary.AddDays(30),
            });

            Assert.Equal(
                whole.Series.Sum(b => b.Count),
                before.Series.Sum(b => b.Count) + after.Series.Sum(b => b.Count));
        }

        [Fact]
        public async Task GetActivity_CalendarNeverExceedsFiftyThreeWeeks()
        {
            var response = await Get(new AnalyticsFilter { TimeZone = "America/Chicago" }); // all time

            Assert.True(response.Calendar.Count <= 371,
                $"calendar returned {response.Calendar.Count} days");

            if (response.Calendar.Count == 371)
                Assert.Contains(response.Coverage.Exclusions,
                    e => e.Reason == ExclusionReason.WindowTruncated);
        }

        [Fact]
        public async Task GetActivity_AFutureDatedPrintDoesNotWipeOutTheCurrentStreak()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // A dedicated printer so this asserts about its own prints only, and the filter is
            // pinned to it — the shared fixture must not shift under other tests.
            var printer = new PrintLogApi.Models.Printer
            {
                UserId = Mcp.McpTestData.MetricsUserId,
                Name = $"streak-{Guid.NewGuid():N}",
                Make = "Test",
                Model = "Streak",
                IsActive = true,
            };
            db.Printers.Add(printer);
            await db.SaveChangesAsync();

            PrintLogApi.Models.Print Print(string title, DateTimeOffset start) => new()
            {
                Title = title,
                StartDate = start,
                PrintTimeInSeconds = 3600,
                Status = PrintLogApi.Models.Print.PrintStatus.Success,
                ViewStatus = PrintLogApi.Models.Print.PrintViewStatus.Private,
                PrinterId = printer.Id,
                CreatedById = Mcp.McpTestData.MetricsUserId,
                UpdatedById = Mcp.McpTestData.MetricsUserId,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow,
            };

            // A genuine two-day run ending today, plus one print mis-dated a month ahead.
            var today = DateTimeOffset.UtcNow;
            var prints = new[]
            {
                Print("yesterday", today.AddDays(-1)),
                Print("today", today),
                Print("mis-dated into next month", today.AddDays(30)),
            };
            db.Prints.AddRange(prints);
            await db.SaveChangesAsync();

            try
            {
                var response = await Get(new AnalyticsFilter
                {
                    TimeZone = "UTC",
                    PrinterIds = { printer.Id },
                });

                // Without the guard the future date is the most recent day in the set, which is
                // neither today nor yesterday, so the current streak collapses to 0.
                Assert.Equal(2, response.Streaks.CurrentDays);
            }
            finally
            {
                db.RemoveRange(prints);
                db.Remove(printer);
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task GetActivity_AnUnownedPrinterFilterYieldsEmptyRatherThanAnError()
        {
            var response = await Get(new AnalyticsFilter
            {
                TimeZone = "UTC", PrinterIds = { long.MaxValue },
            });

            Assert.Equal(0, response.Series.Sum(b => b.Count));
            Assert.Equal(0, response.Streaks.LongestDays);
        }
    }
}
