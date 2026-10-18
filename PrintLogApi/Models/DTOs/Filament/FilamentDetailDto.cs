using System.ComponentModel.DataAnnotations;
using PrintLogApi.Enums;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.Services;
using static PrintLogApi.Models.Filament;

namespace PrintLogApi.Models.DTOs.Filament;

public class FilamentDetailDto
{
    [Required]
    public Guid Id { get; set; }

    /// <summary>
    /// The user's own number for this spool (1, 2, 3…), which Spoolman clients use as its id.
    /// Read-only: assigned on create, never reused, and ignored on PUT.
    /// </summary>
    public int? SpoolNumber { get; set; }

    /// <summary>
    /// Common name for the roll of filament.
    /// </summary>
    [StringLength(255)]
    [Required(AllowEmptyStrings = false)]
    public string? DisplayName { get; set; }

    [StringLength(255)]
    public string? Brand { get; set; }

    public string? MaterialCategoryNickname { get; set; }


    /// <summary>
    /// The material Type, ie PLA, PETG, ABS.
    /// </summary>
    [StringLength(255)]
    [Required(AllowEmptyStrings = false)]
    public string? MaterialType { get; set; }

    /// <summary>
    /// The Density of the Material
    /// </summary>
    public double MaterialDensityGramPerCubicCm { get; set; }

    [StringLength(255)]
    public string? ColorName { get; set; }
    [StringLength(6)]
    public string? ColorHex { get; set; }
    public double? DiameterMm { get; set; }

    /// <summary>
    /// Which measurement is the source. Ie, should measurements be based on weight, volume, etc?
    /// </summary>
    public SourceMeasurement? Source { get; set; }

    /// <summary>
    /// The initial volume of the material in millileters 
    /// </summary>
    public double? InitialNominalVolumeMl { get; set; }

    /// <summary>
    /// The initial length of the material in meters 
    /// </summary>
    public double? InitialNominalLengthM { get; set; }


    public long? InitialTotalWeightMg { get; set; }

    /// <summary>
    /// The initial nominal weight of the filament in milligrams.
    /// </summary>
    /// <example>1kg roll would have an InitialNomialWeightMg of 1,000,000</example>
    public long? InitialNominalWeightMg { get; set; }
    /// <summary>
    /// The weight of the spool in milligrams.
    /// </summary>
    public long? SpoolWeightMg { get; set; }

    /// <summary>
    /// Server-computed remaining filament weight in milligrams
    /// (InitialNominalWeightMg minus print usage plus weight adjustments).
    /// Null when nominal weight is not set. Read-only: ignored on write.
    /// </summary>
    public long? FilamentRemaining { get; set; }

    /// <summary>
    /// Server-computed remaining length in meters, converted from FilamentRemaining so the
    /// three readouts always describe the same spool. Null when the nominal weight is not
    /// tracked, and null for materials with no diameter - resin and powder have no length.
    /// Get-only: a client-supplied value cannot reach the server.
    /// </summary>
    public double? FilamentLengthRemainingInM =>
        MeasurementUtilities.GetLengthRemainingInM(FilamentRemaining, DiameterMm, MaterialDensityGramPerCubicCm);

    /// <summary>
    /// Server-computed remaining volume in milliliters, converted from FilamentRemaining.
    /// Deliberately NOT accumulated from the per-usage VolumeMl columns: a usage row written
    /// without normalization carries milligrams and no volume, and summing the volume column
    /// then silently under-counts the spool. Get-only: a client-supplied value cannot reach
    /// the server.
    /// </summary>
    public double? FilamentVolumeRemainingInMl =>
        MeasurementUtilities.GetVolumeRemainingInMl(FilamentRemaining, MaterialDensityGramPerCubicCm);

    /// <summary>
    /// Distinct prints that used this filament. Read-only: ignored on write.
    /// </summary>
    public int PrintCount { get; set; }

