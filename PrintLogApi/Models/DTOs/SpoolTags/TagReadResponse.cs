using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.SpoolTags;

namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>What a tag read resolved to: the user's linked spool, or a reading with matches for an unlinked tag.</summary>
public class TagReadResponse
{
    /// <summary>"linked" when the user's spool carries the tag, otherwise "unknown".</summary>
    public string? Status { get; set; }

    /// <summary>The spool carrying the tag. Set only when <see cref="Status"/> is "linked".</summary>
    public FilamentDetailDto? Filament { get; set; }

    /// <summary>What the tag says. Null for a linked tag, a blank or unreadable tag, or a format nobody decodes yet.</summary>
    public TagReading? Reading { get; set; }

    /// <summary>The catalog entry that best fits the reading, if any is close enough.</summary>
    public CatalogMatchDto? CatalogMatch { get; set; }

    /// <summary>The user's own materials that best fit the reading.</summary>
    public List<TagSuggestionDto>? Suggestions { get; set; }

    /// <summary>"hold-still" when the read was incomplete and worth repeating, otherwise null.</summary>
    public string? Hint { get; set; }
}

public class CatalogMatchDto
{
    public string? Id { get; set; }
    public double Confidence { get; set; }
    public int? SpoolWeightG { get; set; }
    public string? ColorName { get; set; }
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }

    /// <summary>The product's density in g/cm³, from SpoolmanDB.</summary>
    public double? DensityGramPerCubicCm { get; set; }

    /// <summary>Always "SpoolmanDB".</summary>
    public string? Source { get; set; }

    /// <summary>The product's nozzle temperature range and recommended nozzle and bed temperatures, °C, when SpoolmanDB has them.</summary>
    public int? ExtruderTempMinC { get; set; }
    public int? ExtruderTempMaxC { get; set; }
    public int? ExtruderTempC { get; set; }
    public int? BedTempC { get; set; }

    /// <summary>The product's material, net weight (g) and diameter (mm), for a form a barcode alone filled.</summary>
    public string? Material { get; set; }
    public int? WeightG { get; set; }
    public double? DiameterMm { get; set; }

    public static CatalogMatchDto? From(PrintLogApi.SpoolTags.Matching.CatalogMatch? match) => match is null ? null : new()
    {
        Id = match.Id,
        Confidence = match.Confidence,
        SpoolWeightG = match.SpoolWeightG,
        ColorName = match.ColorName,
        Name = match.Name,
        Manufacturer = match.Manufacturer,
        DensityGramPerCubicCm = match.Density,
        Source = "SpoolmanDB",
        ExtruderTempMinC = match.ExtruderTempMinC,
        ExtruderTempMaxC = match.ExtruderTempMaxC,
        ExtruderTempC = match.ExtruderTempC,
        BedTempC = match.BedTempC,
        Material = match.Material,
        WeightG = match.WeightG,
        DiameterMm = match.DiameterMm,
    };
}

public class TagSuggestionDto
{
    public Guid FilamentId { get; set; }
    public double Score { get; set; }
    public List<string>? Reasons { get; set; }
}
