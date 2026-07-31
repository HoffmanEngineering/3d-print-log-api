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

        public void Normalize()
        {
            PrinterIds = PrinterIds?.Distinct().OrderBy(x => x).ToList() ?? new List<long>();
            FilamentIds = FilamentIds?.Distinct().OrderBy(x => x).ToList() ?? new List<Guid>();
            ProjectIds = ProjectIds?.Distinct().OrderBy(x => x).ToList() ?? new List<Guid>();
            Statuses = Statuses?.Distinct().OrderBy(x => (int)x).ToList() ?? new List<PrintEntity.PrintStatus>();
            if (string.IsNullOrWhiteSpace(TimeZone)) TimeZone = "UTC";
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
