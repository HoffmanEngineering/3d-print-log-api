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
}
