using System;
using System.Collections.Generic;
using System.Linq;
using PrintLogApi.Enums;

namespace PrintLogApi.Mcp
{
    // Concrete tool input/output records for the write surface. Amounts at the MCP boundary use the
    // source's natural unit: Weight = grams, Length = mm, Volume = ml. Remaining is reported in grams
    // to match the read surface (MaterialInventoryItem.RemainingGrams).

    public enum McpMeasurementSource { Weight = 1, Length = 2, Volume = 3 }

    /// <summary>
    /// One material-consumption row on a print: an actual amount and/or an estimated amount, each
    /// measured by weight, length, or volume. Source and its paired amount are always supplied
    /// together; a row must carry at least one of the two pairs.
    /// </summary>
    public sealed record MaterialUsageInput(
        Guid MaterialId,
        McpMeasurementSource? Source, double? Amount,
        McpMeasurementSource? EstimatedSource, double? EstimatedAmount,
        string Notes);

    public sealed record MaterialRemaining(Guid MaterialId, double RemainingGrams);

    public sealed record CreatePrintResult(
        PrintDetailResult Print, bool WasReplayed, IReadOnlyList<MaterialRemaining> MaterialRemaining);

    /// <summary>
    /// The remaining amount before and after an adjustment, ALWAYS in grams regardless of the unit
    /// the caller expressed the delta in.
    /// <para>
    /// Deliberately not named *InSourceUnit: on the read surface <c>SourceUnit</c> means the
    /// material's authoritative measurement (Weight | Length | Volume) and
    /// <c>InitialAmountInSourceUnit</c> really is in that unit (see MaterialDetail). These values are
    /// not — they are grams whatever 'source' was passed. Reusing that name here made a reader expect
    /// the delta's unit and get a hardcoded "g", so the unit is now in the field name itself and the
    /// constant carrying no information is gone.
    /// </para>
    /// </summary>
    public sealed record MaterialWriteResult(
        Guid MaterialId, double BeforeGrams, double AfterGrams);

    public sealed record ProjectWriteResult(
        Guid ProjectId, string Name, string Status, string ViewStatus);

    public sealed record ProjectListItem(
        Guid Id, string Name, string? Reference, string Status, string ViewStatus);

    /// <summary>
    /// Caller-supplied material attributes for create_material / update_material. Every property is
    /// nullable so update can distinguish "not provided" (leave alone) from a value; clearing is a
    /// separate, explicit channel (the tool's <c>clear</c> list), never a null here.
    /// <para>
    /// Units at the boundary: grams / mm / ml / °C / seconds.
    /// </para>
    /// </summary>
    public sealed record MaterialAttributesInput
    {
        public string DisplayName { get; init; }
        public string MaterialType { get; init; }
        public string MaterialCategoryNickname { get; init; }
        public double? DensityGramPerCubicCm { get; init; }
        public double? DiameterMm { get; init; }
        public McpMeasurementSource? Source { get; init; }
        public double? InitialAmount { get; init; }
        public string Brand { get; init; }
        public string ColorName { get; init; }
        public string ColorHex { get; init; }
        public string[] Colors { get; init; }
        public ColorPatternType? ColorPattern { get; init; }
        public FilamentFinishType? FinishType { get; init; }
        public FilamentEffect[] Effects { get; init; }
        public string StorageLocation { get; init; }
        public bool? IsActive { get; init; }
        public bool? IsFavorite { get; init; }
        public string Notes { get; init; }
        public double? SpoolWeightGrams { get; init; }
        public double? InitialTotalWeightGrams { get; init; }
        public double? TempRangeStartC { get; init; }
        public double? TempRangeEndC { get; init; }
        public double? RecommendedTempC { get; init; }
        public double? RecommendedBedTempC { get; init; }
        public double? InitialLayerTimeS { get; init; }
        public double? LayerTimeS { get; init; }
        public double? MeltingTemperatureC { get; init; }
        public string InertGas { get; init; }
        public double? MaterialRefreshRatio { get; init; }
        public DateTimeOffset? PurchaseDate { get; init; }
        public string PurchaseLocation { get; init; }
        public string PurchasePriceValue { get; init; }
        public string PurchasePriceCurrency { get; init; }
        public string PurchaseNotes { get; init; }

        /// <summary>
        /// Trims every string. Call this ONCE in the service, BEFORE both fingerprinting and
        /// persistence: the fingerprint decides whether two calls are the same request, so anything
        /// normalized away must also be normalized in what is stored, or the hash asserts an
        /// equivalence the database contradicts. Never normalize inside the fingerprint instead.
        /// </summary>
        public MaterialAttributesInput Canonicalize() => this with
        {
            DisplayName = DisplayName?.Trim(),
            MaterialType = MaterialType?.Trim(),
            MaterialCategoryNickname = MaterialCategoryNickname?.Trim(),
            Brand = Brand?.Trim(),
            ColorName = ColorName?.Trim(),
            ColorHex = ColorHex?.Trim(),
            Colors = Colors?.Select(c => c?.Trim()).ToArray(),
            StorageLocation = StorageLocation?.Trim(),
            Notes = Notes?.Trim(),
            InertGas = InertGas?.Trim(),
            PurchaseLocation = PurchaseLocation?.Trim(),
            PurchasePriceValue = PurchasePriceValue?.Trim(),
            PurchasePriceCurrency = PurchasePriceCurrency?.Trim(),
            PurchaseNotes = PurchaseNotes?.Trim(),
        };
    }

    public sealed record CreateMaterialResult(MaterialDetail Material, bool WasReplayed);

    /// <summary>
    /// Every settable printer attribute, all nullable so create and update share one shape: on
    /// create a null means "use the default", on update it means "leave unchanged".
    /// <para>
    /// No loaded-filament member, deliberately. Loaded state is managed only through the load/unload
    /// flow; an MCP write must not be able to express a change to it, so the contract does not carry
    /// the field at all.
    /// </para>
    /// </summary>
    public sealed record PrinterAttributesInput
    {
        public string Make { get; init; }
        public string Model { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public string CategoryNickname { get; init; }
        public double? NozzleDiameterMm { get; init; }
        public double? FilamentDiameterMm { get; init; }
        public double? BeamDiameterMm { get; init; }
        public double? BedWidthMm { get; init; }
        public double? BedDepthMm { get; init; }
        public double? BedHeightMm { get; init; }
        public double? ScreenResolutionXPixels { get; init; }
        public double? ScreenResolutionYPixels { get; init; }
        public bool? HasHeatedBed { get; init; }
        public bool? HasHeatedChamber { get; init; }
        public double? WattageW { get; init; }
        public bool? IsActive { get; init; }

        /// <summary>
        /// Trims every string. Call this ONCE in the service, BEFORE both fingerprinting and
        /// persistence: the fingerprint decides whether two calls are the same request, so anything
        /// normalized away must also be normalized in what is stored, or the hash asserts an
        /// equivalence the database contradicts. Never normalize inside the fingerprint instead.
        /// </summary>
        public PrinterAttributesInput Canonicalize() => this with
        {
            Make = Make?.Trim(),
            Model = Model?.Trim(),
            Name = Name?.Trim(),
            Description = Description?.Trim(),
            CategoryNickname = CategoryNickname?.Trim(),
        };
    }

    public sealed record CreatePrinterResult(PrinterDetailResult Printer, bool WasReplayed);
}
