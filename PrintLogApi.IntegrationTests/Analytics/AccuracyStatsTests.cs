using System;
using System.Linq;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class AccuracyStatsTests
    {
        private static AccuracySample S(double estimated, double actual) => new(estimated, actual);

        [Fact]
        public void Analyze_ReportsTheMedianRatioNotTheMean()
        {
            // Mean would be dragged to ~2.2 by the last sample; the median is the honest centre.
            var samples = new[] { S(100, 100), S(100, 110), S(100, 120), S(100, 130), S(100, 900) };

            var result = AccuracyStats.Analyze(samples, suppressSmallSamples: false);

            Assert.Equal(1.2, result.MedianRatio!.Value, 3);
        }

        [Fact]
        public void Analyze_ExcludesRatiosOutsideTheSanityBandAndCountsThem()
        {
            var samples = new[] { S(100, 100), S(100, 110), S(100, 5), S(100, 2000) };

            var result = AccuracyStats.Analyze(samples, suppressSmallSamples: false);

            Assert.Equal(2, result.OutliersExcluded);
            Assert.Equal(2, result.SampleSize);
        }

        [Fact]
        public void Analyze_IgnoresSamplesWhereEitherSideWasNotRecorded()
        {
            var samples = new[] { S(0, 100), S(100, 0), S(-1, 5), S(100, 100) };

            var result = AccuracyStats.Analyze(samples, suppressSmallSamples: false);

            Assert.Equal(1, result.SampleSize);
            Assert.Equal(0, result.OutliersExcluded);
        }

        [Fact]
        public void Analyze_SuppressesASmallGroupWhenAskedButNotTheHeadline()
        {
            var samples = new[] { S(100, 100), S(100, 120) };

            var suppressed = AccuracyStats.Analyze(samples, suppressSmallSamples: true);
            var headline = AccuracyStats.Analyze(samples, suppressSmallSamples: false);

            Assert.Null(suppressed.MedianRatio);
            Assert.True(suppressed.SuppressedForSmallSample);
            Assert.NotNull(headline.MedianRatio);
        }

        [Fact]
        public void Analyze_EmptyInputIsNullAndNeverThrows()
        {
            var result = AccuracyStats.Analyze(Array.Empty<AccuracySample>(), suppressSmallSamples: false);

            Assert.Null(result.MedianRatio);
            Assert.Equal(0, result.SampleSize);
        }

        [Fact]
        public void Analyze_MedianOfAnEvenSampleAveragesTheMiddleTwo()
        {
            var samples = new[] { S(100, 100), S(100, 200) };

            var result = AccuracyStats.Analyze(samples, suppressSmallSamples: false);

            Assert.Equal(1.5, result.MedianRatio!.Value, 3);
        }

        [Fact]
        public void Bin_CollapsesPointsOntoABoundedGrid()
        {
            var samples = Enumerable.Range(1, 1000).Select(i => S(i * 60, i * 66)).ToList();

            var bins = AccuracyStats.Bin(samples, 20);

            Assert.True(bins.Count <= 20 * 20, $"{bins.Count} bins");
            Assert.Equal(1000, bins.Sum(b => b.Count));
        }

        [Fact]
        public void Bin_UsesLogSpaceSoShortAndLongPrintsBothGetResolution()
        {
            // Seconds to days. On a linear grid these would all land in the first column.
            var samples = new[] { S(60, 66), S(600, 660), S(6000, 6600), S(60000, 66000) };

            var bins = AccuracyStats.Bin(samples, 20);

            Assert.Equal(4, bins.Count);
        }

        [Fact]
        public void Bin_DropsUnusableSamplesRatherThanThrowing()
        {
            var bins = AccuracyStats.Bin(new[] { S(0, 5), S(5, 0), S(-1, -1) }, 10);

            Assert.Empty(bins);
        }
    }
}
