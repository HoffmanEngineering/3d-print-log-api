using PrintLogApi.SpoolTags.Labels;

namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>A label read, resolved: what it says, the user's spools it may be, and the catalog entry it may be.</summary>
public sealed record LabelReadResponse(
    LabelReading Reading,
    CatalogMatchDto? CatalogMatch,
    IReadOnlyList<LabelOwnMatch> OwnMatches,
    IReadOnlyList<LabelBarcode> Barcodes);

/// <summary>
/// One of the user's spools the label may be. Reasons: <c>same-barcode</c>, <c>same-color-name</c>,
/// <c>same-brand</c>, <c>same-material</c>, <c>same-weight</c>.
/// </summary>
public sealed record LabelOwnMatch(Guid FilamentId, double Score, IReadOnlyList<string> Reasons);

/// <summary>A barcode from the request, normalized as it would be stored.</summary>
public sealed record LabelBarcode(string Value, string Format);
