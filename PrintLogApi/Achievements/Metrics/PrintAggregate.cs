using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Achievements.Metrics;

/// <summary>
/// Every print-based count, sum and max for one user, read in a single command.
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
    private static readonly PrintAggregate Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 0, 0);

    // The slicer keys whose presence the aggregate reports. "other" is left out: no metric reads
    // it (slicer.distinct excludes it, and plugged-in counts SlicerPlugin prints directly).
    private static readonly string[] TrackedSlicers =
        [SlicerNames.Cura, SlicerNames.PrusaSlicer, SlicerNames.OrcaSlicer, SlicerNames.BambuStudio, SlicerNames.Anycubic, SlicerNames.FLSun];

    /// <summary>
    /// One SELECT of uncorrelated scalar subqueries, each an index seek on the user's prints.
    /// Shaped this way, not as one GROUP BY, because SQL Server rejects an aggregate over an
    /// expression containing a subquery (error 130) and an outer reference inside an aggregate
    /// (8124) — SQLite accepts both, so the tests would never notice. The material sum is split
    /// into its row term and its scalar term for the same reason; the two are disjoint and add.
    /// </summary>
    internal static async Task<PrintAggregate> LoadAsync(PrintLogContext db, long userId, CancellationToken ct)
    {
        var prints = db.Prints.Where(p => p.CreatedById == userId);
        var successful = prints.Where(p => p.Status == PrintStatus.Success || p.Status == PrintStatus.PartialSuccess);
        var usage = db.PrintFilament.Where(pf => pf.Print.CreatedById == userId);

        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                PrintCount = prints.LongCount(),
                SuccessSeconds = successful.Sum(p => (long)(
                    p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                    : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                    : 0)),
                RowMaterialMg = usage
                    .Where(pf => pf.Print.Status == PrintStatus.Success || pf.Print.Status == PrintStatus.PartialSuccess)
                    .Sum(pf => (long)(
                        pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                        : 0)),
                OtherMaterialMg = successful.Sum(p => (long)(
                    p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                    : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                    : 0)),
                LongestSuccessSeconds = prints.Where(p => p.Status == PrintStatus.Success).Max(p => (int?)(
                    p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                    : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                    : 0)),
                WithImage = prints.LongCount(p => db.PrintImages.Any(i => i.PrintId == p.Id)),
                Public = prints.LongCount(p => p.ViewStatus == PrintViewStatus.Public),
                Failed = prints.LongCount(p => p.Status == PrintStatus.Failed),
                SlicerPlugin = prints.LongCount(p => p.Source == PrintSource.SlicerPlugin),
                OctoPrint = prints.LongCount(p => p.Source == PrintSource.OctoPrint),
                Moonraker = prints.LongCount(p => p.Source == PrintSource.Moonraker),
                Mcp = prints.LongCount(p => p.Source == PrintSource.Mcp),
                Cura = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.Cura),
                PrusaSlicer = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.PrusaSlicer),
                OrcaSlicer = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.OrcaSlicer),
                BambuStudio = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.BambuStudio),
                Anycubic = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.Anycubic),
                FLSun = prints.Any(p => p.Source == PrintSource.SlicerPlugin && p.Slicer == SlicerNames.FLSun),
                MaxDistinctMaterials = usage
                    .Where(pf => pf.FilamentId != null)
                    .GroupBy(pf => pf.PrintId)
                    .Select(g => (int?)g.Select(pf => pf.FilamentId).Distinct().Count())
                    .Max(),
                DistinctMaterialTypes = usage
                    .Where(pf => pf.Filament != null && pf.Filament.MaterialType != null && pf.Filament.MaterialType != "")
                    .Select(pf => pf.Filament!.MaterialType)
                    .Distinct()
                    .Count(),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return Empty;
        }

        bool[] present = [row.Cura, row.PrusaSlicer, row.OrcaSlicer, row.BambuStudio, row.Anycubic, row.FLSun];
        var pluginSlicers = TrackedSlicers.Where((_, i) => present[i]).ToList();

        return new PrintAggregate(
            row.PrintCount,
            row.SuccessSeconds,
            row.RowMaterialMg + row.OtherMaterialMg,
            row.LongestSuccessSeconds ?? 0,
            row.WithImage,
            row.Public,
            row.Failed,
            row.SlicerPlugin,
            row.OctoPrint,
            row.Moonraker,
            row.Mcp,
            pluginSlicers,
            row.MaxDistinctMaterials ?? 0,
            row.DistinctMaterialTypes);
    }
}
