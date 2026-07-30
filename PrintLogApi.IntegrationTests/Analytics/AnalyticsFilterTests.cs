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
