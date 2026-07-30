using System.Collections.Generic;
using System.Linq;

namespace PrintLogApi.Models.DTOs.Analytics
{
    /// <summary>
    /// Why a print, spool, or group was left out of a metric. The UI renders honesty badges
    /// from these rather than inferring, so every reason must be actionable to a reader.
    /// </summary>
    public static class ExclusionReason
    {
        public const string Undated = "Undated";
        public const string DurationEstimated = "DurationEstimated";
        public const string MaterialEstimated = "MaterialEstimated";
        public const string PriceMissing = "PriceMissing";
        public const string CurrencyMismatch = "CurrencyMismatch";
        public const string WattageMissing = "WattageMissing";
        public const string RateMissing = "RateMissing";
        public const string OutlierExcluded = "OutlierExcluded";
        public const string SampleTooSmall = "SampleTooSmall";
        public const string RowCapExceeded = "RowCapExceeded";
    }

    public sealed record CoverageExclusion(string Reason, int Count);

    /// <summary>
    /// Population is named explicitly ("prints", "spools", "printers") because a count of
    /// excluded spools is not a count of excluded prints.
    /// </summary>
    public sealed record Coverage(
        string Population,
        int Counted,
        int Total,
        int UndatedCount,
        IReadOnlyList<CoverageExclusion> Exclusions)
    {
        public static Coverage Empty(string population) =>
            new(population, 0, 0, 0, new List<CoverageExclusion>());
    }

    /// <summary>A scalar metric. Previous is null unless comparePrevious was requested.</summary>
    public sealed record Metric(double? Value, double? Previous, Coverage Coverage);

    /// <summary>Money is decimal, never double, and always carries its currency.</summary>
    public sealed record MoneyMetric(decimal? Value, decimal? Previous, string Currency, Coverage Coverage);

    public sealed class CoverageBuilder
    {
        private readonly Dictionary<string, int> _exclusions = new();

        public CoverageBuilder(string population) => Population = population;

        public string Population { get; set; }
        public int Counted { get; set; }
        public int Total { get; set; }
        public int UndatedCount { get; set; }

        public CoverageBuilder Exclude(string reason, int count = 1)
        {
            if (count <= 0) return this;
            _exclusions[reason] = _exclusions.TryGetValue(reason, out var existing) ? existing + count : count;
            return this;
        }

        public Coverage Build() => new(
            Population,
            Counted,
            Total,
            UndatedCount,
            _exclusions.Select(kv => new CoverageExclusion(kv.Key, kv.Value)).OrderBy(e => e.Reason).ToList());
    }
}
