namespace PrintLogApi.PrinterCatalog;

/// <summary>The <c>Catalog</c> configuration section.</summary>
public sealed class PrinterCatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>
    /// The fewest recent owners a model needs to rank in the popular list by its count (spec §6.5).
    /// Below it, the list is filled in the catalog's featured order.
    /// </summary>
    public int PopularityMinOwners { get; set; } = 5;

    /// <summary>
    /// How far back, in days, an added or printed-on printer still counts toward its model's
    /// owners (spec §6.5).
    /// </summary>
    public int PopularityWindowDays { get; set; } = 365;

    /// <summary>
    /// Requests a minute each client may make to the anonymous catalog endpoints. 0 or less turns
    /// the limit off.
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 600;
}
