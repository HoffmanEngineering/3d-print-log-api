using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics;

/// <summary>
/// The Accuracy tab. Time and material are computed as SEPARATE populations: a print can have
/// a measured duration and an estimated-only material amount, and requiring both would
/// silently shrink each sample.
/// </summary>
public sealed class AccuracyAnalyticsService(PrintLogContext context, TimeProvider timeProvider)
    : IAccuracyAnalyticsService
{
    public const int ScatterBins = 24;

    /// <summary>Below a 10% deviation, a callout is noise dressed up as advice.</summary>
    public const double MinCalloutDeviation = 0.1;

    private sealed record Row(
        long PrintId, long PrinterId, bool PrinterOwned, string? PrinterName,
        DateTimeOffset? StartDate,
        int EstimatedSeconds, int ActualSeconds,
        long EstimatedMg, long ActualMg);

    public async Task<AccuracyResponse> GetAccuracy(long userId, AnalyticsFilter filter, CancellationToken ct)
    {
        // ONE clock read per request, taken here at the entry point and threaded down. A
        // request can compute two windows (current and previous), and each of those closes an
        // open end at "now" — reading the clock per computation would let a single response
        // measure its two halves against two different instants.
        var now = timeProvider.GetUtcNow();

        var current = await Compute(userId, filter, now, ct);

        var previousFilter = PreviousWindow.For(filter, now);
        if (previousFilter is null) return current;

        var previous = await Compute(userId, previousFilter, now, ct);

        // Only the SCALAR medians carry a delta. The scatter, the trend and the per-group
        // rows are not tiles, and a per-bucket delta is a different chart.
        return current with
        {
            TimeAccuracyMedian = current.TimeAccuracyMedian with
            {
                Previous = PreviousWindow.Usable(previous.TimeAccuracyMedian.Value),
            },
            MaterialAccuracyMedian = current.MaterialAccuracyMedian with
            {
                Previous = PreviousWindow.Usable(previous.MaterialAccuracyMedian.Value),
            },
        };
    }

    /// <param name="now">
    /// The caller's single clock read. A filter with no ToDate means "up to now", so this is
    /// what closes the window — it is a parameter rather than a field read so that the two
    /// computations behind one response cannot disagree about when "now" was.
    /// </param>
    private async Task<AccuracyResponse> Compute(
        long userId, AnalyticsFilter filter, DateTimeOffset now, CancellationToken ct)
    {
        filter.TryResolveTimeZone(out var zone);
        zone ??= TimeZoneInfo.Utc;
        var granularity = filter.ResolveGranularity();

        var scoped = AnalyticsQueryScope.Scope(
            context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

        var counts = await AnalyticsPrintCounts.Load(scoped, ct);
        var coverage = new CoverageBuilder("prints") { Total = counts.Total };
        coverage.UndatedCount = filter.HasRange ? 0 : counts.Undated;

        if (coverage.Total > AnalyticsService.MaxSeriesRows)
        {
            coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
            return Empty(filter, granularity, coverage.Build());
        }

        // Accuracy needs the actual and estimated columns SEPARATELY, so this is one of the
        // few places that must not use MaterialMgExpr — that expression resolves between
        // them, which is exactly the comparison being made here.
        var rows = (await scoped
            .Select(p => new
            {
                PrintId = p.Id,
                p.PrinterId,
                // Owner-scoped, exactly as BuildHighlights and AnalyticsCostProjection are:
                // this projection reads a printer's NAME, make and model, so an unowned
                // reference would surface another user's machine — and its id — as a group
                // label the caller can click through on.
                PrinterOwned = p.Printer.UserId == userId,
                PrinterName = p.Printer.UserId == userId
                    ? (p.Printer.Name ?? (p.Printer.Make + " " + p.Printer.Model))
                    : null,
                p.StartDate,
                EstimatedSeconds = p.EstimatedPrintTimeInSeconds ?? 0,
                ActualSeconds = p.PrintTimeInSeconds ?? 0,
                // The rows PLUS the "other filament" scalars. Those two columns are a
                // genuine actual/estimate pair on the print itself (PrintProfile keeps them
                // parallel for exactly this reason), so omitting them would silently shrink
                // the material sample and bias it toward spool-tracked prints.
                EstimatedMg = (long)p.FilamentUsage!.Sum(pf =>
                    pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value : 0)
                    + (p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0
                        ? p.EstimatedFilamentUsageMg.Value : 0),
                ActualMg = (long)p.FilamentUsage!.Sum(pf =>
                    pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value : 0)
                    + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0
                        ? p.FilamentUsageMg.Value : 0),
            })
            .ToListAsync(ct))
            .Select(r => new Row(
                r.PrintId, r.PrinterId, r.PrinterOwned, r.PrinterName, r.StartDate,
                r.EstimatedSeconds, r.ActualSeconds, r.EstimatedMg, r.ActualMg))
            .ToList();

        var timeSamples = rows
            .Select(r => new AccuracySample(r.EstimatedSeconds, r.ActualSeconds))
            .ToList();
        var materialSamples = rows
            .Select(r => new AccuracySample(r.EstimatedMg, r.ActualMg))
            .ToList();

        var time = AccuracyStats.Analyze(timeSamples, suppressSmallSamples: false);
        var material = AccuracyStats.Analyze(materialSamples, suppressSmallSamples: false);

        // The response-level record describes the PRINTS this endpoint examined, and nothing
        // finer. Time and material are different populations with different sample sizes, so
        // summing their outliers here would produce a number that describes neither — the
        // dishonest-coverage shape spec §6.3 exists to prevent. Each metric carries its own
        // record below; this one only says "these prints were looked at".
        coverage.Counted = rows.Count;

        // Only prints on a printer this user owns can be grouped BY printer: the group carries
        // the machine's name and its id. The rest still count towards the headline medians,
        // the scatter and the trend, none of which name a printer.
        var ownedRows = rows.Where(r => r.PrinterOwned).ToList();

        var byPrinter = GroupAccuracy(
            ownedRows, "printer",
            r => r.PrinterId.ToString(CultureInfo.InvariantCulture),
            r => r.PrinterName,
            r => new AccuracySample(r.EstimatedSeconds, r.ActualSeconds));

        var (byMaterial, materialSuppressedPrintIds) =
            await GroupByMaterial(userId, scoped, coverage, ct);

        // SampleTooSmall is counted in PRINTS, because that is this record's declared
        // population. A count of suppressed GROUPS would be a number in the wrong unit — the
        // dishonest-coverage shape spec §6.3 exists to prevent, and the same rule
        // GroupByMaterial's row-versus-print comment spells out. The two sets are unioned
        // rather than added, so a print suppressed on both axes is not counted twice.
        var suppressedPrinterKeys = byPrinter
            .Where(g => g.SuppressedForSmallSample)
            .Select(g => g.Key)
            .ToHashSet();

        var suppressedPrintIds = ownedRows
            .Where(r => suppressedPrinterKeys.Contains(
                r.PrinterId.ToString(CultureInfo.InvariantCulture)))
            .Select(r => r.PrintId)
            .ToHashSet();
        suppressedPrintIds.UnionWith(materialSuppressedPrintIds);

        coverage.Exclude(ExclusionReason.SampleTooSmall, suppressedPrintIds.Count);

        return new AccuracyResponse(
            filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
            new Metric(time.MedianRatio, null,
                new CoverageBuilder("prints") { Counted = time.SampleSize, Total = rows.Count }
                    .Exclude(ExclusionReason.OutlierExcluded, time.OutliersExcluded).Build()),
            new Metric(material.MedianRatio, null,
                new CoverageBuilder("prints") { Counted = material.SampleSize, Total = rows.Count }
                    .Exclude(ExclusionReason.OutlierExcluded, material.OutliersExcluded).Build()),
            AccuracyStats.Bin(timeSamples, ScatterBins),
            byPrinter,
            byMaterial,
            BiasTrend(rows, filter, zone, granularity, now),
            Callouts(byPrinter, "time").Concat(Callouts(byMaterial, "material")).ToList(),
            coverage.Build());
    }

    private static IReadOnlyList<AccuracyGroup> GroupAccuracy(
        IReadOnlyList<Row> rows, string scope,
        Func<Row, string> key, Func<Row, string?> label, Func<Row, AccuracySample> sample) =>
        rows
            .GroupBy(key)
            .Select(g =>
            {
                // suppressSmallSamples: true — a per-group figure below n=5 is noise.
                var result = AccuracyStats.Analyze(g.Select(sample), suppressSmallSamples: true);
                return new AccuracyGroup(
                    scope, g.Key, label(g.First()),
                    result.MedianRatio, result.SampleSize, result.SuppressedForSmallSample);
            })
            .OrderByDescending(g => g.SampleSize).ThenBy(g => g.Key)
            .ToList();

    /// <summary>
    /// The by-material groups, plus the ids of the PRINTS in the suppressed ones. The caller's
    /// coverage record is denominated in prints, so it cannot be told a count of rows or of
    /// groups; the print ids let it union rather than add across the two axes.
    /// </summary>
    private async Task<(IReadOnlyList<AccuracyGroup> Groups, IReadOnlyCollection<long> SuppressedPrintIds)> GroupByMaterial(
        long userId, IQueryable<Print> scoped, CoverageBuilder coverage, CancellationToken ct)
    {
        // Two questions, two units, deliberately — this is not an inconsistency:
        //
        //   DECIDING whether to proceed asks "how much would this materialize?" The answer is
        //   in FILAMENT ROWS, because that is what the projection below returns: one row per
        //   spool per print, which the caller's print-grain check does not bound (spec §6.4).
        //
        //   REPORTING the exclusion asks "what did the reader lose?" The answer must be in
        //   the unit of the coverage record's population, which here is PRINTS. A row count
        //   inside a CoverageBuilder("prints") record would be a number in the wrong unit —
        //   the dishonest-coverage shape §6.3 exists to prevent.
        //
        // Reported rather than silent either way: an empty by-material list with no reason is
        // indistinguishable from "you have never used a tracked spool".
        var usageRowCount = await scoped.SelectMany(p => p.FilamentUsage!).CountAsync(ct);
        if (usageRowCount > AnalyticsService.MaxSeriesRows)
        {
            // coverage.Total is this same scoped count, already read by the caller. Counting
            // the set a third time to fill in a number we are holding is pure round-trip.
            coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
            return (Array.Empty<AccuracyGroup>(), Array.Empty<long>());
        }

        // Flattened with the result selector and filtered OUTSIDE the SelectMany: the
        // inner-filter form is a correlated subquery needing SQL APPLY, unsupported on SQLite.
        var rows = await scoped
            .SelectMany(p => p.FilamentUsage!, (p, pf) => new { p, pf })
            .Where(x => x.pf.Filament != null && x.pf.Filament.CreatedById == userId)
            .Select(x => new
            {
                PrintId = x.p.Id,
                Key = x.pf.Filament!.MaterialType ?? "Unknown",
                Estimated = (double)(x.pf.EstimatedAmountMg ?? 0),
                Actual = (double)(x.pf.AmountMg ?? 0),
            })
            .ToListAsync(ct);

        var grouped = rows.GroupBy(r => r.Key).ToList();

        var groups = grouped
            .Select(g =>
            {
                var result = AccuracyStats.Analyze(
                    g.Select(r => new AccuracySample(r.Estimated, r.Actual)), suppressSmallSamples: true);
                return new AccuracyGroup(
                    "material", g.Key, g.Key,
                    result.MedianRatio, result.SampleSize, result.SuppressedForSmallSample);
            })
            .OrderByDescending(g => g.SampleSize).ThenBy(g => g.Key)
            .ToList();

        var suppressedKeys = groups
            .Where(g => g.SuppressedForSmallSample)
            .Select(g => g.Key)
            .ToHashSet();

        var suppressedPrintIds = grouped
            .Where(g => suppressedKeys.Contains(g.Key))
            .SelectMany(g => g.Select(r => r.PrintId))
            .Distinct()
            .ToList();

        return (groups, suppressedPrintIds);
    }

    private static IReadOnlyList<AccuracyTrendBucket> BiasTrend(
        IReadOnlyList<Row> rows, AnalyticsFilter filter,
        TimeZoneInfo zone, AnalyticsGranularity granularity, DateTimeOffset now)
    {
        var dated = rows.Where(r => r.StartDate.HasValue).ToList();
        if (dated.Count == 0) return Array.Empty<AccuracyTrendBucket>();

        var from = filter.FromDate ?? dated.Min(r => r.StartDate!.Value);
        var to = filter.ToDate ?? now;
        if (to <= from) return Array.Empty<AccuracyTrendBucket>();

        var buckets = TimeBucketer.BuildBuckets(from, to, zone, granularity, DayOfWeek.Sunday);
        var samples = buckets.ToDictionary(b => b.Index, _ => new List<AccuracySample>());

        foreach (var row in dated)
        {
            var index = TimeBucketer.IndexOf(buckets, row.StartDate!.Value.ToUniversalTime());
            if (index < 0) continue;
            samples[buckets[index].Index].Add(new AccuracySample(row.EstimatedSeconds, row.ActualSeconds));
        }

        return buckets
            .Select(b =>
            {
                // Per-period suppression too: a bucket with two prints is not a trend point.
                var result = AccuracyStats.Analyze(samples[b.Index], suppressSmallSamples: true);
                return new AccuracyTrendBucket(b.Index, b.LocalStart, result.MedianRatio, result.SampleSize);
            })
            .ToList();
    }

    private static IEnumerable<AccuracyCallout> Callouts(
        IReadOnlyList<AccuracyGroup> groups, string dimension) =>
        groups
            .Where(g => g.MedianRatio.HasValue
                && g.SampleSize >= AccuracyStats.MinSampleSize
                && Math.Abs(g.MedianRatio.Value - 1.0) >= MinCalloutDeviation)
            .OrderByDescending(g => Math.Abs(g.MedianRatio!.Value - 1.0))
            .Take(3)
            .Select(g => new AccuracyCallout(
                g.Scope, g.Key, g.Label, dimension, g.MedianRatio!.Value, g.SampleSize));

    private static AccuracyResponse Empty(
        AnalyticsFilter filter, AnalyticsGranularity granularity, Coverage coverage) =>
        new(filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
            new Metric(null, null, coverage), new Metric(null, null, coverage),
            Array.Empty<ScatterBin>(), Array.Empty<AccuracyGroup>(), Array.Empty<AccuracyGroup>(),
            Array.Empty<AccuracyTrendBucket>(), Array.Empty<AccuracyCallout>(), coverage);
}
