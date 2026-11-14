namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>What the add form knows about a label-scanned spool, to find its catalog entry by name.</summary>
public class CatalogMatchRequestDto
{
    public string? Brand { get; set; }
    public string? MaterialType { get; set; }
    public string? ColorName { get; set; }
    public int? NetWeightG { get; set; }
    public double? DiameterMm { get; set; }
}
