using System;
using System.Linq;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class TimeBucketerTests
    {
        private static TimeZoneInfo Chicago =>
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

        [Fact]
        public void BuildBuckets_DailyBucketsAreContiguousAndHalfOpen()
        {
            var from = new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);
            var to = from.AddDays(5);

            var buckets = TimeBucketer.BuildBuckets(from, to, Chicago, AnalyticsGranularity.Day, DayOfWeek.Sunday);

            Assert.Equal(5, buckets.Count);
            for (var i = 1; i < buckets.Count; i++)
                Assert.Equal(buckets[i - 1].EndUtc, buckets[i].StartUtc);
        }

        [Fact]
        public void BuildBuckets_TheSpringForwardLocalDayIsTwentyThreeHours()
        {
            // US DST 2026 begins Sunday 8 March.
            var from = new DateTimeOffset(2026, 3, 6, 6, 0, 0, TimeSpan.Zero);
            var to = new DateTimeOffset(2026, 3, 11, 6, 0, 0, TimeSpan.Zero);

            var buckets = TimeBucketer.BuildBuckets(from, to, Chicago, AnalyticsGranularity.Day, DayOfWeek.Sunday);
            var springForward = buckets.Single(b => b.LocalStart == new DateOnly(2026, 3, 8));

            Assert.Equal(23, (springForward.EndUtc - springForward.StartUtc).TotalHours);
            // And a fixed-offset implementation would report 24 — that is the bug this guards.
            Assert.All(buckets, b => Assert.InRange((b.EndUtc - b.StartUtc).TotalHours, 23, 25));
        }

        [Fact]
        public void IndexOf_AssignsAnInstantToItsLocalDayNotItsUtcDay()
        {
            var from = new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero);
            var to = from.AddDays(3);
            var buckets = TimeBucketer.BuildBuckets(from, to, Chicago, AnalyticsGranularity.Day, DayOfWeek.Sunday);

            // 2 July 02:00 UTC is still 1 July 21:00 in Chicago.
            var idx = TimeBucketer.IndexOf(buckets, new DateTimeOffset(2026, 7, 2, 2, 0, 0, TimeSpan.Zero));

            Assert.Equal(new DateOnly(2026, 7, 1), buckets[idx].LocalStart);
        }

        [Fact]
        public void IndexOf_ReturnsMinusOneOutsideTheWindow()
        {
            var from = new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero);
            var buckets = TimeBucketer.BuildBuckets(from, from.AddDays(2), Chicago, AnalyticsGranularity.Day, DayOfWeek.Sunday);

            Assert.Equal(-1, TimeBucketer.IndexOf(buckets, from.AddDays(-5)));
            Assert.Equal(-1, TimeBucketer.IndexOf(buckets, from.AddDays(50)));
        }

        [Fact]
        public void BuildBuckets_WeeklyBucketsStartOnTheConfiguredWeekStart()
        {
            var from = new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero);
            var buckets = TimeBucketer.BuildBuckets(from, from.AddDays(28), Chicago, AnalyticsGranularity.Week, DayOfWeek.Monday);

            Assert.All(buckets, b => Assert.Equal(DayOfWeek.Monday, b.LocalStart.DayOfWeek));
        }

        [Fact]
        public void BuildBuckets_MonthlyBucketsStartOnTheFirst()
        {
            var from = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
            var buckets = TimeBucketer.BuildBuckets(from, from.AddDays(120), Chicago, AnalyticsGranularity.Month, DayOfWeek.Sunday);

            Assert.All(buckets, b => Assert.Equal(1, b.LocalStart.Day));
        }

        [Fact]
        public void PreviousWindow_IsTheImmediatelyPrecedingRangeOfEqualLength()
        {
            var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var to = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

            var (pFrom, pTo) = TimeBucketer.PreviousWindow(from, to);

            Assert.Equal(from, pTo);
            Assert.Equal(to - from, pTo - pFrom);
        }

        /// <summary>
        /// The UI sends IANA ids. On .NET 6+ these resolve on Windows only through the ICU/CLDR
        /// mapping: with InvariantGlobalization=true they throw at runtime while every other test
        /// on this machine still passes. The fix is InvariantGlobalization=false in the csproj
        /// (and tzdata on a slim Linux container), never a translation table in application code.
        /// </summary>
        [Theory]
        [InlineData("America/New_York")]
        [InlineData("Europe/London")]
        [InlineData("Australia/Lord_Howe")] // 30-minute DST shift
        [InlineData("Asia/Kathmandu")]      // 45-minute standing offset
        [InlineData("UTC")]
        public void EveryIanaIdTheUiCanSend_ResolvesOnThisPlatform(string id)
        {
            Assert.NotNull(TimeZoneInfo.FindSystemTimeZoneById(id));
        }
    }
}
