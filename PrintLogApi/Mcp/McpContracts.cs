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
        DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds);

    public sealed record PrintDetailResult(
        long Id, string Title, string Status, long? PrinterId, string? PrinterName,
        DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
        decimal? EstimatedCost, string? Notes, string? ProjectName);

    public sealed record MaterialInventoryItem(
        Guid Id, string Name, string? Brand, string Material, string? Color,
        double RemainingGrams, bool IsActive);

    public sealed record MaterialSufficiencyResult(
        double RequiredGrams, double AvailableGrams, bool Sufficient, string? Material, string? Color);

    public sealed record PrinterStatsItem(
        long PrinterId, string PrinterName, int TotalPrints, int SuccessfulPrints,
        int FailedPrints, double SuccessRatePercent, int TotalPrintTimeSeconds);

    public sealed record ReprintCostResult(
        long PrintId, decimal? EstimatedCost, string Currency, double MaterialGrams, int? DurationSeconds);

    public sealed record PrintSummaryResult(
        DateTimeOffset From, DateTimeOffset To, int TotalPrints, int SuccessfulPrints,
        int FailedPrints, double MaterialUsedGrams, int TotalPrintTimeSeconds);
}
