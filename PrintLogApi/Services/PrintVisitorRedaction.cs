using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

/// <summary>
/// What a reader who is not a print's creator may see of it: anonymous visitors and other users
/// reading a public or unlisted print through <c>GET /api/Prints/{id}</c> or the public branch of
/// <c>GET /api/Prints/summary</c>.
/// </summary>
/// <remarks>
/// A user makes a print public, not their spool inventory or a private project. Spools have no
/// visibility of their own (every filament endpoint is owner-only), so a print must not carry the
/// owner's notes, price, storage location, remaining amount, current printer and the like out to
/// visitors. The <c>/seo</c> output already follows the same rule (<c>SeoEndpointsTests</c>).
/// </remarks>
public static class PrintVisitorRedaction
{
    /// <summary>
    /// Replaces each row's spool with <see cref="ForVisitor"/>, and clears the row's notes where
    /// it has a spool. A row with no spool keeps its notes: there they are the material's label,
    /// which the public print page shows in place of a spool name.
    /// </summary>
    public static void RedactFilamentUsage(IEnumerable<PrintFilamentSummaryDto>? rows)
    {
        if (rows is null)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (row.Filament is null)
            {
                continue;
            }

            row.Notes = null;
            row.Filament = ForVisitor(row.Filament);
        }
    }

    /// <summary>
    /// The spool as the public print page uses it: what the material is (name, brand, type,
    /// category), its color and swatch, and density and diameter, which convert the usage between
    /// weight, length and volume. Copied field by field into a new instance, so a field added to
    /// <see cref="FilamentSummaryDto"/> later stays with the owner until someone decides otherwise.
    /// Everything about the owner's own spool (notes, price, storage, remaining, loaded printer,
    /// spool number, favorite, active, created date, nominal weight) is left at its default.
    /// </summary>
    public static FilamentSummaryDto ForVisitor(FilamentSummaryDto filament) => new()
    {
        // Kept: the page links it for the owner only, and the public summary already accepts it
        // as a filter. Its default would be a wrong value rather than an absent one.
        Id = filament.Id,
        DisplayName = filament.DisplayName,
        Brand = filament.Brand,
        MaterialType = filament.MaterialType,
        MaterialCategory = filament.MaterialCategory,
        Source = filament.Source,
        MaterialDensityGramPerCubicCm = filament.MaterialDensityGramPerCubicCm,
        DiameterMm = filament.DiameterMm,
        RecommendedTemp = filament.RecommendedTemp,
        ColorName = filament.ColorName,
        ColorHex = filament.ColorHex,
        ColorPattern = filament.ColorPattern,
        FinishType = filament.FinishType,
        Colors = [.. filament.Colors],
        Effects = [.. filament.Effects],
    };

    /// <summary>
    /// Whether a print's project must be left out for <paramref name="viewerId"/>: the project is
    /// one they could not open (<c>GET /api/Projects/{id}</c> refuses them), so neither its name
    /// nor its id, which that refusal would confirm, goes out with the print.
    /// </summary>
    public static bool HidesProject(Project.ProjectViewStatus viewStatus, long projectOwnerId, long? viewerId) =>
        !Project.IsViewableByNonOwner(viewStatus) && projectOwnerId != viewerId;
}
