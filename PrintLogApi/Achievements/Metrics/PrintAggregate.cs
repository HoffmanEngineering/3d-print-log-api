using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Achievements.Metrics;

/// <summary>One print as every print-based metric needs it. Read once per pass.</summary>
public sealed record PrintRow(
    long Id,
    DateTimeOffset? StartDate,
    DateTime CreatedDate,
    PrintStatus Status,
    PrintViewStatus ViewStatus,
    PrintSource Source,
    string? Slicer,
    int? PrintTimeInSeconds,
    int? EstimatedPrintTimeInSeconds,
    int? FilamentUsageMg,
    int? EstimatedFilamentUsageMg);

/// <summary>
/// Every print-based count, sum and max for one user.
/// </summary>
/// <param name="SuccessMaterialMg">
/// Canonical material (<see cref="PrintMetrics.MaterialMgExpr"/>) over Success and PartialSuccess
/// prints: per-filament rows, each falling back to its own estimate, plus the scalar "other
/// filament".
/// </param>
/// <param name="PluginSlicers">Distinct normalized slicers on <see cref="PrintSource.SlicerPlugin"/> prints.</param>
public sealed record PrintAggregate(
    long PrintCount,
    long SuccessSeconds,
    long SuccessMaterialMg,
    long LongestSuccessSeconds,
    long WithImage,
    long Public,
    long Failed,
    long SlicerPlugin,
    long OctoPrint,
    long Moonraker,
    long Mcp,
    IReadOnlyList<string> PluginSlicers,
    long MaxDistinctMaterials,
    long DistinctMaterialTypes)
{
    /// <summary>
    /// Computes the aggregate in memory from the pass's print rows, plus two more commands: the
    /// user's filament usage rows (one join) and the count of their prints with a photo.
    /// <para>
    /// Not a single SQL aggregate, and that was measured. SQL Server rejects an aggregate over a
    /// subquery (error 130) or an outer reference (8124), so a one-statement version needs a
    /// scalar subquery per figure, and over a 10,000-print user that cost ~80 ms: nineteen range
    /// scans of the same rows plus three hash joins. The print rows are loaded for the date metrics
    /// anyway, so summing them here is nearly free.
    /// </para>
    /// </summary>
    internal static async Task<PrintAggregate> LoadAsync(PrintLogContext db, long userId, IReadOnlyList<PrintRow> prints, CancellationToken ct)
    {
        var usage = await db.PrintFilament
            .AsNoTracking()
            .Where(pf => pf.Print.CreatedById == userId)
            .Select(pf => new
            {
                pf.PrintId,
                pf.FilamentId,
                pf.AmountMg,
                pf.EstimatedAmountMg,
                pf.Print.Status,
                MaterialType = pf.Filament != null ? pf.Filament.MaterialType : null,
            })
            .ToListAsync(ct);

        // Only a print's owner can attach photos, so the creator index finds them without a
        // per-print lookup (the PrintId index is filtered to default images).
        var withImage = await db.PrintImages
            .Where(i => i.CreatedById == userId)
            .Select(i => i.PrintId)
            .Distinct()
            .LongCountAsync(ct);

        static bool Successful(PrintStatus s) => s is PrintStatus.Success or PrintStatus.PartialSuccess;
        static long Seconds(PrintRow p) => PrintMetrics.Resolve(p.PrintTimeInSeconds, p.EstimatedPrintTimeInSeconds);

        var rowMaterial = usage.Where(u => Successful(u.Status)).Sum(u => (long)PrintMetrics.Resolve(u.AmountMg, u.EstimatedAmountMg));
        var otherMaterial = prints.Where(p => Successful(p.Status)).Sum(p => (long)PrintMetrics.Resolve(p.FilamentUsageMg, p.EstimatedFilamentUsageMg));

        var pluginSlicers = prints
            .Where(p => p.Source == PrintSource.SlicerPlugin && p.Slicer is not null)
            .Select(p => p.Slicer!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var maxDistinct = usage
            .Where(u => u.FilamentId is not null)
            .GroupBy(u => u.PrintId)
            .Select(g => g.Select(u => u.FilamentId).Distinct().Count())
            .DefaultIfEmpty(0)
            .Max();

        // Case-insensitive, matching SQL Server's default collation, so "PLA" and "pla" are one.
        var materialTypes = usage
            .Where(u => !string.IsNullOrWhiteSpace(u.MaterialType))
            .Select(u => u.MaterialType!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new PrintAggregate(
            prints.Count,
            prints.Where(p => Successful(p.Status)).Sum(Seconds),
            rowMaterial + otherMaterial,
            prints.Where(p => p.Status == PrintStatus.Success).Select(Seconds).DefaultIfEmpty(0).Max(),
            withImage,
            prints.Count(p => p.ViewStatus == PrintViewStatus.Public),
            prints.Count(p => p.Status == PrintStatus.Failed),
            prints.Count(p => p.Source == PrintSource.SlicerPlugin),
            prints.Count(p => p.Source == PrintSource.OctoPrint),
            prints.Count(p => p.Source == PrintSource.Moonraker),
            prints.Count(p => p.Source == PrintSource.Mcp),
            pluginSlicers,
            maxDistinct,
            materialTypes);
    }
}
