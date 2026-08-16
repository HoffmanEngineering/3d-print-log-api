using System;
using System.Collections.Generic;
using System.Linq;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class AnalyticsFilterTests
    {
        private static AnalyticsFilter Valid() => new()
        {
            FromDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ToDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZone = "America/Chicago",
        };

        // A fixed clock, so every clamp assertion is deterministic regardless of when the suite
        // runs or which hour boundary it straddles.
        private static readonly DateTimeOffset Now =
            new(2026, 7, 30, 14, 37, 12, TimeSpan.Zero);

        [Fact]
        public void Normalize_ClampsAFutureToDateToTheNextWholeHour()
        {
            var f = Valid();
            f.ToDate = Now.AddYears(10);
            f.Normalize(Now);

            Assert.Equal(new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero), f.ToDate);
        }

        [Fact]
        public void ClampCeiling_RoundsInUtcRegardlessOfTheInputsOffset()
        {
            // 14:37-05:00 is 19:37Z and must round to 20:00Z, not 15:00Z. Reading the calendar
            // fields before converting is the bug this pins.
            var offsetNow = new DateTimeOffset(2026, 7, 30, 14, 37, 12, TimeSpan.FromHours(-5));

            Assert.Equal(
                new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero),
                AnalyticsFilter.ClampCeiling(offsetNow));
        }

        [Fact]
        public void Normalize_ClampsATimestampLessThanAnHourInTheFuture()
        {
            // The gap the rounded-ceiling comparison used to let through: 20 minutes ahead is
            // still the future, and §6.6 says future dates are clamped.
            var f = Valid();
            f.ToDate = Now.AddMinutes(20);
            f.Normalize(Now);

            Assert.Equal(new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero), f.ToDate);
        }

        [Fact]
        public void Normalize_MakesTwoFutureReachingRangesShareACacheKey()
        {
            var a = Valid();
            a.ToDate = Now.AddYears(5);
            var b = Valid();
            b.ToDate = Now.AddYears(9);

            a.Normalize(Now);
            b.Normalize(Now);

            // The actual claim: ONE cache entry, not merely similar dates. CacheKey serializes
            // with "O", so it exposes any sub-second difference the clamp failed to remove.
            Assert.Equal(a.CacheKey(7), b.CacheKey(7));
        }

        [Fact]
        public void Validate_RejectsARangeLyingEntirelyInTheFuture()
        {
            var f = Valid();
            f.FromDate = Now.AddYears(1);
            f.ToDate = Now.AddYears(2);
            f.Normalize(Now);

            // ToDate clamps back, FromDate does not move, so the range is inverted and rejected —
            // "there is no data for next year" is a 400, not a silent empty chart.
            Assert.Contains(f.Validate(), e => e.Contains("fromDate"));
        }

        [Fact]
        public void Normalize_LeavesAWhollyPastRangeAlone()
        {
            var f = Valid();
            f.FromDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            f.ToDate = new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero);

            f.Normalize(Now);

            Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), f.FromDate);
            Assert.Equal(new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero), f.ToDate);
        }

        [Fact]
        public void Validate_AcceptsAWellFormedFilter()
        {
            Assert.Empty(Valid().Validate());
        }

        [Fact]
        public void Validate_RejectsInvertedRange()
        {
            var f = Valid();
            (f.FromDate, f.ToDate) = (f.ToDate, f.FromDate);
            Assert.Contains(f.Validate(), e => e.Contains("fromDate"));
        }

        [Fact]
        public void Validate_RejectsTooManyPrinterIds()
        {
            var f = Valid();
            f.PrinterIds = Enumerable.Range(1, 51).Select(i => (long)i).ToList();
            Assert.Contains(f.Validate(), e => e.Contains("printerIds"));
        }

        [Fact]
        public void Validate_RejectsUnknownTimeZone()
        {
            var f = Valid();
            f.TimeZone = "Mars/Olympus_Mons";
            Assert.Contains(f.Validate(), e => e.Contains("timeZone"));
        }

        [Fact]
        public void Validate_RejectsRangeLongerThanTwentyYears()
        {
            var f = Valid();
            f.FromDate = new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.Contains(f.Validate(), e => e.Contains("range"));
        }

        [Theory]
        [InlineData(31, AnalyticsGranularity.Day)]
        [InlineData(32, AnalyticsGranularity.Week)]
        [InlineData(182, AnalyticsGranularity.Week)]
        [InlineData(183, AnalyticsGranularity.Month)]
        public void ResolveGranularity_UsesTheDocumentedThresholds(int days, AnalyticsGranularity expected)
        {
            var f = Valid();
            f.ToDate = f.FromDate!.Value.AddDays(days);
            Assert.Equal(expected, f.ResolveGranularity());
        }

        [Fact]
        public void ResolveGranularity_AllTimeIsMonthly()
        {
            var f = new AnalyticsFilter { TimeZone = "UTC" };
            Assert.Equal(AnalyticsGranularity.Month, f.ResolveGranularity());
        }

        [Fact]
        public void Normalize_DeduplicatesAndSortsSoEquivalentFiltersShareACacheKey()
        {
            var a = Valid();
            a.PrinterIds = new List<long> { 3, 1, 3 };
            var b = Valid();
            b.PrinterIds = new List<long> { 1, 3 };

            a.Normalize();
            b.Normalize();

            Assert.Equal(a.CacheKey(7), b.CacheKey(7));
        }

        [Fact]
        public void CacheKey_IsTenantScoped()
        {
            var f = Valid();
            f.Normalize();
            Assert.NotEqual(f.CacheKey(7), f.CacheKey(8));
        }
    }
}
