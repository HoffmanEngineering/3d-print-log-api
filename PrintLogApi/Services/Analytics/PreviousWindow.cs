#nullable enable

using System;
using System.Linq;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>
    /// The window a "compare to previous period" delta is measured against: the immediately
    /// preceding range of identical length **in the user's local calendar** (spec §5). Written
    /// once because five services need the same rule, and a delta computed against a slightly
    /// different window is worse than no delta at all.
    ///
    /// This deliberately does NOT reuse TimeBucketer.PreviousWindow, which subtracts a UTC span
    /// and is therefore off by an hour whenever the range crosses a DST transition.
    /// </summary>
    public static class PreviousWindow
    {
        /// <summary>
        /// The filter re-pointed at the preceding window, or null when there is nothing to
        /// compare against — no explicit range, or comparison not requested. Callers treat null
        /// as "leave Previous alone", which is what suppresses the delta in the UI.
        /// </summary>
        public static AnalyticsFilter? For(AnalyticsFilter? filter)
        {
            if (filter is null || !filter.ComparePrevious || !filter.HasRange) return null;

            var (from, to) = Preceding(filter);

            var previous = new AnalyticsFilter
            {
                FromDate = from,
                ToDate = to,
                TimeZone = filter.TimeZone,
                PrinterIds = filter.PrinterIds,
                FilamentIds = filter.FilamentIds,
                ProjectIds = filter.ProjectIds,
                Statuses = filter.Statuses,
                Granularity = filter.Granularity,
                // Never true: the previous window does not get a previous window of its own,
                // and leaving it set would recurse once per request.
                ComparePrevious = false,
            };

            // AnalyticsFilter's documented invariant is that Normalize() runs before the filter
            // is used. Inheriting already-normalized collections is not the same as being
            // normalized, and a future field could break that assumption silently.
            previous.Normalize();
            return previous;
        }

        /// <summary>
        /// The preceding window, measured in the user's LOCAL calendar.
        ///
        /// Subtracting the UTC span is wrong across a DST transition: a 30-day local window that
        /// contains a spring-forward is 719 UTC hours, so a UTC-span subtraction lands the prior
        /// window an hour off its own local midnight and shifts every bucket boundary with it.
        /// Spec §5 says the comparison window is computed in the user's timezone, and §7 says
        /// boundaries come from civil local dates — this converts to local, subtracts whole local
        /// days, and converts back.
        /// </summary>
        private static (DateTimeOffset From, DateTimeOffset To) Preceding(AnalyticsFilter filter)
        {
            if (!filter.TryResolveTimeZone(out var zone) || zone is null)
            {
                var span = filter.ToDate.Value - filter.FromDate.Value;
                return (filter.FromDate.Value - span, filter.FromDate.Value);
            }

            var localFrom = TimeZoneInfo.ConvertTime(filter.FromDate.Value, zone);
            var localTo = TimeZoneInfo.ConvertTime(filter.ToDate.Value, zone);

            var days = (int)Math.Round((localTo.Date - localFrom.Date).TotalDays);
            if (days <= 0)
            {
                // Sub-day range: a local-day subtraction is meaningless, so fall back to the span.
                var span = filter.ToDate.Value - filter.FromDate.Value;
                return (filter.FromDate.Value - span, filter.FromDate.Value);
            }

            DateTimeOffset ToUtc(DateTime naiveLocal)
            {
                // Spring forward: this local time does not exist. The day starts an hour late.
                if (zone.IsInvalidTime(naiveLocal)) naiveLocal = naiveLocal.AddHours(1);

                // Fall back: this local time happens TWICE, and GetUtcOffset resolves the
                // ambiguity to standard time by default — which would silently shift a window
                // boundary by an hour. Take the FIRST occurrence (the daylight offset), so the
                // window opens at the earlier of the two identical clock readings and no print
                // logged in the repeated hour falls outside it.
                var offset = zone.IsAmbiguousTime(naiveLocal)
                    ? zone.GetAmbiguousTimeOffsets(naiveLocal).Max()
                    : zone.GetUtcOffset(naiveLocal);

                return new DateTimeOffset(naiveLocal, offset).ToUniversalTime();
            }

            // The end is the current window's start, passed through UNCHANGED. Reconstructing it
            // from the local date would break adjacency in exactly the case the ambiguity
            // handling above cares about: if the caller's FromDate was the SECOND occurrence of
            // a repeated fall-back hour, ToUtc would hand back the first, leaving a one-hour gap
            // between the two windows. "Immediately preceding" has to mean exactly that.
            return (
                ToUtc(localFrom.DateTime.AddDays(-days)),
                filter.FromDate.Value);
        }

        /// <summary>
        /// A prior value fit to divide by. Zero and null both suppress the delta rather than
        /// producing an infinite or meaningless percentage change (spec §5).
        /// </summary>
        public static double? Usable(double? previous) =>
            previous is null or 0 ? null : previous;

        public static decimal? Usable(decimal? previous) =>
            previous is null or 0m ? null : previous;
    }
}
