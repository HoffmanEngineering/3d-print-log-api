#nullable enable

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
    /// One print, costed. Filament and electricity stay SEPARATE because the Costs tab splits
    /// spend by component and the two fail independently — a print can have a known filament
    /// cost and an unknown electricity cost.
    /// </summary>
    public sealed record CostedPrint(
        long PrintId,
        string? Title,
        System.DateTimeOffset? StartDate,
        Print.PrintStatus Status,
        long PrinterId,
        decimal? FilamentCost,
        decimal? ElectricityCost,
        IReadOnlyList<string> ExclusionReasons)
    {
        /// <summary>
        /// Null when NEITHER component is knowable. Deliberately not 0m: a confident "$0.00"
        /// against a print with no price and no wattage is a claim we cannot support.
        /// </summary>
        public decimal? Total =>
            FilamentCost is null && ElectricityCost is null
                ? null
                : (FilamentCost ?? 0m) + (ElectricityCost ?? 0m);
    }

    public sealed record CostProjection(
        IReadOnlyList<CostedPrint> Prints,
        CostInputs Inputs,
        bool RowCapExceeded,
        int PrintCount);

    /// <summary>
    /// The two numbers the row cap is decided from. Named rather than anonymous so the query
    /// that produces them can be lifted out of the projection and asserted on directly.
    /// </summary>
    public sealed record CostRowCounts(int PrintRows, int FilamentRows)
    {
        public static readonly CostRowCounts Empty = new(0, 0);
    }

    /// <summary>
    /// The single per-print costing pass, shared by /overview, /activity, /printers and /costs.
    /// Costing needs per-filament-row detail, so it is the one projection in analytics that is
    /// bounded on ROWS rather than prints — a four-spool print materializes four rows, and a cap
    /// counting prints would let a multi-material library blow several times past its own limit.
    /// </summary>
    public static class AnalyticsCostProjection
    {
        /// <summary>
        /// The user's cost settings. Public because callers that price something OTHER than a
        /// whole print — the Materials tab's per-spool consumed cost — still have to use the
        /// same currency, default-price and rate inputs, and re-reading the settings ad hoc is
        /// how two surfaces come to disagree.
        /// </summary>
        public static async Task<CostInputs> LoadInputs(
            PrintLogContext context, long userId, CancellationToken ct)
        {
            var settings = await context.UserSettings.AsNoTracking()
                .Where(s => s.UserId == userId)
                .Select(s => new { s.UserSettingTypeId, s.Value })
                .ToListAsync(ct);

            string? Setting(int id) => settings.FirstOrDefault(s => s.UserSettingTypeId == id)?.Value;

            return new CostInputs(
                // Null-forgiven at the single source rather than nullable: UserCurrency feeds
                // the Currency of every analytics response record, whose positional parameters
                // are non-nullable by the #43 convention. A user with no currency setting
                // already yields a null Currency today; this keeps that unchanged instead of
                // pushing eight null-forgives out to the response boundary.
                UserCurrency: Setting(5)!,           // Currency_Name
                DefaultFilamentPrice: Setting(8),    // Filaments_DefaultPrice
                KwhRate: Setting(12),                // Electricity_KwhRate
                DefaultWattageW: Setting(13));       // Electricity_DefaultWattageW
        }

        /// <summary>
        /// Cap on the rows that would actually be materialized: one per filament usage row, plus
        /// one per print for the printer/electricity term. Counting prints alone would let a
        /// multi-material library blow several times past the limit.
        ///
        /// Both counts come from ONE aggregate: they are two numbers about the same filtered set,
        /// and asking for them separately meant two scans to decide a single question. The
        /// per-print filament count stays a CORRELATED subquery rather than a join, so a
        /// four-spool print contributes 4 to the sum without multiplying the outer row set.
        ///
        /// Exposed separately from <see cref="Project"/> so its translation can be asserted
        /// against the production provider without a database — see the note on
        /// <see cref="AnalyticsPrintCounts.Query"/>.
        /// </summary>
        public static IQueryable<CostRowCounts> CapsQuery(IQueryable<Print> scoped) =>
            scoped
                .GroupBy(_ => 1)
                .Select(g => new CostRowCounts(
                    g.Count(),
                    g.Sum(p => p.FilamentUsage!.Count())));

        /// <param name="inputs">
        /// Pre-loaded settings, for a caller that has already read them. Passing them avoids a
        /// second identical UserSettings round-trip AND removes the possibility of one request
        /// costing two things against two different reads of the same rate.
        /// </param>
        public static async Task<CostProjection> Project(
            PrintLogContext context, long userId, IQueryable<Print> scoped, CancellationToken ct,
            CostInputs? inputs = null)
        {
            inputs ??= await LoadInputs(context, userId, ct);

            var caps = await CapsQuery(scoped).FirstOrDefaultAsync(ct) ?? CostRowCounts.Empty;

            var filamentRows = caps.FilamentRows;
            var printRows = caps.PrintRows;
            if (filamentRows + printRows > AnalyticsService.MaxCostRows)
                return new CostProjection(System.Array.Empty<CostedPrint>(), inputs, true, printRows);

            var projected = await scoped
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    p.StartDate,
                    p.Status,
                    p.PrinterId,
                    DurationSeconds = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
                        ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                            ? p.EstimatedPrintTimeInSeconds.Value
                            : 0,
                    // Owner-scoped join, NOT just `!= null`. The spec requires every join to
                    // carry the tenant predicate, and the shipped ComputeCost omits it — this
                    // lift is the moment to close that gap rather than propagate it into four
                    // more endpoints. A wattage or price read through an unscoped navigation is
                    // a cross-tenant read even when the parent print is correctly scoped.
                    WattageW = p.Printer.UserId == userId ? p.Printer.WattageW : null,
                    Rows = p.FilamentUsage!
                        .Where(pf => pf.Filament != null && pf.Filament.CreatedById == userId)
                        .Select(pf => new
                    {
                        pf.Filament!.PurchasePriceValue,
                        pf.Filament.PurchasePriceCurrency,
                        pf.Filament.InitialNominalWeightMg,
                        pf.Filament.MaterialDensityGramPerCubicCm,
                        pf.Filament.DiameterMm,
                        Source = (int)pf.Source,
                        AmountMg = (double?)pf.AmountMg,
                        pf.LengthInM,
                        pf.VolumeMl,
                        EstimatedSource = (int)pf.EstimatedSource,
                        EstimatedAmountMg = (double?)pf.EstimatedAmountMg,
                        pf.EstimatedLengthInM,
                        pf.EstimatedVolumeMl,
                    }).ToList(),
                })
                .ToListAsync(ct);

            var costed = new List<CostedPrint>(projected.Count);

            foreach (var p in projected)
            {
                var rows = p.Rows.Select(r => new FilamentCostRow(
                    r.PurchasePriceValue, r.PurchasePriceCurrency, r.InitialNominalWeightMg,
                    r.MaterialDensityGramPerCubicCm, r.DiameterMm,
                    r.Source, r.AmountMg, r.LengthInM, r.VolumeMl,
                    r.EstimatedSource, r.EstimatedAmountMg, r.EstimatedLengthInM, r.EstimatedVolumeMl));

                var filament = PrintCostCalculator.FilamentCost(rows, inputs);
                var electricity = PrintCostCalculator.ElectricityCost(p.DurationSeconds, p.WattageW, inputs);
                // A null WattageW from the ownership guard falls through to the user's default
                // wattage setting, exactly as an unset wattage does — the caller cannot tell the
                // two apart, which is the point: an unowned printer must be indistinguishable
                // from an absent one.

                // Distinct() so one print contributes at most 1 to any reason, which is what makes
                // a summed reason count readable as "N prints affected".
                var reasons = filament.ExclusionReasons
                    .Concat(electricity.ExclusionReasons)
                    .Distinct()
                    .ToList();

                costed.Add(new CostedPrint(
                    p.Id, p.Title, p.StartDate, p.Status, p.PrinterId,
                    filament.Amount, electricity.Amount, reasons));
            }

            return new CostProjection(costed, inputs, false, printRows);
        }

        /// <summary>Reason counts across a set of costed prints, counted per print.</summary>
        public static IReadOnlyDictionary<string, int> CountExclusions(IEnumerable<CostedPrint> prints)
        {
            var counts = new Dictionary<string, int>();
            foreach (var reason in prints.SelectMany(p => p.ExclusionReasons))
                counts[reason] = counts.TryGetValue(reason, out var n) ? n + 1 : 1;
            return counts;
        }
    }
}
