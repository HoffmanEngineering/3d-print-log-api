namespace PrintLogApi.SpoolTags;

/// <summary>
/// What a decoder found on a spool tag, in one shape for every tag format. Not a DTO: it is the
/// domain shape and is serialized as-is. A null scalar means "not on the tag".
/// </summary>
public class TagReading
{
    public string? Format { get; set; }
    public int DecoderVersion { get; set; }
    public string? Brand { get; set; }
    public string? Manufacturer { get; set; }
    public string? MaterialType { get; set; }
    public string? MaterialSubtype { get; set; }
    public int? MaterialSubtypeCode { get; set; }

    /// <summary>Six-digit uppercase hex, no leading hash.</summary>
    public List<string> Colors { get; set; } = [];

    /// <summary>0-255.</summary>
    public int? Alpha { get; set; }
    public double? DiameterMm { get; set; }
    public int? NetWeightG { get; set; }
    public int? LengthM { get; set; }
    public int? HotendMinC { get; set; }
    public int? HotendMaxC { get; set; }
    public int? FirstLayerHotendC { get; set; }
    public int? HotendC { get; set; }
    public int? BedMinC { get; set; }
    public int? BedMaxC { get; set; }
    public int? DryTempC { get; set; }
    public int? DryHours { get; set; }
    public string? Sku { get; set; }
    public DateOnly? ManufacturedOn { get; set; }
    public Dictionary<string, string> Extras { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
