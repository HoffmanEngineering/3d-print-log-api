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
        // Provenance. A number with no provenance forces an assistant to state an estimate as
        // measured fact. DurationSeconds is null when NOTHING was recorded — never 0, which would
        // claim a measurement of zero seconds.
        bool DurationIsEstimated, bool MaterialIsEstimated,
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
        // Grams stays the RESOLVED figure (actual if recorded, else estimate); IsEstimated says which.
        double Grams, bool IsEstimated,
        double? ActualGrams, double? EstimatedGrams, string? Notes);

    public sealed record PrintDetailResult(
        long Id, string Title, string Status, long? PrinterId, string? PrinterName,
        DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
        // MaterialsUsed[] already carries a per-row IsEstimated; these lift the same idea to the
        // print level. DurationSeconds is null when nothing was recorded, never 0.
        bool DurationIsEstimated, bool MaterialIsEstimated,
        decimal? EstimatedCost, string? Notes,
        Guid? ProjectId, string? ProjectName,
        IReadOnlyList<MaterialUsage> MaterialsUsed,
        bool MaterialsUsedTruncated,
        double ReturnedMaterialsUsedGrams,
        string? FileName, string? Url, string ViewStatus,
        int? EstimatedDurationSeconds, bool AllowComments, bool AllowFileDownloads);

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
        // Null also means INDETERMINATE: when CandidatesTruncated dropped spools from this group,
        // a "no" cannot be asserted, because the omitted spools might have closed the gap. A "yes"
        // is always trustworthy (a qualifying subset was actually found).
        bool? MeetsRequirementByCombiningSpools,
        IReadOnlyList<SpoolItem>? CombinationForRequirement);

    public sealed record FindMaterialResult(
        double? RequiredGrams,
        IReadOnlyList<MaterialGroup> Groups,
        bool GroupsTruncated,
        bool CandidatesTruncated);

    /// <summary>
    /// Duration provenance only: this record carries no material figure, so a material-provenance
    /// count here would qualify a number that does not exist.
    /// </summary>
    public sealed record PrinterStatsItem(
        long PrinterId, string PrinterName, int TotalPrints, int SuccessfulPrints,
        int FailedPrints, double SuccessRatePercent, int TotalPrintTimeSeconds,
        int PrintsWithEstimatedDuration);

    public sealed record PrinterListItem(
        long Id, string Name, string? Make, string? Model,
        double? NozzleDiameterMm, bool IsActive);

    /// <summary>A spool currently mounted on a printer (never one that has been unloaded).</summary>
    public sealed record LoadedFilament(
        Guid FilamentId, string? Name, string? Brand, string? Material, string? Color,
        double? DiameterMm, double RemainingGrams, DateTimeOffset LoadedAt);

    public sealed record PrinterDetailResult(
        long Id, string Name, string? Make, string? Model, string? Description,
        string? CategoryNickname,
        double? NozzleDiameterMm,
        double? BedWidthMm, double? BedDepthMm, double? BedHeightMm,
        bool? HasHeatedBed, bool? HasHeatedChamber, double? WattageW,
        bool IsActive,
        IReadOnlyList<LoadedFilament> LoadedFilaments,
        int LoadedFilamentCount,          // true count before capping
        bool LoadedFilamentsTruncated,    // silently omitting a loaded spool is a WRONG answer
        int ExcludedUnreadableSpools);    // corrupt rows pointing at another user's spool

    public sealed record SummaryMetrics(
        int Prints, double MaterialUsedGrams, int TotalPrintTimeSeconds,
        // A summary spans many prints, so a single bool would be meaningless. These counts let an
        // agent say "about 40 hours, but 9 of the 13 are estimates" instead of passing off a blended
        // figure as measured.
        int PrintsWithEstimatedDuration,
        int PrintsWithEstimatedMaterial);

    /// <summary>
    /// Nested on purpose. The status filter and the status breakdown describe DIFFERENT populations:
    /// Filtered is scoped by the status filter, UnfilteredStatusCounts is not. Sitting them side by
    /// side as flat fields invites an agent to compare a filtered scalar against an unfiltered map.
    /// </summary>
    public sealed record PrintSummaryResult(
        DateTimeOffset? From,   // null = all-time
        DateTimeOffset? To,
        string? AppliedStatusFilter,
        SummaryMetrics Filtered,
        IReadOnlyDictionary<string, int> UnfilteredStatusCounts,
        // Prints with no start date. They are included in all-time totals but can never appear in a
        // date range, so without this block all-time != sum(ranges) with no way to reconcile.
        SummaryMetrics Undated);
}
