using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    public sealed record CostInputs(string UserCurrency, string? DefaultFilamentPrice, string? KwhRate, string? DefaultWattageW);

    /// <summary>
    /// One filament usage row flattened for costing. SourceMeasurement mirrors
    /// PrintFilament.SourceMeasurement: 1 = Weight, 2 = Length, 3 = Volume.
    /// </summary>
    public sealed record FilamentCostRow(
        string? PurchasePriceValue,
        string? PurchasePriceCurrency,
        long? InitialNominalWeightMg,
        double MaterialDensityGramPerCubicCm,
        double? DiameterMm,
        int SourceMeasurement,
        double? AmountMg,
        double? LengthInM,
        double? VolumeMl,
        int EstimatedSourceMeasurement,
        double? EstimatedAmountMg,
        double? EstimatedLengthInM,
        double? EstimatedVolumeMl);

    public sealed record CostResult(decimal? Amount, bool UsedDefaultPrice, IReadOnlyList<string> ExclusionReasons);

    /// <summary>
    /// Server-side port of the client rules in print.service.ts (calculateTotalPrintCost,
    /// calculatePrintCost, calculateElectricityCost), pinned to the shared fixture corpus.
    ///
    /// Costs are CURRENT-PRICE ESTIMATES: they read the spool's present price, the user's present
    /// kWh rate, and the printer's present wattage. Editing any of those changes historical figures.
    /// That is a deliberate design decision (spec §8.5), not an oversight.
    /// </summary>
    public static class PrintCostCalculator
    {
        private const int Weight = 1, Length = 2, Volume = 3;

        /// <summary>
        /// Invariant-culture parse with finite and non-negative checks. Deliberately stricter than
        /// the client's Number(...): "1,5" is rejected rather than silently becoming 15 or NaN.
        /// </summary>
        public static decimal? ParseInvariant(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var value))
                return null;
            return value < 0 ? null : value;
        }

        public static CostResult FilamentCost(IEnumerable<FilamentCostRow>? rows, CostInputs inputs)
        {
            var exclusions = new List<string>();
            var usedDefault = false;
            decimal? total = null;

            foreach (var row in rows ?? Enumerable.Empty<FilamentCostRow>())
            {
                // Currency gate first: a mismatched row contributes nothing and is reported.
                if (!CurrencyMatches(row.PurchasePriceCurrency, inputs.UserCurrency))
                {
                    exclusions.Add(ExclusionReason.CurrencyMismatch);
                    continue;
                }

                // "Not set" and "not valid" are different, and the client already treats them so.
                // An ABSENT price falls back to the user's default; a PRESENT but unparseable one
                // ("twenty five", "1,5") is corrupt data and excludes the row. Falling back there
                // would silently invent a plausible cost from a value we failed to understand.
                var priceIsAbsent = string.IsNullOrWhiteSpace(row.PurchasePriceValue);
                var spoolPrice = ParseInvariant(row.PurchasePriceValue);
                var isDefault = false;
                if (spoolPrice is null && priceIsAbsent)
                {
                    spoolPrice = ParseInvariant(inputs.DefaultFilamentPrice);
                    isDefault = spoolPrice is not null;
                }

                if (spoolPrice is null || row.InitialNominalWeightMg is null or 0)
                {
                    exclusions.Add(ExclusionReason.PriceMissing);
                    continue;
                }

                var pricePerGram = spoolPrice.Value / (decimal)(row.InitialNominalWeightMg.Value / 1000.0);

                var (grams, isEstimated) = ResolveGrams(row);
                if (grams is null)
                {
                    exclusions.Add(ExclusionReason.MaterialEstimated);
                    continue;
                }
                if (isEstimated) exclusions.Add(ExclusionReason.MaterialEstimated);
                if (isDefault) usedDefault = true;

                total = (total ?? 0m) + decimal.Round(pricePerGram * (decimal)grams.Value, 2, MidpointRounding.AwayFromZero);
            }

            return new CostResult(total.HasValue ? decimal.Round(total.Value, 2, MidpointRounding.AwayFromZero) : null,
                usedDefault, exclusions.Distinct().ToList());
        }

        /// <summary>
        /// Mirrors the client's per-row selection: the actual path when the source-appropriate
        /// actual measurement is recorded, otherwise the estimated path with estimatedSource.
        /// </summary>
        private static (double? Grams, bool IsEstimated) ResolveGrams(FilamentCostRow r)
        {
            var hasActual = r.SourceMeasurement == Length
                ? r.LengthInM is > 0
                : r.SourceMeasurement == Volume
                    ? r.VolumeMl is > 0
                    : r.AmountMg is > 0;

            if (hasActual)
                return (ToGrams(r.SourceMeasurement, r.AmountMg, r.LengthInM, r.VolumeMl, r), false);

            var grams = ToGrams(r.EstimatedSourceMeasurement, r.EstimatedAmountMg, r.EstimatedLengthInM, r.EstimatedVolumeMl, r);
            return (grams, grams is not null);
        }

        private static double? ToGrams(int source, double? amountMg, double? lengthM, double? volumeMl, FilamentCostRow r)
        {
            switch (source)
            {
                case Weight:
                    return amountMg is > 0 ? amountMg.Value / 1000.0 : null;
                case Volume:
                    return volumeMl is > 0 ? volumeMl.Value * r.MaterialDensityGramPerCubicCm : null;
                case Length:
                    if (lengthM is not > 0 || r.DiameterMm is not > 0) return null;
                    var radiusCm = r.DiameterMm.Value / 20.0;         // mm diameter → cm radius
                    var lengthCm = lengthM.Value * 100.0;
                    var volumeCm3 = Math.PI * radiusCm * radiusCm * lengthCm;
                    return volumeCm3 * r.MaterialDensityGramPerCubicCm;
                default:
                    return null;
            }
        }

        private static bool CurrencyMatches(string? rowCurrency, string? userCurrency)
        {
            // Legacy spools predate the currency field; treat absent as matching rather than
            // excluding years of data.
            if (string.IsNullOrWhiteSpace(rowCurrency)) return true;
            if (string.IsNullOrWhiteSpace(userCurrency)) return true;
            return string.Equals(rowCurrency.Trim(), userCurrency.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static CostResult ElectricityCost(int durationSeconds, double? printerWattageW, CostInputs inputs)
        {
            // Zero or negative duration means NOT RECORDED (see PrintMetrics), so the cost is
            // unknown, not zero. Returning 0.00 here would put a confident "$0.00 of electricity"
            // against every print whose duration was never captured. This matches
            // calculateElectricityCost in print.service.ts, which returns an invalid result.
            if (durationSeconds <= 0) return new CostResult(null, false, Array.Empty<string>());

            var exclusions = new List<string>();

            var rate = ParseInvariant(inputs.KwhRate);
            if (rate is null) exclusions.Add(ExclusionReason.RateMissing);

            var watts = printerWattageW is > 0
                ? (decimal?)printerWattageW.Value
                : ParseInvariant(inputs.DefaultWattageW);
            if (watts is null or 0) exclusions.Add(ExclusionReason.WattageMissing);

            if (rate is null || watts is null or 0)
                return new CostResult(null, false, exclusions);

            var hours = (decimal)durationSeconds / 3600m;
            var kwh = watts.Value * hours / 1000m;
            return new CostResult(decimal.Round(kwh * rate.Value, 2, MidpointRounding.AwayFromZero), false, exclusions);
        }
    }
}
