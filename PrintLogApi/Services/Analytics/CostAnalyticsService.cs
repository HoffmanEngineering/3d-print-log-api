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
    /// The Costs tab. Every figure comes from AnalyticsCostProjection, so it obeys the same
    /// currency, default-price and parse rules as the cost tile on Overview — the two surfaces
    /// disagreeing is the failure mode this design exists to prevent.
    /// </summary>
    public sealed class CostAnalyticsService : ICostAnalyticsService
    {
        public const int MaxExtremes = 5;
        public const int MaxCostGroups = 15;

        /// <summary>
        /// Fixed money bands, half-open, always all present. Quantile bands would relabel the
        /// axis on every filter change; a fixed band is what a user compares against last month.
        /// </summary>
        private static readonly (string Label, decimal Lower, decimal? Upper)[] Bands =
        {
            ("<1", 0m, 1m), ("1–2", 1m, 2m), ("2–5", 2m, 5m), ("5–10", 5m, 10m),
            ("10–25", 10m, 25m), ("25–50", 25m, 50m), ("50–100", 50m, 100m), ("100+", 100m, null),
        };

        private readonly PrintLogContext _context;

        public CostAnalyticsService(PrintLogContext context) => _context = context;

        public async Task<CostsResponse> GetCosts(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            var current = await Compute(userId, filter, ct);

            var previousFilter = PreviousWindow.For(filter);
            if (previousFilter is null) return current;

            var previous = await Compute(userId, previousFilter, ct);

            // Only the SCALAR tiles carry a delta. A bucket-by-bucket delta on a series is a
            // different chart, not a delta, and the spec asks for tile deltas.
            return current with
            {
                TotalSpend = current.TotalSpend with { Previous = PreviousWindow.Usable(previous.TotalSpend.Value) },
                FilamentSpend = current.FilamentSpend with { Previous = PreviousWindow.Usable(previous.FilamentSpend.Value) },
                ElectricitySpend = current.ElectricitySpend with { Previous = PreviousWindow.Usable(previous.ElectricitySpend.Value) },
                MaintenanceSpend = current.MaintenanceSpend with { Previous = PreviousWindow.Usable(previous.MaintenanceSpend.Value) },
                CostOfFailure = current.CostOfFailure with { Previous = PreviousWindow.Usable(previous.CostOfFailure.Value) },
            };
        }

        private async Task<CostsResponse> Compute(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var scoped = AnalyticsQueryScope.Scope(
                _context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

            var coverage = new CoverageBuilder("prints") { Total = await scoped.CountAsync(ct) };
            coverage.UndatedCount = filter.HasRange
                ? 0 : await scoped.CountAsync(p => p.StartDate == null, ct);

            var projection = await AnalyticsCostProjection.Project(_context, userId, scoped, ct);
            var currency = projection.Inputs.UserCurrency;

            if (projection.RowCapExceeded)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, projection.PrintCount);
                return EmptyResponse(filter, granularity, currency, coverage.Build());
            }

            foreach (var (reason, count) in AnalyticsCostProjection.CountExclusions(projection.Prints))
                coverage.Exclude(reason, count);
            coverage.Counted = projection.Prints.Count(p => p.Total.HasValue);

            var filamentSpend = Sum(projection.Prints.Select(p => p.FilamentCost));
            var electricitySpend = Sum(projection.Prints.Select(p => p.ElectricityCost));

            var maintenance = await LoadMaintenance(userId, filter, zone, coverage, ct);
            var maintenanceSpend = maintenance.Count == 0
                ? (decimal?)null
                : maintenance.Sum(m => m.Cost);

            var total = filamentSpend is null && electricitySpend is null && maintenanceSpend is null
                ? (decimal?)null
                : (filamentSpend ?? 0m) + (electricitySpend ?? 0m) + (maintenanceSpend ?? 0m);

            var failureTotal = Sum(projection.Prints
                .Where(p => p.Status == Print.PrintStatus.Failed || p.Status == Print.PrintStatus.Cancelled)
                .Select(p => p.Total));

            var byMaterialType = await ByFilamentAttribute(userId, scoped, projection, byBrand: false, ct);
            var byBrand = await ByFilamentAttribute(userId, scoped, projection, byBrand: true, ct);

            var costCoverage = coverage.Build();

            return new CostsResponse(
                filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(), currency,
                new MoneyMetric(total, null, currency, costCoverage),
                new MoneyMetric(filamentSpend, null, currency, costCoverage),
                new MoneyMetric(electricitySpend, null, currency, costCoverage),
                new MoneyMetric(maintenanceSpend, null, currency, costCoverage),
                BuildSeries(projection.Prints, maintenance, filter, zone, granularity),
                Distribution(projection.Prints),
                byMaterialType,
                byBrand,
                new MoneyMetric(failureTotal, null, currency, costCoverage),
                // A share of nothing is undefined. 0% would read as "no failures".
                total is null or 0m || failureTotal is null
                    ? null
                    : (double)(failureTotal.Value / total.Value) * 100.0,
                Extremes(projection.Prints, zone, descending: true),
                Extremes(projection.Prints, zone, descending: false),
                costCoverage);
        }

        private static decimal? Sum(IEnumerable<decimal?> values)
        {
            var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return list.Count == 0 ? null : list.Sum();
        }

        private static IReadOnlyList<HistogramBucket> Distribution(IReadOnlyList<CostedPrint> prints)
        {
            var counts = new int[Bands.Length];
            foreach (var amount in prints.Where(p => p.Total.HasValue).Select(p => p.Total!.Value))
            {
                for (var i = 0; i < Bands.Length; i++)
                {
                    var (_, lower, upper) = Bands[i];
                    if (amount >= lower && (upper is null || amount < upper.Value)) { counts[i]++; break; }
                }
            }

            return Bands
                .Select((b, i) => new HistogramBucket(b.Label, (int)b.Lower, (int?)b.Upper, counts[i]))
                .ToList();
        }

        private static IReadOnlyList<PrintCostRef> Extremes(
            IReadOnlyList<CostedPrint> prints, TimeZoneInfo zone, bool descending)
        {
            // Only prints with a KNOWN cost. A print whose cost could not be computed is not
            // "the cheapest print"; it is a print we cannot price.
            var priced = prints.Where(p => p.Total.HasValue);

            var ordered = descending
                ? priced.OrderByDescending(p => p.Total!.Value).ThenBy(p => p.PrintId)
                : priced.OrderBy(p => p.Total!.Value).ThenBy(p => p.PrintId);

            return ordered
                .Take(MaxExtremes)
                .Select(p => new PrintCostRef(
                    p.PrintId, p.Title,
                    p.StartDate.HasValue
                        ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.StartDate.Value, zone).DateTime)
                        : null,
                    p.Total!.Value))
                .ToList();
        }

        private static IReadOnlyList<CostSeriesBucket> BuildSeries(
            IReadOnlyList<CostedPrint> prints,
            IReadOnlyList<(long PrinterId, DateOnly Date, DateTimeOffset Instant, decimal Cost)> maintenance,
            AnalyticsFilter filter, TimeZoneInfo zone, AnalyticsGranularity granularity)
        {
            var dated = prints.Where(p => p.StartDate.HasValue).ToList();
            var from = filter.FromDate
                ?? (dated.Count > 0 ? dated.Min(p => p.StartDate!.Value) : DateTimeOffset.UtcNow);
            var to = filter.ToDate ?? DateTimeOffset.UtcNow;
            if (to <= from) return Array.Empty<CostSeriesBucket>();

            var buckets = TimeBucketer.BuildBuckets(from, to, zone, granularity, DayOfWeek.Sunday);
            if (buckets.Count == 0) return Array.Empty<CostSeriesBucket>();

            var filament = new decimal?[buckets.Count];
            var electricity = new decimal?[buckets.Count];
            var maintenanceTotals = new decimal?[buckets.Count];

            foreach (var print in dated)
            {
                var index = TimeBucketer.IndexOf(buckets, print.StartDate!.Value.ToUniversalTime());
                if (index < 0) continue;
                if (print.FilamentCost.HasValue)
                    filament[index] = (filament[index] ?? 0m) + print.FilamentCost.Value;
                if (print.ElectricityCost.HasValue)
                    electricity[index] = (electricity[index] ?? 0m) + print.ElectricityCost.Value;
            }

            foreach (var entry in maintenance)
            {
                var index = TimeBucketer.IndexOf(buckets, entry.Instant.ToUniversalTime());
                if (index < 0) continue;
                maintenanceTotals[index] = (maintenanceTotals[index] ?? 0m) + entry.Cost;
            }

            return buckets
                .Select(b => new CostSeriesBucket(
                    b.Index, b.LocalStart, filament[b.Index], electricity[b.Index], maintenanceTotals[b.Index]))
                .ToList();
        }

        /// <summary>
        /// Cost attributed to a spool ATTRIBUTE (material type, brand). This is a different grain
        /// from the print-level totals — a print using PLA and PETG splits between them — so
        /// these deliberately do not have to sum to FilamentSpend when rows were excluded.
        /// </summary>
        private async Task<IReadOnlyList<CostGroup>> ByFilamentAttribute(
            long userId, IQueryable<Print> scoped, CostProjection projection,
            bool byBrand, CancellationToken ct)
        {
            // `scoped` IS the projection's population — AnalyticsCostProjection.Project reads it
            // unfiltered — so re-filtering by the projected ids would only add a 20 000-parameter
            // IN clause for no change in the result set.
            if (projection.Prints.Count == 0) return Array.Empty<CostGroup>();

            // Flattened with the result selector and filtered OUTSIDE the SelectMany, not with a
            // Where inside it. The inner-filter form is a correlated subquery, which needs SQL
            // APPLY — unsupported on SQLite, so it throws under the integration-test provider
            // while working on SQL Server. This form is a plain INNER JOIN on every provider.
            var rows = await scoped
                .SelectMany(p => p.FilamentUsage, (p, pf) => new { p, pf })
                .Where(x => x.pf.Filament != null && x.pf.Filament.CreatedById == userId)
                .Select(x => new
                {
                    PrintId = x.p.Id,
                    x.pf.Filament.MaterialType,
                    x.pf.Filament.Brand,
                    x.pf.Filament.PurchasePriceValue,
                    x.pf.Filament.PurchasePriceCurrency,
                    x.pf.Filament.InitialNominalWeightMg,
                    x.pf.Filament.MaterialDensityGramPerCubicCm,
                    x.pf.Filament.DiameterMm,
                    Source = (int)x.pf.Source,
                    AmountMg = (double?)x.pf.AmountMg,
                    x.pf.LengthInM,
                    x.pf.VolumeMl,
                    EstimatedSource = (int)x.pf.EstimatedSource,
                    EstimatedAmountMg = (double?)x.pf.EstimatedAmountMg,
                    x.pf.EstimatedLengthInM,
                    x.pf.EstimatedVolumeMl,
                })
                .ToListAsync(ct);

            var groups = rows
                .Select(r => new
                {
                    // The key is chosen HERE, in memory. A delegate over the entity cannot be
                    // applied inside the EF projection above, and a nullable-delegate trick
                    // silently grouped both calls by MaterialType.
                    Key = (byBrand ? r.Brand : r.MaterialType) ?? "Unknown",
                    r.PrintId,
                    // One row at a time, through the SAME calculator every other figure uses.
                    Cost = PrintCostCalculator.FilamentCost(
                        new[]
                        {
                            new FilamentCostRow(
                                r.PurchasePriceValue, r.PurchasePriceCurrency, r.InitialNominalWeightMg,
                                r.MaterialDensityGramPerCubicCm, r.DiameterMm,
                                r.Source, r.AmountMg, r.LengthInM, r.VolumeMl,
                                r.EstimatedSource, r.EstimatedAmountMg, r.EstimatedLengthInM, r.EstimatedVolumeMl),
                        },
                        projection.Inputs).Amount,
                })
                .Where(x => x.Cost.HasValue)
                .ToList();

            return groups
                .GroupBy(x => x.Key)
                .Select(g => new CostGroup(
                    g.Key, g.Key, g.Sum(x => x.Cost!.Value), g.Select(x => x.PrintId).Distinct().Count()))
                .OrderByDescending(g => g.Amount).ThenBy(g => g.Key)
                .Take(MaxCostGroups)
                .ToList();
        }

        private async Task<IReadOnlyList<(long PrinterId, DateOnly Date, DateTimeOffset Instant, decimal Cost)>> LoadMaintenance(
            long userId, AnalyticsFilter filter, TimeZoneInfo zone,
            CoverageBuilder coverage, CancellationToken ct)
        {
            var query = _context.PrinterMaintenance.AsNoTracking()
                .Where(m => m.Printer.UserId == userId && m.Done);

            if (filter.PrinterIds.Count > 0)
                query = query.Where(m => filter.PrinterIds.Contains(m.PrinterId));
            if (filter.HasRange)
                query = query.Where(m => m.Date >= filter.FromDate.Value && m.Date < filter.ToDate.Value);

            // Bounded like every other second stage: two narrow columns make the ceiling generous,
            // and exceeding it is reported rather than silently truncating the money.
            var totalRows = await query.CountAsync(ct);
            if (totalRows > AnalyticsService.MaxSeriesRows)
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, totalRows);
                return Array.Empty<(long, DateOnly, DateTimeOffset, decimal)>();
            }

            var rows = await query
                .Select(m => new { m.PrinterId, m.Date, m.PriceValue })
                .ToListAsync(ct);

            var parsed = rows
                .Select(m => (
                    m.PrinterId,
                    LocalDate: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.Date, zone).DateTime),
                    m.Date,
                    Cost: PrintCostCalculator.ParseInvariant(m.PriceValue),
                    HasPrice: !string.IsNullOrWhiteSpace(m.PriceValue)))
                .ToList();

            // A price that was ENTERED but could not be parsed is a distinct failure from one
            // that was never entered (spec §8.3). Dropping both silently would understate spend
            // with nothing on screen to explain the gap.
            coverage.Exclude(
                ExclusionReason.PriceMissing,
                parsed.Count(m => m.Cost is null && m.HasPrice));

            return parsed
                .Where(m => m.Cost.HasValue)
                .Select(m => (m.PrinterId, m.LocalDate, m.Date, m.Cost!.Value))
                .ToList();
        }

        private static CostsResponse EmptyResponse(
            AnalyticsFilter filter, AnalyticsGranularity granularity, string currency, Coverage coverage)
        {
            var money = new MoneyMetric(null, null, currency, coverage);
            return new CostsResponse(
                filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(), currency,
                money, money, money, money,
                Array.Empty<CostSeriesBucket>(),
                Distribution(Array.Empty<CostedPrint>()),
                Array.Empty<CostGroup>(), Array.Empty<CostGroup>(),
                money, null,
                Array.Empty<PrintCostRef>(), Array.Empty<PrintCostRef>(),
                coverage);
        }
    }
}
