using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// A print's slicer settings as a connector has them (#164): the settings block the slicer wrote
/// into the G-code, Moonraker's metadata summary, or both.
/// </summary>
/// <remarks>
/// The size caps are checked by the action rather than by <c>[StringLength]</c>, so an oversized
/// block is a 413 rather than a 400. Both are in UTF-8 bytes.
/// </remarks>
public class PrintSlicerSettingsDto : IValidatableObject
{
    /// <summary>Largest settings block accepted, in UTF-8 bytes. Real blocks are tens of kilobytes.</summary>
    public const int MaxConfigBlockBytes = 512 * 1024;

    /// <summary>Largest header accepted, in UTF-8 bytes. Real headers are a few hundred bytes.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>
    /// The settings block the slicer wrote into the G-code, as text: the <c>; key = value</c>
    /// comment lines. OrcaSlicer, Anycubic Slicer Next and PrusaSlicer write it at the end of the
    /// file (Orca: from <c>; EXECUTABLE_BLOCK_END</c>; PrusaSlicer: from the <c>; filament used</c>
    /// totals through <c>; prusaslicer_config = end</c>). Bambu Studio and FLSun Slicer write it
    /// near the top, after <c>; HEADER_BLOCK_END</c> through <c>; CONFIG_BLOCK_END</c>. Toolpaths
    /// may be included but are never needed. At most 512 KiB.
    /// </summary>
    public string? ConfigBlock { get; set; }

    /// <summary>
    /// The start of the file through <c>; HEADER_BLOCK_END</c> (PrusaSlicer: its first line). It
    /// names the slicer and its version; without it the version is unknown and the slicer is a
    /// best guess from the settings. Anycubic Slicer Next can only be told apart from OrcaSlicer by
    /// its header. At most 64 KiB.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>
    /// The summary Moonraker extracts from the file. Used on its own when there is no settings
    /// block, or when the block holds no settings; with a block it only fills in a missing version.
    /// </summary>
    public SlicerMetadataDto? Metadata { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(ConfigBlock) && Metadata is null)
        {
            yield return new ValidationResult(
                "Send configBlock, metadata, or both.",
                [nameof(ConfigBlock), nameof(Metadata)]);
        }
    }
}

/// <summary>
/// The slicer facts Moonraker's file metadata carries. Every field is optional except
/// <see cref="Slicer"/> when no settings block is sent.
/// </summary>
public class SlicerMetadataDto
{
    /// <summary>The slicer's name as the file reports it, e.g. <c>OrcaSlicer</c>, <c>PrusaSlicer</c>.</summary>
    [StringLength(50)]
    public string? Slicer { get; set; }

    /// <summary>The slicer's version, e.g. <c>2.3.1</c>.</summary>
    [StringLength(50)]
    public string? SlicerVersion { get; set; }

    /// <summary>Layer height in millimetres.</summary>
    [Range(0, 100)]
    public double? LayerHeightMm { get; set; }

    /// <summary>First layer height in millimetres.</summary>
    [Range(0, 100)]
    public double? FirstLayerHeightMm { get; set; }

    /// <summary>Nozzle diameter in millimetres.</summary>
    [Range(0, 100)]
    public double? NozzleDiameterMm { get; set; }

    /// <summary>The filament profile's name.</summary>
    [StringLength(200)]
    public string? FilamentName { get; set; }

    /// <summary>The filament's material type, e.g. <c>PLA</c>.</summary>
    [StringLength(100)]
    public string? FilamentType { get; set; }

    /// <summary>The first layer's nozzle temperature in °C.</summary>
    [Range(-100, 1000)]
    public double? FirstLayerNozzleTemperatureC { get; set; }

    /// <summary>The first layer's bed temperature in °C.</summary>
    [Range(-100, 1000)]
    public double? FirstLayerBedTemperatureC { get; set; }
}

/// <summary>What recording a print's slicer settings did.</summary>
public class PrintSlicerSettingsResultDto
{
    public long PrintId { get; set; }

    /// <summary>
    /// The print's slicer now: <c>cura</c>, <c>prusaslicer</c>, <c>orcaslicer</c>,
    /// <c>bambustudio</c>, <c>anycubic</c>, <c>flsun</c> or <c>other</c>.
    /// </summary>
    public string? Slicer { get; set; }

    /// <summary>The slicer's version, or null when neither the header nor the metadata gave one.</summary>
    public string? SlicerVersion { get; set; }

    /// <summary>
    /// What the settings came from: <c>configBlock</c>, or <c>metadata</c> when there was no block
    /// or it held no settings.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// For a settings block: true when the header named the slicer, false when it was guessed from
    /// the settings. Null for metadata.
    /// </summary>
    public bool? IsRecognized { get; set; }

    /// <summary>How many settings were stored, after credential and host settings were removed.</summary>
    public int SettingsCount { get; set; }

    /// <summary>True when this replaced settings an earlier call recorded.</summary>
    public bool Replaced { get; set; }

    /// <summary>
    /// True when the settings note is in the print's notes. False only when adding it would take
    /// the notes past the 50,000 characters the print form accepts; the settings are stored anyway.
    /// </summary>
    public bool NoteAppended { get; set; }

    /// <summary>The rendered settings note.</summary>
    public string? Note { get; set; }
}
