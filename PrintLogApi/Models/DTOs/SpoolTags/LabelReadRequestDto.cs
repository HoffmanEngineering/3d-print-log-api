using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.SpoolTags;

/// <summary>What the phone read from a spool's label. The photo itself never leaves the phone.</summary>
public class LabelReadRequestDto
{
    /// <summary>Barcodes and QR codes decoded from the label, at most eight. Invalid values are dropped.</summary>
    public List<BarcodeInputDto>? Barcodes { get; set; }

    /// <summary>The OCR text of the label. Only the first 4,000 characters are read.</summary>
    public string? OcrText { get; set; }

    /// <summary>Fields the phone extracted from the OCR text.</summary>
    public LabelFieldsDto? Fields { get; set; }
}

/// <summary>Numbers and the material a phone read off a label. Brand and color are found on the server.</summary>
public class LabelFieldsDto
{
    public string? MaterialType { get; set; }
    public int? WeightG { get; set; }
    public double? DiameterMm { get; set; }
    public int? HotendMinC { get; set; }
    public int? HotendMaxC { get; set; }
    public int? BedMinC { get; set; }
    public int? BedMaxC { get; set; }
}