    /// <summary>
    /// Total milligrams consumed by prints, actual where recorded and estimated
    /// otherwise - the same rule FilamentRemaining subtracts. Read-only: ignored on write.
    /// </summary>
    public long TotalUsedMg { get; set; }
    public double? TempRangeStart { get; set; }
    public double? TempRangeEnd { get; set; }
    /// <summary>
    /// The user's recommended temperature
    /// </summary>
    public double? RecommendedTemp { get; set; }
    /// <summary>
    /// The user's recommended bed temperature
    /// </summary>
    public double? RecommendedBedTemp { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? PurchaseDate { get; set; }

    /// <summary>
    /// The location (either URL, or physical location where the filament was purchased)
    /// </summary>
    [StringLength(1000)]
    public string? PurchaseLocation { get; set; }
    [StringLength(256)]
    public string? PurchasePriceValue { get; set; }
    /// <summary>
    /// The Currency Marker (ie, USD)
    /// </summary>
    [StringLength(256)]
    public string? PurchasePriceCurrency { get; set; }
    /// <summary>
    /// Any notes about the purchase price
    /// </summary>
    [StringLength(1000)]
    public string? PurchaseNotes { get; set; }
    /// <summary>
    /// Where is this filament stored?
    /// </summary>
    [StringLength(256)]
    public string? StorageLocation { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public bool IsFavorite { get; set; }

    /// <summary>
    /// The initial layer cure time in seconds
    /// </summary>
    public double? InitialLayerTimeS { get; set; }

    /// <summary>
    /// The layer cure time in seconds (after the initial layers)
    /// </summary>
    public double? LayerTimeS { get; set; }

    /// <summary>
    /// Melting temperature of the material
    /// </summary>
    public double? MeltingTemperature { get; set; }


    /// <summary>
    /// What inert gas should be used?
    /// </summary>
    [StringLength(255)]
    public string? InertGas { get; set; }

    /// <summary>
    /// The percentage of new powder when mixing with old powder.
    /// 1.0 means always use new powder.
    /// </summary>
    [Range(0.0, 1.0)]
    public double? MaterialRefreshRatio { get; set; }

    /// <summary>
    /// Amounts added to (positive) or removed from (negative) the spool outside of prints.
    /// Always a list in responses.
    /// On PUT, the list replaces the spool's adjustments, and an empty list removes them all;
    /// leave it out (or null) to keep them, which is what clients that never send adjustments do.
    /// </summary>
    public ICollection<FilamentAdjustmentDto>? FilamentAdjustments { get; set; }

    public ColorPatternType ColorPattern { get; set; } = ColorPatternType.Solid;
    public FilamentFinishType FinishType { get; set; } = FilamentFinishType.Standard;
    public List<string> Colors { get; set; } = new();
    public List<FilamentEffect> Effects { get; set; } = new();

    /// <summary>
    /// RFID or NFC tag UIDs on this spool, as uppercase hex without separators (<c>04A23F11</c>),
    /// in order. Always a list in responses.
    /// On PUT, any case and separators are accepted and the list replaces the spool's tags;
    /// leave it out (or null) to keep them, which is what clients that predate tags do. A UID
    /// already on another of the user's spools is a 409 naming that spool.
    /// </summary>
    public List<string>? TagUids { get; set; }

    /// <summary>
    /// Decoded tag readings to keep with the spool's tags, matched to <c>TagUids</c> by UID. A
    /// reading for a UID that is not in <c>TagUids</c> is ignored. When a spool is updated, a
    /// tag's stored reading is replaced only when a new one is sent for it.
    /// </summary>
    public List<TagReadingInputDto>? TagReadings { get; set; }

    /// <summary>
    /// Response-only: the spool's tags with their last decoded reading, ordered by UID. Inbound
    /// it is ignored. A reading whose stored JSON cannot be read comes back null.
    /// </summary>
    public List<FilamentTagDetailDto>? TagDetails { get; set; }

    /// <summary>The catalog entry the spool was created from. Response-only: a PUT never changes it.</summary>
    public string? CatalogEntryId { get; set; }


    /// <summary>
    /// Response-only, populated by <c>FilamentService.HydrateDetailImageUrlsAsync</c>.
    /// This DTO doubles as the PUT request body, so a caller can send anything here —
    /// nothing reads it inbound. <c>Filament</c> has no <c>Images</c> navigation, so
    /// AutoMapper has nowhere to map it to; if one is ever added, this needs an
    /// explicit <c>Ignore()</c> on the <c>FilamentDetailDto -> Filament</c> map or it
    /// becomes an injection path.
    /// </summary>
    public IList<FilamentImageDto> Images { get; set; } = new List<FilamentImageDto>();
}
