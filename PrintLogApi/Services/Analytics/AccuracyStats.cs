#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>One estimated/actual pair. Both must be &gt; 0 to be usable.</summary>
    public sealed record AccuracySample(double Estimated, double Actual)
    {
        public double Ratio => Actual / Estimated;
    }

    public sealed record AccuracyResult(
        double? MedianRatio, int SampleSize, int OutliersExcluded, bool SuppressedForSmallSample);

    /// <summary>A binned scatter point: the bin's centre plus how many samples landed in it.</summary>
    public sealed record ScatterBin(double Estimated, double Actual, int Count);

    /// <summary>
    /// Estimate-accuracy shaping. Pure and EF-free: a median does not translate portably across
    /// SQL Server and SQLite (spec §6.4), so it runs over bounded stage-1 rows.
    /// </summary>
    public static class AccuracyStats
    {
        /// <summary>Below this a per-group figure is noise, not a signal.</summary>
        public const int MinSampleSize = 5;

        /// <summary>Outside this band a ratio is a data-entry error, not a bad estimate.</summary>
        public const double MinRatio = 0.1;
        public const double MaxRatio = 10.0;

        public static AccuracyResult Analyze(IEnumerable<AccuracySample> samples, bool suppressSmallSamples)
        {
            var usable = (samples ?? Enumerable.Empty<AccuracySample>())
                .Where(s => s.Estimated > 0 && s.Actual > 0)
                .ToList();

            var ratios = usable.Select(s => s.Ratio).ToList();
            var kept = ratios.Where(r => r >= MinRatio && r <= MaxRatio).OrderBy(r => r).ToList();
            var outliers = ratios.Count - kept.Count;

            if (kept.Count == 0)
                return new AccuracyResult(null, 0, outliers, false);

            if (suppressSmallSamples && kept.Count < MinSampleSize)
                return new AccuracyResult(null, kept.Count, outliers, true);

            // Median, not mean: one 9x outlier inside the sanity band would still drag a mean
            // far enough to make the headline useless.
            var middle = kept.Count / 2;
            var median = kept.Count % 2 == 1
                ? kept[middle]
                : (kept[middle - 1] + kept[middle]) / 2.0;

            return new AccuracyResult(median, kept.Count, outliers, false);
        }

        /// <summary>
        /// Bins onto a bins × bins grid in LOG space. Print durations span seconds to days; a
        /// linear grid would put almost every point in the first cell and the chart would show
        /// nothing. Raw points are never shipped unbounded (spec §6.4).
        /// </summary>
        public static IReadOnlyList<ScatterBin> Bin(IEnumerable<AccuracySample> samples, int bins)
        {
            var usable = (samples ?? Enumerable.Empty<AccuracySample>())
                .Where(s => s.Estimated > 0 && s.Actual > 0)
                .ToList();

            if (usable.Count == 0 || bins <= 0) return Array.Empty<ScatterBin>();

            var logEstimates = usable.Select(s => Math.Log10(s.Estimated)).ToList();
            var logActuals = usable.Select(s => Math.Log10(s.Actual)).ToList();

            var minX = logEstimates.Min();
            var maxX = logEstimates.Max();
            var minY = logActuals.Min();
            var maxY = logActuals.Max();

            // A degenerate axis (every value identical) would divide by zero; one bin is correct.
            var spanX = maxX - minX;
            var spanY = maxY - minY;

            int Index(double value, double min, double span) =>
                span <= 0 ? 0 : Math.Min(bins - 1, (int)((value - min) / span * bins));

            return usable
                .Select((sample, i) => new
                {
                    X = Index(logEstimates[i], minX, spanX),
                    Y = Index(logActuals[i], minY, spanY),
                    sample,
                })
                .GroupBy(p => new { p.X, p.Y })
                .Select(g => new ScatterBin(
                    g.Average(p => p.sample.Estimated),
                    g.Average(p => p.sample.Actual),
                    g.Count()))
                .OrderBy(b => b.Estimated).ThenBy(b => b.Actual)
                .ToList();
        }
    }
}
