using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>
    /// The Activity tab. One stage-1 query grouped by { StartDate, DurationSeconds } feeds the
    /// series, the calendar, the streaks, the duration histogram and the weekday×hour matrix —
    /// five widgets that must agree, so they are derived from one set of rows rather than five
    /// queries that can drift.
    /// </summary>
    public sealed class ActivityAnalyticsService : IActivityAnalyticsService
    {
        /// <summary>53 weeks. Beyond this a calendar heatmap is unreadable, not merely large.</summary>
        public const int MaxCalendarDays = 371;

        private readonly PrintLogContext _context;

        public ActivityAnalyticsService(PrintLogContext context) => _context = context;

        private sealed record ActivityRow(DateTimeOffset StartDate, int DurationSeconds, int Count, long MaterialMg);

        public async Task<ActivityResponse> GetActivity(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var scoped = AnalyticsQueryScope.Scope(
                _context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

            var coverage = new CoverageBuilder("prints");
            var dated = scoped.Where(p => p.StartDate != null);

            coverage.Total = await scoped.CountAsync(ct);
            coverage.UndatedCount = filter.HasRange ? 0 : await scoped.CountAsync(p => p.StartDate == null, ct);

            var windowFrom = filter.FromDate ?? await dated.MinAsync(p => p.StartDate, ct) ?? DateTimeOffset.UtcNow;
            var windowTo = filter.ToDate ?? DateTimeOffset.UtcNow;

            if (windowTo <= windowFrom || await dated.CountAsync(ct) > AnalyticsService.MaxSeriesRows)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
                return Empty(filter, granularity, null, coverage.Build());
            }

            // Grouping by { instant, duration } rather than by instant alone: the histogram needs
            // each print's own duration, and two prints starting at the same second with different
            // durations must not be merged. Still bounded by the dated print count.
            var rows = (await dated
                .GroupBy(p => new
                {
                    p.StartDate,
                    DurationSeconds = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
                        ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                            ? p.EstimatedPrintTimeInSeconds.Value
                            : 0,
                })
                .Select(g => new
                {
                    g.Key.StartDate,
                    g.Key.DurationSeconds,
                    Count = g.Count(),
                    // Inlined copy of PrintMetrics.MaterialMgExpr: g.Sum takes a Func, not an
                    // Expression, so a group projection cannot consume the shared expression.
                    //
                    // The ownership guard is `no linked spool OR an owned one`, NOT
                    // `linked AND owned`: a usage row with FilamentId == null is legitimate
                    // untracked material and counts toward the canonical rowSum + other rule.
                    // Requiring a linked spool would silently drop it and make these totals
                    // disagree with /overview.
                    MaterialMg = g.Sum(p =>
                        (long)p.FilamentUsage
                            .Where(pf => pf.Filament == null || pf.Filament.CreatedById == userId)
                            .Sum(pf =>
                            pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                            : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                            : 0)
                        + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                           : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                           : 0)),
                })
                .ToListAsync(ct))
                .Select(r => new ActivityRow(r.StartDate!.Value, r.DurationSeconds, r.Count, r.MaterialMg))
                .ToList();

            coverage.Counted = rows.Sum(r => r.Count);

            var buckets = TimeBucketer.BuildBuckets(windowFrom, windowTo, zone, granularity, DayOfWeek.Sunday);
            var costByBucket = await CostByBucket(userId, scoped, buckets, coverage, ct);

            var counts = new int[buckets.Count];
            var durations = new long[buckets.Count];
            var materials = new long[buckets.Count];

            var calendarCounts = new Dictionary<DateOnly, int>();
            var matrixObservations = new List<(int Weekday, int Hour, int Count)>();
            var durationSamples = new List<int>();
            var durationMissing = 0;

            foreach (var row in rows)
            {
                var instant = row.StartDate.ToUniversalTime();
                var local = TimeZoneInfo.ConvertTime(instant, zone);
                var localDate = DateOnly.FromDateTime(local.DateTime);

                var index = TimeBucketer.IndexOf(buckets, instant);
                if (index >= 0)
                {
                    counts[index] += row.Count;
                    durations[index] += (long)row.DurationSeconds * row.Count;
                    materials[index] += row.MaterialMg;
                }

                calendarCounts[localDate] = calendarCounts.TryGetValue(localDate, out var n)
                    ? n + row.Count : row.Count;

                matrixObservations.Add(((int)local.DayOfWeek, local.Hour, row.Count));

                if (row.DurationSeconds > 0)
                    durationSamples.AddRange(Enumerable.Repeat(row.DurationSeconds, row.Count));
                else
                    durationMissing += row.Count;
            }

            coverage.Exclude(ExclusionReason.DurationMissing, durationMissing);

            var series = buckets
                .Select(b => new ActivitySeriesBucket(
                    b.Index, b.LocalStart, counts[b.Index], durations[b.Index], materials[b.Index],
                    costByBucket is null ? null : costByBucket[b.Index]))
                .ToList();

            var (calendar, calendarFrom, calendarTo, truncated) =
                BuildCalendar(calendarCounts, windowFrom, windowTo, zone);
            if (truncated) coverage.Exclude(ExclusionReason.WindowTruncated, 1);

            var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);

            // Future-dated days are excluded from the streak input. An all-time query has no
            // upper bound, so a print mis-dated into next month becomes the most recent day in
            // the set — and because that day is neither today nor yesterday, Streaks reports a
            // CURRENT streak of 0 and silently wipes out a run the user is genuinely on. A
            // future date is also not a day anyone has printed on yet, so it cannot extend the
            // longest run either.
            var streaks = ActivityStats.Streaks(
                calendarCounts
                    .Where(kv => kv.Key <= localToday)
                    .Select(kv => new DayCount(kv.Key, kv.Value))
                    .ToList(),
                localToday);

            return new ActivityResponse(
                filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
                await UserCurrency(userId, ct),
                series, calendar, calendarFrom, calendarTo, streaks,
                ActivityStats.DurationHistogram(durationSamples),
                ActivityStats.StartTimeMatrix(matrixObservations),
                coverage.Build());
        }

        /// <summary>
        /// Per-bucket cost, or null for the whole series when the cost row cap was exceeded. The
        /// other three metrics on the toggle stay usable either way — one expensive metric must
        /// not take the tab down with it.
        /// </summary>
        private async Task<decimal?[]> CostByBucket(
            long userId, IQueryable<Print> scoped, IReadOnlyList<TimeBucket> buckets,
            CoverageBuilder coverage, CancellationToken ct)
        {
            var projection = await AnalyticsCostProjection.Project(_context, userId, scoped, ct);
            if (projection.RowCapExceeded)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
                return null;
            }

            foreach (var (reason, count) in AnalyticsCostProjection.CountExclusions(projection.Prints))
                coverage.Exclude(reason, count);

            var totals = new decimal?[buckets.Count];
            foreach (var print in projection.Prints)
            {
                if (print.StartDate is null || print.Total is null) continue;
                var index = TimeBucketer.IndexOf(buckets, print.StartDate.Value.ToUniversalTime());
                if (index < 0) continue;
                totals[index] = (totals[index] ?? 0m) + print.Total.Value;
            }
            return totals;
        }

        /// <summary>
        /// Every local day in the window gets a cell, including empty ones — a calendar with gaps
        /// is not a calendar. Capped at 53 weeks, keeping the TRAILING window, because the recent
        /// end is the part a user reads.
        /// </summary>
        private static (IReadOnlyList<CalendarDay> Days, DateOnly? From, DateOnly? To, bool Truncated) BuildCalendar(
            IReadOnlyDictionary<DateOnly, int> counts,
            DateTimeOffset windowFrom, DateTimeOffset windowTo, TimeZoneInfo zone)
        {
            var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(windowFrom, zone).DateTime);
            var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(windowTo.AddTicks(-1), zone).DateTime);
            if (last < first) return (Array.Empty<CalendarDay>(), null, null, false);

            var truncated = false;
            var span = last.DayNumber - first.DayNumber + 1;
            if (span > MaxCalendarDays)
            {
                first = last.AddDays(-(MaxCalendarDays - 1));
                truncated = true;
                span = MaxCalendarDays;
            }

            var days = new List<CalendarDay>(span);
            for (var i = 0; i < span; i++)
            {
                var date = first.AddDays(i);
                days.Add(new CalendarDay(date, counts.TryGetValue(date, out var n) ? n : 0));
            }

            return (days, first, last, truncated);
        }

        private async Task<string> UserCurrency(long userId, CancellationToken ct) =>
            await _context.UserSettings.AsNoTracking()
                .Where(s => s.UserId == userId && s.UserSettingTypeId == 5)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);

        private static ActivityResponse Empty(
            AnalyticsFilter filter, AnalyticsGranularity granularity, string currency, Coverage coverage) =>
            new(filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(), currency,
                Array.Empty<ActivitySeriesBucket>(), Array.Empty<CalendarDay>(), null, null,
                ActivityStats.Streaks(Array.Empty<DayCount>(), DateOnly.FromDateTime(DateTime.UtcNow)),
                ActivityStats.DurationHistogram(Array.Empty<int>()),
                ActivityStats.StartTimeMatrix(Array.Empty<(int, int, int)>()),
                coverage);
    }
}
