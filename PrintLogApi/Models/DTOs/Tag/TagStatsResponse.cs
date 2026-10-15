namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>Every one of the caller's tags with its totals, ordered by name.</summary>
public class TagStatsResponse
{
    /// <summary>The caller's currency setting, which every cost is in. Null when unset.</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// True when the tagged prints are too many to cost in one request (the analytics row cap),
    /// in which case every <see cref="TagStatsDto.TotalCost"/> is null. Counts, time and filament
    /// are still complete.
    /// </summary>
    public bool CostUnavailable { get; set; }

    public List<TagStatsDto>? Tags { get; set; }
}
