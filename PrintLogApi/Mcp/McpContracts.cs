using System;
using System.Collections.Generic;

namespace PrintLogApi.Mcp
{
    // Concrete, invariant-unit tool response records. Tools never return anonymous objects,
    // EF entities, API DTOs, or image/comment/file fields. Units: grams, seconds, UTC ISO-8601.

    public sealed record McpPage<T>(
        IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

    public sealed record PrintListItem(
        long Id, string Title, string Status, long? PrinterId, string? PrinterName,
        DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
        // Prints are searchable by project name, so a result must say which project matched —
        // otherwise the hit is uninterpretable.
        Guid? ProjectId, string? ProjectName);

    /// <summary>
    /// One filament-usage row on a print. Every field except Grams is nullable: PrintFilament's
    /// FilamentId is itself nullable, and a spool owned by another user is redacted rather than
    /// dropped — dropping it would break the invariant that the parts sum to MaterialUsedGrams.
    /// </summary>
    public sealed record MaterialUsage(
        Guid? FilamentId, string? Name, string? Brand, string? Material, string? Color,
        double Grams, bool IsEstimated);

    public sealed record PrintDetailResult(
        long Id, string Title, string Status, long? PrinterId, string? PrinterName,
        DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
        decimal? EstimatedCost, string? Notes,
        Guid? ProjectId, string? ProjectName,
        IReadOnlyList<MaterialUsage> MaterialsUsed,
        bool MaterialsUsedTruncated,
        double ReturnedMaterialsUsedGrams);

    public sealed record MaterialInventoryItem(
        Guid Id, string Name, string? Brand, string Material, string? Color,
        double RemainingGrams, bool IsActive,
        string? StorageLocation, double? DiameterMm);

    public sealed record SpoolItem(
        Guid Id, string Name, string? Brand, string Material, string? Color,
        double? DiameterMm, double RemainingGrams, string? StorageLocation);

    /// <summary>
    /// Spools sharing an exact (MaterialType, ColorName) pair. Grouping does NOT establish that the
    /// spools are interchangeable — brand, diameter and pigment lot still vary — it only stops the
    /// tool silently merging PLA with PLA-CF, or Light Blue with Navy, when deciding sufficiency.
    /// </summary>
    public sealed record MaterialGroup(
        string Material,
        string? Color,
        int SpoolCount,
        double TotalGrams,
        double LargestSpoolGrams,
        IReadOnlyList<SpoolItem> Spools,
        bool SpoolsTruncated,
        // Populated only when requiredGrams was supplied.
        bool? SufficientOnLargestSpool,
        bool? MeetsRequirementByCombiningSpools,
        IReadOnlyList<SpoolItem>? CombinationForRequirement);

    public sealed record FindMaterialResult(
        double? RequiredGrams,
        IReadOnlyList<MaterialGroup> Groups,
        bool GroupsTruncated,
        bool CandidatesTruncated);

    public sealed record PrinterStatsItem(
        long PrinterId, string PrinterName, int TotalPrints, int SuccessfulPrints,
        int FailedPrints, double SuccessRatePercent, int TotalPrintTimeSeconds);

    public sealed record ReprintCostResult(
        long PrintId, decimal? EstimatedCost, string Currency, double MaterialGrams, int? DurationSeconds);

    public sealed record PrintSummaryResult(
        DateTimeOffset From, DateTimeOffset To, int TotalPrints, int SuccessfulPrints,
        int FailedPrints, double MaterialUsedGrams, int TotalPrintTimeSeconds);
}
