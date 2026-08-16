using System.Collections.Generic;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Input-bound validation for the printer write surface, shared by create_printer and
    /// update_printer so the two can never drift apart. Every rule is expressed against
    /// <see cref="PrinterAttributesInput"/> alone; anything needing the database (category
    /// existence, ownership) lives in PrinterService.
    /// <para>
    /// Printer numerics are stored exactly as entered (mm / W / px) with no unit conversion, so
    /// unlike the material surface there is no rounding or overflow class of bug to guard — finite
    /// and non-negative is the whole rule.
    /// </para>
    /// </summary>
    public static class McpPrinterValidation
    {
        /// <summary>
        /// The printer fields update_printer will clear on request. Excluded: make, model and name
        /// (identity — a printer with no make is not a state MCP will create), isActive (a
        /// non-nullable bool with no "cleared" value), and categoryNickname (see the category
        /// resolution rules in PrinterService).
        /// <para>
        /// This lives here, not on the tool, so the SERVICE enforces it: a tool wrapper is one caller
        /// among several (tests call the service directly), and an allow-list checked only at the
        /// wrapper is not an invariant, just a habit.
        /// </para>
        /// </summary>
        public static readonly IReadOnlySet<string> ClearableFields = new HashSet<string>
        {
            "description", "nozzleDiameterMm", "filamentDiameterMm", "beamDiameterMm",
            "bedWidthMm", "bedDepthMm", "bedHeightMm",
            "screenResolutionXPixels", "screenResolutionYPixels",
            "hasHeatedBed", "hasHeatedChamber", "wattageW",
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

        public static void ValidateAttributes(PrinterAttributesInput input)
        {
            RequireNonBlankIfSet(input.Make, "make");
            RequireNonBlankIfSet(input.Model, "model");
            RequireNonBlankIfSet(input.Name, "name");

            McpWriteValidation.RequireMaxLength(input.Make, 50, "make");
            McpWriteValidation.RequireMaxLength(input.Model, 50, "model");
            McpWriteValidation.RequireMaxLength(input.Name, 100, "name");
            McpWriteValidation.RequireMaxLength(input.Description, 1000, "description");
            McpWriteValidation.RequireMaxLength(input.CategoryNickname, 50, "categoryNickname");

            RequireFiniteNonNegative(input.NozzleDiameterMm, "nozzleDiameterMm");
            RequireFiniteNonNegative(input.FilamentDiameterMm, "filamentDiameterMm");
            RequireFiniteNonNegative(input.BeamDiameterMm, "beamDiameterMm");
            RequireFiniteNonNegative(input.BedWidthMm, "bedWidthMm");
            RequireFiniteNonNegative(input.BedDepthMm, "bedDepthMm");
            RequireFiniteNonNegative(input.BedHeightMm, "bedHeightMm");
            RequireFiniteNonNegative(input.ScreenResolutionXPixels, "screenResolutionXPixels");
            RequireFiniteNonNegative(input.ScreenResolutionYPixels, "screenResolutionYPixels");
            RequireFiniteNonNegative(input.WattageW, "wattageW");
        }

        /// <summary>
        /// Only checks a value the caller actually supplied: on update, a null make means "leave it
        /// alone", which is not the same as asking for a blank one.
        /// </summary>
        private static void RequireNonBlankIfSet(string? value, string field)
        {
            if (value != null && string.IsNullOrWhiteSpace(value))
            {
                throw McpToolException.InvalidArguments($"{field} cannot be blank.");
            }
        }

        private static void RequireFiniteNonNegative(double? value, string field)
        {
            if (!value.HasValue)
            {
                return;
            }
            if (!double.IsFinite(value.Value))
            {
                throw McpToolException.InvalidArguments($"{field} must be a finite number.");
            }
            if (value.Value < 0)
            {
                throw McpToolException.InvalidArguments($"{field} must not be negative.");
            }
        }
    }
}
