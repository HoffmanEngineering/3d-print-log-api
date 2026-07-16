using System;
using System.Collections.Generic;

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

    public sealed record MaterialWriteResult(
        Guid MaterialId, double BeforeInSourceUnit, double AfterInSourceUnit, string SourceUnit);

    public sealed record ProjectWriteResult(
        Guid ProjectId, string Name, string Status, string ViewStatus);

    public sealed record ProjectListItem(
        Guid Id, string Name, string? Reference, string Status, string ViewStatus);
}
