using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Input-bound validation for the material write surface, shared by create_material and
    /// update_material so the two can never drift apart. Every rule is expressed against
    /// <see cref="MaterialAttributesInput"/> alone; anything needing the database (category
    /// existence, the category's diameter requirement, capacity representability) lives in the
    /// service, which is the only place that knows the post-patch state.
    /// </summary>
    public static class McpMaterialValidation
    {
        /// <summary>Matches the entity's ColorHex column: exactly 6 hex digits, no leading '#'.</summary>
        private static readonly Regex HexColor = new("^[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        /// <summary>Upper bound on swatches in one material, so a single call cannot submit an unbounded array.</summary>
        public const int MaxColors = 32;

        /// <summary>
        /// The material fields update_material will clear on request. Excluded by product choice:
        /// displayName, materialType, materialCategoryNickname, densityGramPerCubicCm, source,
        /// initialAmount, isActive, isFavorite — product identity, or required for the capacity
        /// computation. (Only MaterialCategoryNickname is [Required] at the database level; the rest
        /// are a deliberate policy, not a schema constraint.)
        /// <para>
        /// This lives here, not on the tool, so the SERVICE enforces it: a tool wrapper is one caller
        /// among several (tests call the service directly), and an allow-list checked only at the
        /// wrapper is not an invariant, just a habit.
        /// </para>
        /// </summary>
        public static readonly IReadOnlySet<string> ClearableFields = new HashSet<string>
        {
            "brand", "colorName", "colorHex", "colors", "storageLocation", "notes",
            "purchaseLocation", "purchasePriceValue", "purchasePriceCurrency", "purchaseNotes",
            "inertGas", "purchaseDate", "spoolWeightGrams", "initialTotalWeightGrams", "diameterMm",
            "tempRangeStartC", "tempRangeEndC", "recommendedTempC", "recommendedBedTempC",
            "initialLayerTimeS", "layerTimeS", "meltingTemperatureC", "materialRefreshRatio",
            "colorPattern", "finishType", "effects",
        };

        public static void RequireClearableFields(ISet<string> clear)
        {
            if (clear == null)
            {
                return;
            }
            foreach (var field in clear)
            {
                if (!ClearableFields.Contains(field))
                {
                    throw McpToolException.InvalidArguments($"'{field}' is not a clearable field.");
                }
            }
        }

        public static void ValidateAttributes(MaterialAttributesInput input)
        {
            McpWriteValidation.RequireMaxLength(input.DisplayName, 255, "displayName");
            McpWriteValidation.RequireMaxLength(input.MaterialType, 255, "materialType");
            McpWriteValidation.RequireMaxLength(input.MaterialCategoryNickname, 50, "materialCategoryNickname");
            McpWriteValidation.RequireMaxLength(input.Brand, 255, "brand");
            McpWriteValidation.RequireMaxLength(input.ColorName, 255, "colorName");
            McpWriteValidation.RequireMaxLength(input.StorageLocation, 256, "storageLocation");
            McpWriteValidation.RequireMaxLength(input.Notes, 1000, "notes");
            McpWriteValidation.RequireMaxLength(input.InertGas, 255, "inertGas");
            McpWriteValidation.RequireMaxLength(input.PurchaseLocation, 1000, "purchaseLocation");
            McpWriteValidation.RequireMaxLength(input.PurchasePriceValue, 256, "purchasePriceValue");
            McpWriteValidation.RequireMaxLength(input.PurchasePriceCurrency, 256, "purchasePriceCurrency");
            McpWriteValidation.RequireMaxLength(input.PurchaseNotes, 1000, "purchaseNotes");

            if (input.Source.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(input.Source.Value, "source");
            }
            if (input.ColorPattern.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(input.ColorPattern.Value, "colorPattern");
            }
            if (input.FinishType.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(input.FinishType.Value, "finishType");
            }
            if (input.Effects != null)
            {
                foreach (var effect in input.Effects)
                {
                    McpWriteValidation.RequireDefinedEnum(effect, "effects");
                }
            }

            RequireHex(input.ColorHex, "colorHex");
            if (input.Colors != null)
            {
                if (input.Colors.Length > MaxColors)
                {
                    throw McpToolException.InvalidArguments($"At most {MaxColors} colors are allowed.");
                }
                foreach (var color in input.Colors)
                {
                    RequireHex(color, "colors");
                }
            }

            if (input.DensityGramPerCubicCm.HasValue)
            {
                McpWriteValidation.RequirePositiveDensity(input.DensityGramPerCubicCm.Value);
            }
            // Whenever supplied, even for a category that does not track diameter: a stored NaN or -1
            // would silently corrupt every later length conversion on this material.
            if (input.DiameterMm.HasValue)
            {
                RequireFinite(input.DiameterMm.Value, "diameterMm");
                if (input.DiameterMm.Value <= 0)
                {
                    throw McpToolException.InvalidArguments("diameterMm must be greater than zero.");
                }
            }
            if (input.InitialAmount.HasValue)
            {
                McpWriteValidation.RequirePositiveAmount(input.InitialAmount.Value);
            }

            RequireFiniteNonNegative(input.SpoolWeightGrams, "spoolWeightGrams");
            RequireFiniteNonNegative(input.InitialTotalWeightGrams, "initialTotalWeightGrams");
            RequireFiniteNonNegative(input.InitialLayerTimeS, "initialLayerTimeS");
            RequireFiniteNonNegative(input.LayerTimeS, "layerTimeS");

            // Temperatures may legitimately be negative (chamber/bed figures), so these are only
            // checked for finiteness.
            RequireFiniteIfSet(input.TempRangeStartC, "tempRangeStartC");
            RequireFiniteIfSet(input.TempRangeEndC, "tempRangeEndC");
            RequireFiniteIfSet(input.RecommendedTempC, "recommendedTempC");
            RequireFiniteIfSet(input.RecommendedBedTempC, "recommendedBedTempC");
            RequireFiniteIfSet(input.MeltingTemperatureC, "meltingTemperatureC");

            if (input.TempRangeStartC.HasValue && input.TempRangeEndC.HasValue &&
                input.TempRangeStartC.Value > input.TempRangeEndC.Value)
            {
                throw McpToolException.InvalidArguments("tempRangeStartC must not be greater than tempRangeEndC.");
            }

            if (input.MaterialRefreshRatio.HasValue)
            {
                RequireFinite(input.MaterialRefreshRatio.Value, "materialRefreshRatio");
                if (input.MaterialRefreshRatio.Value < 0 || input.MaterialRefreshRatio.Value > 1)
                {
                    throw McpToolException.InvalidArguments("materialRefreshRatio must be between 0.0 and 1.0.");
                }
            }
        }

        private static void RequireHex(string value, string field)
        {
            if (value != null && !HexColor.IsMatch(value))
            {
                throw McpToolException.InvalidArguments($"{field} must be 6 hex digits with no leading '#'.");
            }
        }

        private static void RequireFinite(double value, string field)
        {
            if (!double.IsFinite(value))
            {
                throw McpToolException.InvalidArguments($"{field} must be a finite number.");
            }
        }

        private static void RequireFiniteIfSet(double? value, string field)
        {
            if (value.HasValue)
            {
                RequireFinite(value.Value, field);
            }
        }

        private static void RequireFiniteNonNegative(double? value, string field)
        {
            if (!value.HasValue)
            {
                return;
            }
            RequireFinite(value.Value, field);
            if (value.Value < 0)
            {
                throw McpToolException.InvalidArguments($"{field} must not be negative.");
            }
        }
    }
}
