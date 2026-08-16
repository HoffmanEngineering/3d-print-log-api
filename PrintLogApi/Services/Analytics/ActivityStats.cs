using System;
using System.Collections.Generic;
using System.Linq;

namespace PrintLogApi.Services.Analytics
{
    public sealed record DayCount(DateOnly Date, int Count);

    public sealed record StreakSummary(
        int CurrentDays,
        int LongestDays,
        DateOnly? LongestStart,
        DateOnly? LongestEnd,
        DateOnly? BusiestDate,
        int BusiestDateCount,
        int? BusiestWeekday,
        int BusiestWeekdayCount);

    /// <summary>A half-open [LowerSeconds, UpperSeconds) duration bucket. Null upper means unbounded.</summary>
    public sealed record HistogramBucket(string Label, int LowerSeconds, int? UpperSeconds, int Count);

    /// <summary>Weekday is 0-6 with Sunday = 0, matching DayOfWeek. Hour is local, 0-23.</summary>
    public sealed record MatrixCell(int Weekday, int Hour, int Count);

    /// <summary>
    /// Stage-2 shaping for the Activity tab. Deliberately pure and EF-free: streaks, histogram
    /// bucketing and the weekday matrix do not translate portably across SQL Server and SQLite
    /// (spec §6.4), so they run over bounded stage-1 rows and are tested without a database.
    /// </summary>
    public static class ActivityStats
    {
        private static readonly (string Label, int Lower, int? Upper)[] DurationBuckets =
        {
            ("<30m",   0,      1800),
            ("30m–1h", 1800,   3600),
            ("1–2h",   3600,   7200),
            ("2–4h",   7200,   14400),
            ("4–8h",   14400,  28800),
            ("8–12h",  28800,  43200),
            ("12–24h", 43200,  86400),
            ("24h+",   86400,  null),
        };

        /// <param name="days">Days with at least one print. Order and duplicates do not matter.</param>
        /// <param name="today">The user's LOCAL today, so a streak does not break at UTC midnight.</param>
        public static StreakSummary Streaks(IReadOnlyList<DayCount> days, DateOnly today)
        {
            if (days is null || days.Count == 0)
                return new StreakSummary(0, 0, null, null, null, 0, null, 0);

            // Collapse duplicates defensively: the caller groups by date, but a second source of
            // rows must not be able to inflate a streak by repeating a date.
            var byDate = days
                .Where(d => d.Count > 0)
                .GroupBy(d => d.Date)
                .Select(g => new DayCount(g.Key, g.Sum(x => x.Count)))
                .OrderBy(d => d.Date)
                .ToList();

            if (byDate.Count == 0) return new StreakSummary(0, 0, null, null, null, 0, null, 0);

            var longest = 0;
            DateOnly? longestStart = null, longestEnd = null;
            var runStart = byDate[0].Date;

            for (var i = 0; i < byDate.Count; i++)
            {
                var isLast = i == byDate.Count - 1;
                var breaksHere = isLast || byDate[i + 1].Date != byDate[i].Date.AddDays(1);
                if (!breaksHere) continue;

                var length = byDate[i].Date.DayNumber - runStart.DayNumber + 1;
                // Strictly greater, so the EARLIEST run wins a tie and the answer is stable.
                if (length > longest)
                {
                    longest = length;
                    longestStart = runStart;
                    longestEnd = byDate[i].Date;
                }

                if (!isLast) runStart = byDate[i + 1].Date;
            }

            var last = byDate[^1].Date;
            var current = 0;
            // Today counts, and so does yesterday — today may simply not have happened yet.
            // Anything older is a finished run, not a streak the user is currently on.
            if (last == today || last == today.AddDays(-1))
            {
                current = 1;
                for (var i = byDate.Count - 1; i > 0; i--)
                {
                    if (byDate[i - 1].Date != byDate[i].Date.AddDays(-1)) break;
                    current++;
                }
            }

            var busiest = byDate
                .OrderByDescending(d => d.Count).ThenBy(d => d.Date)
                .First();

            var busiestWeekday = byDate
                .GroupBy(d => (int)d.Date.DayOfWeek)
                .Select(g => new { Weekday = g.Key, Count = g.Sum(x => x.Count) })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Weekday)
                .First();

            return new StreakSummary(
                current, longest, longestStart, longestEnd,
                busiest.Date, busiest.Count,
                busiestWeekday.Weekday, busiestWeekday.Count);
        }

        /// <param name="samples">
        /// (duration, how many prints had it) pairs. Weighted rather than one entry per print:
        /// the caller already knows the multiplicity, and expanding it into a flat list allocated
        /// an int per print to feed eight counters.
        ///
        /// Durations must be strictly positive. Zero or negative means NOT RECORDED and the
        /// caller must have excluded it as DurationMissing — a "0 seconds" print in the &lt;30m
        /// bucket would misreport data the user never entered.
        /// </param>
        public static IReadOnlyList<HistogramBucket> DurationHistogram(
            IEnumerable<(int Seconds, int Count)> samples)
        {
            var counts = new int[DurationBuckets.Length];

            foreach (var (seconds, count) in samples ?? Enumerable.Empty<(int, int)>())
            {
                if (seconds <= 0 || count <= 0) continue;
                for (var i = 0; i < DurationBuckets.Length; i++)
                {
                    var (_, lower, upper) = DurationBuckets[i];
                    if (seconds >= lower && (upper is null || seconds < upper.Value))
                    {
                        counts[i] += count;
                        break;
                    }
                }
            }

            return DurationBuckets
                .Select((b, i) => new HistogramBucket(b.Label, b.Lower, b.Upper, counts[i]))
                .ToList();
        }

        /// <summary>
        /// All 7 × 24 cells are always returned. A missing cell and a zero cell look identical to
        /// a heatmap, but only one of them is honest, and a sparse grid also reflows on every
        /// filter change.
        /// </summary>
        public static IReadOnlyList<MatrixCell> StartTimeMatrix(
            IEnumerable<(int Weekday, int Hour, int Count)> observations)
        {
            var grid = new int[7, 24];

            foreach (var (weekday, hour, count) in observations ?? Enumerable.Empty<(int, int, int)>())
            {
                if (weekday is < 0 or > 6 || hour is < 0 or > 23) continue;
                grid[weekday, hour] += count;
            }

            var cells = new List<MatrixCell>(168);
            for (var weekday = 0; weekday < 7; weekday++)
                for (var hour = 0; hour < 24; hour++)
                    cells.Add(new MatrixCell(weekday, hour, grid[weekday, hour]));

            return cells;
        }
    }
}
