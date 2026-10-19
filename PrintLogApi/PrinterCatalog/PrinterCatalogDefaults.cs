using PrintLogApi.Models;

namespace PrintLogApi.PrinterCatalog;

/// <summary>
/// Fills the specs a new printer left empty from its catalog model (spec §5.2, §6.6). Used on create
/// only (the REST POST and the MCP create_printer tool): an update never fills, and the backfill only
/// links. A value the caller gave always wins, and null means unknown.
/// </summary>
public static class PrinterCatalogDefaults
{
    /// <summary>
    /// Fills every spec field that is still empty on <paramref name="printer"/> from
    /// <paramref name="model"/>. <see cref="Printer.SlotCount"/> has no "unset" value on the entity,
    /// so <paramref name="requestedSlotCount"/> is what the caller sent: the model's slot count
    /// applies only when it is null (omitted), never over an explicit value, so a 4-slot model
    /// created with 1 slot keeps 1. Make and model are filled when blank, and the category when no
    /// category is named, from the model's technology.
    /// </summary>
    public static void ApplyOnCreate(Printer printer, PrinterModel model, int? requestedSlotCount)
    {
        if (string.IsNullOrWhiteSpace(printer.Make))
        {
            printer.Make = Truncate(model.Make, 50);
        }

        if (string.IsNullOrWhiteSpace(printer.Model))
        {
            printer.Model = Truncate(model.Model, 50);
        }

        printer.BedWidthMm ??= model.BedWidthMm;
        printer.BedDepthMm ??= model.BedDepthMm;
        printer.BedHeightMm ??= model.BedHeightMm;
        printer.NozzleDiameter ??= model.DefaultNozzleMm;
        printer.FilamentDiameter ??= model.FilamentDiameterMm;
        printer.HasHeatedBed ??= model.HasHeatedBed;
        printer.HasHeatedChamber ??= model.HasHeatedChamber;

        if (requestedSlotCount is null && model.DefaultSlotCount is >= 1 and var slots)
        {
            printer.SlotCount = Math.Min(slots, Printer.MaxSlotCount);
        }

        if (printer.CategoryNickname is null && CategoryFor(model.Technology) is { } category)
        {
            printer.CategoryNickname = category;
        }
    }

    /// <summary>
    /// The printer category nickname for a catalog technology, or null when the technology has no
    /// matching category. FFF printers are FFF and resin printers are MSLA.
    /// </summary>
    public static string? CategoryFor(string? technology) => technology?.Trim().ToLowerInvariant() switch
    {
        "fff" => "FFF",
        "resin" => "MSLA",
        _ => null,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
