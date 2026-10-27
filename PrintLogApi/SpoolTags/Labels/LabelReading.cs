namespace PrintLogApi.SpoolTags.Labels;

/// <summary>
/// What a spool's label says, from on-device OCR plus the brand and color name the server found in
/// its text. Unlike a <see cref="TagReading"/> it carries a color name, never a hex color.
/// </summary>
public sealed record LabelReading(
    string? Brand,
    string? MaterialType,
    string? ColorName,
    int? NetWeightG,
    double? DiameterMm,
    int? HotendMinC,
    int? HotendMaxC,
    int? BedMinC,
    int? BedMaxC);
