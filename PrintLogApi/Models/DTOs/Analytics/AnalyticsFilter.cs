using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// Inside this namespace the bare name `Print` binds to the sibling DTO namespace
// PrintLogApi.Models.DTOs.Print, not to the entity. Alias it so PrintStatus resolves.
using PrintEntity = PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Analytics
{
    public enum AnalyticsGranularity { Auto = 0, Day = 1, Week = 2, Month = 3 }

    /// <summary>
    /// The shared query contract for every analytics endpoint. Ranges are half-open [From, To).
    /// Limits exist to bound SQL IN lists, URL length, and cache-key cardinality.
    /// </summary>
    public class AnalyticsFilter
    {
        public const int MaxPrinterIds = 50;
        public const int MaxFilamentIds = 100;
        public const int MaxProjectIds = 50;
        public const int MaxRangeYears = 20;

        public DateTimeOffset? FromDate { get; set; }
        public DateTimeOffset? ToDate { get; set; }
        public string TimeZone { get; set; } = "UTC";
        public List<long> PrinterIds { get; set; } = new();
        public List<Guid> FilamentIds { get; set; } = new();
        public List<Guid> ProjectIds { get; set; } = new();
        public List<PrintEntity.PrintStatus> Statuses { get; set; } = new();
        public AnalyticsGranularity Granularity { get; set; } = AnalyticsGranularity.Auto;
        public bool ComparePrevious { get; set; }

        public bool HasRange => FromDate.HasValue && ToDate.HasValue;

        /// <summary>
        /// The clamp ceiling, rounded UP to the next whole UTC hour.
        ///
        /// Rounding is what makes the clamp cacheable. Clamping to DateTimeOffset.UtcNow would
        /// stamp a different tick into every request, and since CacheKey serializes ToDate with
        /// "O", two otherwise-identical requests a second apart would mint two cache entries —
        /// turning a fix for unbounded ranges into an unbounded cache. An hour's ceiling also
        /// keeps "up to now" genuinely inclusive of the current hour's prints.
        /// </summary>
        public static DateTimeOffset ClampCeiling(DateTimeOffset now)
        {
            // Convert to UTC BEFORE reading the calendar fields. now.Year/Month/Day/Hour are the
            // value's own offset's wall clock, so pairing them with TimeSpan.Zero would silently
            // reinterpret 14:37-05:00 (19:37Z) as 14:37Z and round to the wrong hour. Harmless
            // while the only caller passes UtcNow, but this is public and documents a UTC
            // contract, so it has to honour one.
            var utc = now.ToUniversalTime();
            return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero)
                .AddHours(1);
        }

        public void Normalize() => Normalize(DateTimeOffset.UtcNow);

        /// <summary>
        /// Clock-injectable. Every clamp assertion needs a fixed `now`, or it becomes flaky at
        /// whatever boundary the clamp rounds to and calendar-dependent besides.
        ///
        /// PUBLIC, not internal: the tests live in `PrintLogApi.IntegrationTests`, a separate
        /// assembly, and the solution has no `InternalsVisibleTo` anywhere. An internal overload
        /// would simply not compile from the test project, and adding a friend-assembly
        /// declaration to widen one method's reach is the larger change.
        /// </summary>
        public void Normalize(DateTimeOffset now)
        {
            PrinterIds = PrinterIds?.Distinct().OrderBy(x => x).ToList() ?? new List<long>();
            FilamentIds = FilamentIds?.Distinct().OrderBy(x => x).ToList() ?? new List<Guid>();
            ProjectIds = ProjectIds?.Distinct().OrderBy(x => x).ToList() ?? new List<Guid>();
            Statuses = Statuses?.Distinct().OrderBy(x => (int)x).ToList() ?? new List<PrintEntity.PrintStatus>();
            if (string.IsNullOrWhiteSpace(TimeZone)) TimeZone = "UTC";

            // Clamp anything beyond NOW — not merely beyond the rounded ceiling, or a timestamp
            // up to an hour ahead would slip through unclamped. The stored value is the rounded
            // ceiling, which is what keeps CacheKey stable: clamping to the raw instant would
            // stamp a distinct tick into every request and mint a cache entry per call.
            if (ToDate.HasValue && ToDate.Value > now) ToDate = ClampCeiling(now);

            // FromDate is deliberately NOT moved. A range lying entirely in the future stays
            // inverted and Validate() rejects it, which is the honest answer to "show me next
            // March". Rewriting FromDate to equal ToDate would produce exactly the
            // `FromDate >= ToDate` state Validate already rejects, just less legibly.
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();

            if (FromDate.HasValue != ToDate.HasValue)
                errors.Add("fromDate and toDate must be supplied together, or both omitted for all-time.");

            if (HasRange)
            {
                if (FromDate >= ToDate)
                    errors.Add("fromDate must be earlier than toDate.");
                else if ((ToDate.Value - FromDate.Value).TotalDays > MaxRangeYears * 366)
                    errors.Add($"The requested range exceeds the maximum of {MaxRangeYears} years.");
            }

            if (PrinterIds?.Count > MaxPrinterIds) errors.Add($"printerIds exceeds the maximum of {MaxPrinterIds}.");
            if (FilamentIds?.Count > MaxFilamentIds) errors.Add($"filamentIds exceeds the maximum of {MaxFilamentIds}.");
            if (ProjectIds?.Count > MaxProjectIds) errors.Add($"projectIds exceeds the maximum of {MaxProjectIds}.");

            if (Statuses != null && Statuses.Any(s => !Enum.IsDefined(typeof(PrintEntity.PrintStatus), s)))
                errors.Add("statuses contains an unrecognized value.");

            if (!TryResolveTimeZone(out _))
                errors.Add($"timeZone '{TimeZone}' is not a recognized time zone identifier.");

            return errors;
        }

        /// <summary>
        /// Resolves an IANA id, falling back to the Windows id on hosts without ICU IANA support.
        /// </summary>
        public bool TryResolveTimeZone(out TimeZoneInfo zone)
        {
            zone = null;
            if (string.IsNullOrWhiteSpace(TimeZone)) return false;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); return true; }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { return false; }

            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZone, out var windowsId))
            {
                try { zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId); return true; }
                catch (TimeZoneNotFoundException) { }
            }
            return false;
        }

        public AnalyticsGranularity ResolveGranularity()
        {
            if (Granularity != AnalyticsGranularity.Auto) return Granularity;
            if (!HasRange) return AnalyticsGranularity.Month;

            var days = (ToDate.Value - FromDate.Value).TotalDays;
            if (days <= 31) return AnalyticsGranularity.Day;
            if (days <= 182) return AnalyticsGranularity.Week;
            return AnalyticsGranularity.Month;
        }

        /// <summary>
        /// Call Normalize() first. The tenant id is part of the key: a key derived only from
        /// user-supplied filter values would allow cross-tenant cache reads.
        /// </summary>
        public string CacheKey(long userId)
        {
            var sb = new StringBuilder("analytics:v1:").Append(userId).Append(':')
                .Append(FromDate?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "-").Append(':')
                .Append(ToDate?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? "-").Append(':')
                .Append(TimeZone).Append(':')
                .Append(ResolveGranularity()).Append(':')
                .Append(ComparePrevious ? '1' : '0').Append(':')
                .Append(string.Join(",", PrinterIds)).Append(':')
                .Append(string.Join(",", FilamentIds)).Append(':')
                .Append(string.Join(",", ProjectIds)).Append(':')
                .Append(string.Join(",", Statuses.Select(s => (int)s)));

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return $"analytics:v1:{userId}:{Convert.ToHexString(hash)}";
        }
    }
}
