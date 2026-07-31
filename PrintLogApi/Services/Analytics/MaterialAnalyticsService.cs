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
    /// The Materials tab. Everything here groups over PrintFilament rows joined to their spool,
    /// which is the ONLY grain at which a type, brand or colour exists.
    ///
    /// Consequence, deliberate and tested: the group totals fall short of /overview's filament
    /// total by exactly the "other filament" scalar (Print.FilamentUsageMg), which records
    /// material never attached to a tracked spool and therefore has no attributes to group by.
    /// It is reported in coverage rather than silently absorbed into a bucket it does not
    /// belong to.
    /// </summary>
    public sealed class MaterialAnalyticsService : IMaterialAnalyticsService
    {
        public const int MaxGroups = 15;
        public const int MaxSpools = 25;
        public const int BurnRateWindowDays = 90;
        public const int MaxRunwayDays = 365;

        /// <summary>The synthetic rollup key, shared by the group lists and the time series.</summary>
        public const string OtherKey = "__other";

        /// <summary>Neutral grey, used when a spool records no colour at all (spec §10).</summary>
        public const string UnknownSwatchColor = "#9e9e9e";

        private readonly PrintLogContext _context;

        public MaterialAnalyticsService(PrintLogContext context) => _context = context;

        private sealed record UsageRow(
            long PrintId,
            Guid FilamentId, string DisplayName, string Brand, string MaterialType, string ColorName,
            IReadOnlyList<string> Colors, int ColorPattern, int FinishType, IReadOnlyList<int> Effects,
            DateTimeOffset? StartDate, Print.PrintStatus Status, long UsedMg);

        public async Task<MaterialsResponse> GetMaterials(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var scoped = AnalyticsQueryScope.Scope(
                _context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

            var coverage = new CoverageBuilder("prints")
            {
                Total = await scoped.CountAsync(ct),
            };
            coverage.UndatedCount = filter.HasRange
                ? 0
                : await scoped.CountAsync(p => p.StartDate == null, ct);

            // Bound the second stage on the grain it actually materializes. Every widget on this
            // tab is built from PrintFilament ROWS, not prints, so a print-count cap would let a
            // multi-material library stream several times its own limit into memory (spec §6.4).
            var usageRowCount = await scoped.SelectMany(p => p.FilamentUsage).CountAsync(ct);
            if (usageRowCount > AnalyticsService.MaxSeriesRows)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, coverage.Total);
                return EmptyMaterials(filter, granularity,
                    await AnalyticsCostProjection.LoadInputs(_context, userId, ct), coverage.Build());
            }

            // Material that was never attached to a spool has no attributes to group by. Count
            // the PRINTS carrying it, so the coverage note reads as "N prints used filament that
            // is not linked to a spool" — without this the tab silently disagrees with Overview.
            var unattributedPrints = await scoped.CountAsync(p =>
                (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0)
                || (!(p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0)
                    && p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0), ct);
            coverage.Exclude(ExclusionReason.UnattributedMaterial, unattributedPrints);

            // Flattened with the result selector and filtered OUTSIDE the SelectMany, not with a
            // Where inside it. The inner-filter form is a correlated subquery, which needs SQL
            // APPLY — unsupported on SQLite, so it throws under the integration-test provider
            // while working on SQL Server. This form is a plain INNER JOIN on every provider.
            var rows = (await scoped
                .SelectMany(p => p.FilamentUsage, (p, pf) => new { p, pf })
                .Where(x => x.pf.FilamentId.HasValue
                    && x.pf.Filament != null
                    && x.pf.Filament.CreatedById == userId)
                .Select(x => new
                {
                    PrintId = x.p.Id,
                    FilamentId = x.pf.FilamentId.Value,
                    x.pf.Filament.DisplayName,
                    x.pf.Filament.Brand,
                    x.pf.Filament.MaterialType,
                    x.pf.Filament.ColorName,
                    x.pf.Filament.Colors,
                    x.pf.Filament.ColorHex,
                    x.pf.Filament.ColorPattern,
                    x.pf.Filament.FinishType,
                    x.pf.Filament.Effects,
                    x.p.StartDate,
                    x.p.Status,
                    UsedMg = (long)(x.pf.AmountMg.HasValue && x.pf.AmountMg > 0 ? x.pf.AmountMg.Value
                        : x.pf.EstimatedAmountMg.HasValue && x.pf.EstimatedAmountMg > 0 ? x.pf.EstimatedAmountMg.Value
                        : 0),
                })
                .ToListAsync(ct))
                .Select(r => new UsageRow(
                    r.PrintId,
                    r.FilamentId, r.DisplayName, r.Brand, r.MaterialType, r.ColorName,
                    SwatchColors(r.Colors, r.ColorHex),
                    (int)(r.ColorPattern ?? Enums.ColorPatternType.Solid),
                    (int)(r.FinishType ?? Enums.FilamentFinishType.Standard),
                    (r.Effects ?? new List<Enums.FilamentEffect>()).Select(e => (int)e).ToList(),
                    r.StartDate, r.Status, r.UsedMg))
                .ToList();

            // A usage row with no recorded amount resolves to 0 and is dropped here rather than
            // carried into grouping: it would otherwise produce a zero-mass material group and a
            // zero-use "top spool", and make the tab report itself non-empty on the strength of
            // rows that say nothing. The ROW CAP above deliberately still counts them, because
            // its job is bounding what gets materialized, and these rows are materialized.
            rows = rows.Where(r => r.UsedMg > 0).ToList();

            // Counted and Total must describe the SAME population, which the builder names as
            // "prints". Distinct filaments is a different population, and reporting it here
            // produced impossible coverage — "20 of 5" for a library with more spools than
            // prints in range.
            coverage.Counted = rows.Select(r => r.PrintId).Distinct().Count();

            var byType = Group(rows, r => Label(r.MaterialType));
            var byBrand = Group(rows, r => Label(r.Brand));
            var byColor = Group(rows, r => Label(r.ColorName));

            // The series must use the SAME key set the groups were truncated to. Keyed on raw
            // material type it would emit buckets under keys the UI has no series for — the UI
            // builds its series from ByType — and every type ranked below the cap would vanish
            // from a stacked chart the user reads as a total.
            var series = BuildSeries(rows, byType, filter, zone, granularity);
            var costInputs = await AnalyticsCostProjection.LoadInputs(_context, userId, ct);
            var spools = await TopSpools(userId, rows, costInputs, ct);
            var runway = Runway(spools, rows, filter);
            var (wasteGrams, wasteCost, currency) = await Waste(userId, scoped, coverage, ct);

            return new MaterialsResponse(
                filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(), currency,
                byType, byBrand, byColor, series, spools, runway,
                wasteGrams, wasteCost, coverage.Build());
        }

        /// <summary>
        /// The colour tokens for a swatch: the spool's own list, else its legacy single hex.
        ///
        /// Blank entries are dropped rather than passed through, and a spool with NO recorded
        /// colour gets neutral grey (spec §10) instead of a token the client would resolve to
        /// black — "we do not know this spool's colour" and "this spool is black" are different
        /// claims, and the swatch must not make the second one on the first one's evidence.
        /// </summary>
        private static IReadOnlyList<string> SwatchColors(List<string> colors, string colorHex)
        {
            var tokens = (colors ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToList();

            if (tokens.Count > 0) return tokens;
            if (!string.IsNullOrWhiteSpace(colorHex)) return new List<string> { colorHex.Trim() };
            return new List<string> { UnknownSwatchColor };
        }

        /// <summary>
        /// The key a row groups under. Blank and whitespace-only metadata is "Unknown", not its
        /// own nameless group, and surrounding whitespace is trimmed so " PLA " and "PLA" are
        /// one material rather than two. Case is deliberately preserved: folding it would change
        /// the label the user chose for their own spool.
        /// </summary>
        private static string Label(string value) =>
            string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

        /// <summary>
        /// Top MaxGroups by mass plus an aggregated "Other". Truncating without the rollup would
        /// silently lose mass from a chart the user reads as a total.
        /// </summary>
        private static IReadOnlyList<MaterialGroup> Group(
            IReadOnlyList<UsageRow> rows, Func<UsageRow, string> key)
        {
            var groups = rows
                .GroupBy(key)
                .Select(g =>
                {
                    var exemplar = g.OrderByDescending(r => r.UsedMg).First();
                    return new MaterialGroup(
                        g.Key, g.Key,
                        // DISTINCT prints. Counting usage rows would rank a material used twice
                        // on one multi-material print above a material used on two prints, which
                        // is not what "most used" means (spec §5).
                        g.Select(r => r.PrintId).Distinct().Count(),
                        g.Sum(r => r.UsedMg),
                        new SwatchDto(exemplar.Colors, exemplar.ColorPattern, exemplar.FinishType, exemplar.Effects));
                })
                .OrderByDescending(g => g.MaterialMg).ThenBy(g => g.Key)
                .ToList();

            if (groups.Count <= MaxGroups) return groups;

            var kept = groups.Take(MaxGroups).ToList();
            var rest = groups.Skip(MaxGroups).ToList();
            kept.Add(new MaterialGroup(
                OtherKey, "Other",
                // Summed, not de-duplicated: one print can appear under several rolled-up
                // groups, so this is "group appearances", and the label makes no per-print claim.
                rest.Sum(g => g.PrintCount),
                rest.Sum(g => g.MaterialMg),
                new SwatchDto(new List<string> { UnknownSwatchColor },
                    (int)Enums.ColorPatternType.Solid, (int)Enums.FilamentFinishType.Standard, new List<int>())));
            return kept;
        }

        /// <summary>
        /// Grams per bucket, split by material type — using the SAME keys ByType was truncated
        /// to. A type that was rolled into "Other" for the group chart is rolled into "Other"
        /// here too, so the stacked series conserves mass instead of dropping everything ranked
        /// below the cap on the floor.
        /// </summary>
        private static IReadOnlyList<MaterialSeriesBucket> BuildSeries(
            IReadOnlyList<UsageRow> rows, IReadOnlyList<MaterialGroup> byType,
            AnalyticsFilter filter, TimeZoneInfo zone, AnalyticsGranularity granularity)
        {
            var retained = byType.Select(g => g.Key).ToHashSet();
            var dated = rows.Where(r => r.StartDate.HasValue).ToList();
            if (dated.Count == 0) return Array.Empty<MaterialSeriesBucket>();

            var from = filter.FromDate ?? dated.Min(r => r.StartDate!.Value);
            var to = filter.ToDate ?? DateTimeOffset.UtcNow;
            if (to <= from) return Array.Empty<MaterialSeriesBucket>();

            var buckets = TimeBucketer.BuildBuckets(from, to, zone, granularity, DayOfWeek.Sunday);
            var accumulator = buckets.ToDictionary(b => b.Index, _ => new Dictionary<string, long>());

            foreach (var row in dated)
            {
                var index = TimeBucketer.IndexOf(buckets, row.StartDate!.Value.ToUniversalTime());
                if (index < 0) continue;

                var label = Label(row.MaterialType);
                var key = retained.Contains(label) ? label : OtherKey;
                var slot = accumulator[buckets[index].Index];
                slot[key] = (slot.TryGetValue(key, out var n) ? n : 0) + row.UsedMg;
            }

            return buckets
                .Select(b => new MaterialSeriesBucket(
                    b.Index, b.LocalStart, (IReadOnlyDictionary<string, long>)accumulator[b.Index]))
                .ToList();
        }

        /// <summary>
        /// Remaining follows the canonical rule in FilamentProfile (InitialNominalWeightMg minus
        /// resolved usage plus adjustments). It is inlined because EF cannot share the AutoMapper
        /// expression, and MaterialAnalyticsServiceTests pins this copy against
        /// FilamentSummaryDto.FilamentRemaining. Negative values are reported as-is.
        /// </summary>
        private async Task<IReadOnlyList<SpoolRow>> TopSpools(
            long userId, IReadOnlyList<UsageRow> rows, CostInputs inputs, CancellationToken ct)
        {
            var usedByFilament = rows
                .GroupBy(r => r.FilamentId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.UsedMg));

            if (usedByFilament.Count == 0) return Array.Empty<SpoolRow>();

            var ids = usedByFilament.Keys.ToList();

            var spools = await _context.Filaments.AsNoTracking()
                .Where(f => f.CreatedById == userId && ids.Contains(f.Id))
                .Select(f => new
                {
                    f.Id,
                    f.DisplayName,
                    f.Brand,
                    f.MaterialType,
                    f.ColorName,
                    f.InitialNominalWeightMg,
                    // Priced through PrintCostCalculator below, never with ad-hoc arithmetic:
                    // the currency gate and the default-price fallback live there.
                    f.PurchasePriceValue,
                    f.PurchasePriceCurrency,
                    f.MaterialDensityGramPerCubicCm,
                    f.DiameterMm,
                    RemainingMg = f.InitialNominalWeightMg.HasValue
                        ? (long?)(f.InitialNominalWeightMg.Value
                            - f.PrintFilaments.Sum(pf =>
                                pf.AmountMg.HasValue && pf.AmountMg > 0 ? (long)pf.AmountMg
                                : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? (long)pf.EstimatedAmountMg
                                : (long)0)
                            + f.FilamentAdjustments.Sum(adj => adj.AmountMg))
                        : null,
                })
                .ToListAsync(ct);

            var swatches = rows
                .GroupBy(r => r.FilamentId)
                .ToDictionary(
                    g => g.Key,
                    g => new SwatchDto(g.First().Colors, g.First().ColorPattern, g.First().FinishType, g.First().Effects));

            return spools
                .Select(s =>
                {
                    var used = usedByFilament[s.Id];

                    // Cost of the material actually consumed off THIS spool, priced by weight
                    // because `used` is already resolved milligrams. Routing it through
                    // PrintCostCalculator rather than multiplying inline is what keeps the
                    // currency-mismatch and default-price rules identical to every other cost
                    // figure in the product.
                    var costConsumed = PrintCostCalculator.FilamentCost(
                        new[]
                        {
                            new FilamentCostRow(
                                s.PurchasePriceValue, s.PurchasePriceCurrency,
                                s.InitialNominalWeightMg, s.MaterialDensityGramPerCubicCm,
                                s.DiameterMm,
                                (int)Filament.SourceMeasurement.Weight, used, null, null,
                                (int)Filament.SourceMeasurement.Weight, null, null, null),
                        },
                        inputs).Amount;

                    return new SpoolRow(
                        s.Id.ToString(),
                        string.Join(" · ", new[] { s.DisplayName, s.Brand, s.MaterialType, s.ColorName }
                            .Where(x => !string.IsNullOrWhiteSpace(x))),
                        swatches[s.Id],
                        used,
                        s.RemainingMg,
                        s.InitialNominalWeightMg,
                        s.InitialNominalWeightMg is > 0
                            ? 100.0 * used / s.InitialNominalWeightMg.Value
                            : null,
                        costConsumed);
                })
                .OrderByDescending(s => s.UsedMg).ThenBy(s => s.FilamentId)
                .Take(MaxSpools)
                .ToList();
        }

        /// <summary>
        /// Burn rate over the trailing min(window, 90 days), NOT the whole filter range: a
        /// five-year range would compute a rate from an era that says nothing about this month.
        /// Runway is suppressed at zero burn and never extrapolated past a year.
        /// </summary>
        private static IReadOnlyList<RunwayRow> Runway(
            IReadOnlyList<SpoolRow> spools, IReadOnlyList<UsageRow> rows, AnalyticsFilter filter)
        {
            var windowTo = filter.ToDate ?? DateTimeOffset.UtcNow;
            var windowFrom = filter.FromDate ?? windowTo.AddDays(-BurnRateWindowDays);
            var days = Math.Min((windowTo - windowFrom).TotalDays, BurnRateWindowDays);
            if (days <= 0) return Array.Empty<RunwayRow>();

            var burnFrom = windowTo.AddDays(-days);
            var recent = rows
                .Where(r => r.StartDate.HasValue && r.StartDate.Value >= burnFrom && r.StartDate.Value < windowTo)
                .GroupBy(r => r.FilamentId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.UsedMg) / 1000.0 / days);

            return spools
                .Where(s => s.RemainingMg.HasValue)
                .Select(s =>
                {
                    var id = Guid.Parse(s.FilamentId);
                    var burn = recent.TryGetValue(id, out var rate) ? rate : 0;
                    var remainingGrams = s.RemainingMg!.Value / 1000.0;

                    return new RunwayRow(
                        s.FilamentId, s.Label, s.Swatch, remainingGrams, burn,
                        burn <= 0 || remainingGrams <= 0
                            ? null
                            : Math.Min(MaxRunwayDays, remainingGrams / burn));
                })
                .OrderBy(r => r.RunwayDays ?? double.MaxValue).ThenBy(r => r.FilamentId)
                .ToList();
        }

        /// <summary>
        /// Failed + Cancelled only. PartialSuccess is deliberately excluded: partial output has
        /// value, and folding it in would inflate "waste" with prints the user actually used.
        /// </summary>
        private async Task<(Metric Grams, MoneyMetric Cost, string Currency)> Waste(
            long userId, IQueryable<Print> scoped, CoverageBuilder coverage, CancellationToken ct)
        {
            var wasted = scoped.Where(p =>
                p.Status == Print.PrintStatus.Failed || p.Status == Print.PrintStatus.Cancelled);

            var wastedMg = await wasted.SumAsync(PrintMetrics.MaterialMgExpr, ct);
            var wastedCount = await wasted.CountAsync(ct);

            // The COST metric carries its own coverage. The stat tile renders the honesty note
            // from the metric it is given, not from the tab, so pricing exclusions recorded only
            // at tab level would leave "Cost of waste" showing a partial — or null — figure with
            // nothing on the tile to say why.
            var costCoverage = new CoverageBuilder("prints") { Total = wastedCount };

            var projection = await AnalyticsCostProjection.Project(_context, userId, wasted, ct);
            decimal? cost = null;
            if (projection.RowCapExceeded)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
                costCoverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
            }
            else
            {
                var priced = projection.Prints.Where(p => p.Total.HasValue).ToList();
                cost = priced.Count > 0 ? priced.Sum(p => p.Total ?? 0m) : null;
                costCoverage.Counted = priced.Count;

                foreach (var (reason, count) in AnalyticsCostProjection.CountExclusions(projection.Prints))
                {
                    coverage.Exclude(reason, count);
                    costCoverage.Exclude(reason, count);
                }
            }

            // Mass does not depend on pricing, so the grams metric keeps a clean record: every
            // wasted print's material is counted, whether or not it could be costed.
            var massCoverage = new CoverageBuilder("prints") { Counted = wastedCount, Total = wastedCount }.Build();

            return (
                new Metric(wastedMg / 1000.0, null, massCoverage),
                new MoneyMetric(cost, null, projection.Inputs.UserCurrency, costCoverage.Build()),
                projection.Inputs.UserCurrency);
        }

        /// <summary>
        /// The shape returned when the row cap bites. Everything is empty and the coverage record
        /// says why — an empty tab with no explanation reads as "you have no materials".
        /// </summary>
        private static MaterialsResponse EmptyMaterials(
            AnalyticsFilter filter, AnalyticsGranularity granularity, CostInputs inputs, Coverage coverage) =>
            new(filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
                inputs.UserCurrency,
                Array.Empty<MaterialGroup>(), Array.Empty<MaterialGroup>(), Array.Empty<MaterialGroup>(),
                Array.Empty<MaterialSeriesBucket>(), Array.Empty<SpoolRow>(), Array.Empty<RunwayRow>(),
                new Metric(null, null, coverage),
                new MoneyMetric(null, null, inputs.UserCurrency, coverage),
                coverage);
    }
}
