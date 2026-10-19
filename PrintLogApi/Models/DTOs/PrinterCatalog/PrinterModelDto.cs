using System.Text.Json;
using PrintLogApi.PrinterCatalog.Generation;

namespace PrintLogApi.Models.DTOs.PrinterCatalog;

/// <summary>
/// One printer model from the shared catalog. Search, popular and get all return this shape.
/// Popularity counts never leave the API.
/// </summary>
public class PrinterModelDto
{
    public string? Id { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }

    /// <summary>Other names the model is sold or sliced under.</summary>
    public string[]? Aliases { get; set; }

    public string? Technology { get; set; }
    public double? BedWidthMm { get; set; }
    public double? BedDepthMm { get; set; }
    public double? BedHeightMm { get; set; }

    /// <summary>Nozzle diameters in millimeters.</summary>
    public double[]? NozzleOptionsMm { get; set; }

    public double? DefaultNozzleMm { get; set; }
    public double? FilamentDiameterMm { get; set; }
    public bool? HasHeatedBed { get; set; }
    public bool? HasHeatedChamber { get; set; }
    public int? ExtruderCount { get; set; }
    public int? DefaultSlotCount { get; set; }
    public string? Kinematics { get; set; }

    /// <summary>True for a model no longer offered. It still resolves by id.</summary>
    public bool? Retired { get; set; }

    public static PrinterModelDto From(PrinterModel model) => new()
    {
        Id = model.Id,
        Make = model.Make,
        Model = model.Model,
        Aliases = ParseArray<string>(model.AliasesJson),
        Technology = model.Technology,
        BedWidthMm = model.BedWidthMm,
        BedDepthMm = model.BedDepthMm,
        BedHeightMm = model.BedHeightMm,
        NozzleOptionsMm = ParseArray<double>(model.NozzleOptionsJson),
        DefaultNozzleMm = model.DefaultNozzleMm,
        FilamentDiameterMm = model.FilamentDiameterMm,
        HasHeatedBed = model.HasHeatedBed,
        HasHeatedChamber = model.HasHeatedChamber,
        ExtruderCount = model.ExtruderCount,
        DefaultSlotCount = model.DefaultSlotCount,
        Kinematics = model.Kinematics,
        Retired = model.Retired,
    };

    private static T[] ParseArray<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<T[]>(json) ?? [];
}

/// <summary>A slicer printer name resolved to one catalog model.</summary>
public class PrinterModelMatchDto
{
    public string? Id { get; set; }

    /// <summary><c>exact</c>, <c>alias</c> or <c>fuzzy</c>.</summary>
    public string? Confidence { get; set; }
}

/// <summary>Where the catalog facts came from.</summary>
public class PrinterCatalogAttributionDto
{
    public string? Attribution { get; set; }
    public IReadOnlyList<CatalogSource>? Sources { get; set; }
}
