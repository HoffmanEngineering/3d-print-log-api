using System.ComponentModel;
using System.Text.Json.Serialization;
using PrintLogApi.Models;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services;

/// <summary>What selects and orders the grouped feed: everything but the page.</summary>
public sealed record GroupedFeedQuery(
    long UserId,
    string? SearchText,
    IReadOnlyList<long>? PrinterIds,
    IReadOnlyList<Guid>? FilamentIds,
    Print.PrintStatus? Status,
    PrintSummarySortColumn? SortColumn,
    SortDirection? SortDirection);

/// <summary>
/// The grouped feed's rows in order, for one <see cref="GroupedFeedQuery"/> (#309). Cached on the
/// user's cache version so every page of a query reads one entry; see
/// <see cref="PrintService.LoadGroupedFeedOrderAsync"/>.
///
/// <para><c>[ImmutableObject(true)]</c> lets HybridCache hand the stored instance to each caller
/// instead of deserializing a copy per hit, so treat it as read-only.</para>
/// </summary>
[ImmutableObject(true)]
public sealed class GroupedFeedOrder
{
    public List<GroupedFeedEntry> Entries { get; init; } = [];

    /// <summary>Whether a search or filter selected the rows, which is when projects carry <see cref="GroupedFeedEntry.FilteredPrintCount"/>.</summary>
    public bool HasFilters { get; init; }
}

/// <summary>
/// One row of a <see cref="GroupedFeedOrder"/>: a standalone print (<see cref="PrintId"/> only) or
/// a project (everything else). The short names and the omitted nulls keep the serialized entry,
/// which is what the cache budget charges, to a few bytes per print.
/// </summary>
public sealed class GroupedFeedEntry
{
    [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PrintId { get; init; }

    [JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProjectId { get; init; }

    /// <summary>The project's resolved start, the instant it sorts on.</summary>
    [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ProjectSortDate { get; init; }

    /// <summary>The project's prints that matched the filters; null when none were applied.</summary>
    [JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FilteredPrintCount { get; init; }

    /// <summary>Material across all of the project's prints, filters or not.</summary>
    [JsonPropertyName("w"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ProjectWeightMg { get; init; }

    /// <summary>Resin across all of the project's prints (#294).</summary>
    [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ProjectVolumeMl { get; init; }
}
