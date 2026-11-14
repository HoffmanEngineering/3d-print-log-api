namespace PrintLogApi.Models.DTOs.Filament;

/// <summary>A barcode read from a spool's label or box, as the scanner reported it.</summary>
public class BarcodeInputDto
{
    /// <summary>The decoded value. Normalized on the server (trimmed, uppercased, UPC-A padded to EAN-13).</summary>
    public string? Value { get; set; }

    /// <summary>The symbology the scanner reported (<c>EAN_13</c>, <c>CODE_128</c>…). Optional.</summary>
    public string? Format { get; set; }
}
