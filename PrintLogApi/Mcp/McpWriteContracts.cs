using System;
using System.Collections.Generic;

namespace PrintLogApi.Mcp
{
    // Concrete tool input/output records for the write surface. Amounts at the MCP boundary use the
    // source's natural unit: Weight = grams, Length = mm, Volume = ml. Remaining is reported in grams
    // to match the read surface (MaterialInventoryItem.RemainingGrams).

    public enum McpMeasurementSource { Weight = 1, Length = 2, Volume = 3 }

    /// <summary>One material-consumption row on a print: an amount measured by weight, length, or volume.</summary>
    public sealed record MaterialUsageInput(Guid MaterialId, McpMeasurementSource Source, double Amount);

    public sealed record MaterialRemaining(Guid MaterialId, double RemainingGrams);

    public sealed record LogPrintResult(
        long PrintId, bool WasReplayed, IReadOnlyList<MaterialRemaining> MaterialRemaining);

    public sealed record MaterialWriteResult(
        Guid MaterialId, double BeforeInSourceUnit, double AfterInSourceUnit, string SourceUnit);

    public sealed record ProjectWriteResult(
        Guid ProjectId, string Name, string Status, string ViewStatus);

    public sealed record ProjectListItem(
        Guid Id, string Name, string? Reference, string Status, string ViewStatus);
}
