namespace PrintLogApi.Models.DTOs.Filament;

/// <summary>A barcode stored on a spool.</summary>
public class FilamentBarcodeDto
{
    public long Id { get; set; }

    /// <summary>The normalized value.</summary>
    public string? Value { get; set; }

    /// <summary>The symbology: <c>ean13</c>, <c>ean8</c>, <c>upce</c>, <c>code128</c>, <c>code39</c>, <c>qrcode</c> or <c>other</c>.</summary>
    public string? Format { get; set; }
}
