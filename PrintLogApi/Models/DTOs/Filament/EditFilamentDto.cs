using System.ComponentModel.DataAnnotations;
using PrintLogApi.Enums;
using static PrintLogApi.Models.Filament;

namespace PrintLogApi.Models.DTOs.Filament;

public class EditFilamentDto
{
    [Required]
    public Guid Id { get; set; }

    /// <summary>
    /// Common name for the roll of filament.
    /// </summary>
    [StringLength(255)]
    public string? DisplayName { get; set; }

    [StringLength(255)]
    public string? Brand { get; set; }

    public string? MaterialCategoryNickname { get; set; }


    /// <summary>
    /// The material Type, ie PLA, PETG, ABS.
    /// </summary>
    [StringLength(255)]
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
    public double? TempRangeStart { get; set; }
    public double? TempRangeEnd { get; set; }
    /// <summary>
    /// The user's recommended temperature
    /// </summary>
    public double? RecommendedTemp { get; set; }
    /// <summary>
    /// The user's recommended Bed temperature
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

    public ICollection<FilamentAdjustmentDto>? FilamentAdjustments { get; set; }

    public ColorPatternType? ColorPattern { get; set; }
    public FilamentFinishType? FinishType { get; set; }
    public List<string> Colors { get; set; } = new();
    public List<FilamentEffect> Effects { get; set; } = new();
}
