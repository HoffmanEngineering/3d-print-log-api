using System.Linq.Expressions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Profiles;

namespace PrintLogApi;

/// <summary>
/// The single rule for "actual, else estimated" across prints.
///
/// ZERO OR LESS MEANS NOT RECORDED, not "zero seconds". The integration webhooks persist
/// (int)Math.Round(x ?? 0.0), so a missing duration lands in the database as 0 — and
/// 0.HasValue is true, so any ??-coalescing reader reports 0 instead of falling back. That is
/// how a production MCP query came to report 0 seconds of printing across 13 prints that every
/// one of them had an estimate for.
///
/// Do not restate this rule. Where EF forces an inline ternary (group projections — see the
/// plan's Global Constraints), PrintMetricsTests pins the copy to this one.
/// </summary>
public static class PrintMetrics
{
    /// <summary>The value to report: the actual when genuinely recorded, else the estimate.</summary>
    public static int Resolve(int? actual, int? estimated) =>
        actual is > 0 ? actual.Value
        : estimated is > 0 ? estimated.Value
        : 0;

    /// <summary>True when the reported value came from an estimate rather than a measurement.</summary>
    public static bool IsEstimated(int? actual, int? estimated) =>
        actual is not > 0 && estimated is > 0;

    /// <summary>
    /// EF-translatable duration. TOP-LEVEL USE ONLY: prints.SumAsync(DurationSecondsExpr, ct).
    /// It cannot be passed to g.Sum(...) inside a group projection — that overload takes a
    /// Func, not an Expression, so it is a compile error, not a translation failure.
    /// </summary>
    public static Expression<Func<Print, int>> DurationSecondsExpr =>
        p => p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
            ? p.PrintTimeInSeconds.Value
            : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                ? p.EstimatedPrintTimeInSeconds.Value
                : 0;

    /// <summary>EF-translatable provenance flag. Top-level use only, as above.</summary>
    public static Expression<Func<Print, bool>> DurationIsEstimatedExpr =>
        p => !(p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0)
            && p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0;

    /// <summary>
    /// EF-translatable material usage in milligrams. TOP-LEVEL USE ONLY, as with
    /// DurationSecondsExpr, or spliced into a group projection with
    /// <see cref="ExpressionSplice.SelectWith{TSource, TArgument, TResult}"/> as
    /// <c>g.Sum(material)</c>. The per-filament rows, each resolved by
    /// <see cref="FilamentProfile.UsageRowMg"/> (actual, else estimate, in whichever measure the
    /// row carries), PLUS the scalar Print.FilamentUsageMg, which records "other filament" —
    /// material used on the print that was never attached to a tracked spool. The two are
    /// disjoint, so they add. Every term obeys the zero-or-negative rule above, so a stored 0 or
    /// a negative falls back rather than contributing.
    ///
    /// <para>Each row is rounded to the milligram once and the rounded rows are added, so a
    /// print's parts always sum to its total (get_print's MaterialUsage invariant) and every total
    /// built from this rule (overview, printer and activity analytics, achievements, the MCP
    /// summary, the print list, projects) agrees to the milligram. SQL's ROUND rounds midpoints
    /// away from zero; <see cref="Round"/> does the same in memory.</para>
    /// </summary>
    public static Expression<Func<Print, long>> MaterialMgExpr { get; } =
        ExpressionSplice.Inline<Print, Func<PrintFilament, double>, long>(
            (p, row) => p.FilamentUsage!.Sum(pf => (long)Math.Round(row(pf)))
                + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                   : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                   : 0),
            FilamentProfile.UsageRowMg);

    /// <summary>
    /// <see cref="MaterialMgExpr"/> counting only the rows a user may see: no linked spool, or
    /// one they own. `no linked spool OR an owned one`, not `linked AND owned`: an unlinked row
    /// is legitimate untracked material. For the analytics tabs that read the user's printers.
    /// </summary>
    public static Expression<Func<Print, long>> OwnedMaterialMgExpr(long userId) =>
        ExpressionSplice.Inline<Print, Func<PrintFilament, double>, long>(
            (p, row) => p.FilamentUsage!
                    .Where(pf => pf.Filament == null || pf.Filament.CreatedById == userId)
                    .Sum(pf => (long)Math.Round(row(pf)))
                + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                   : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                   : 0),
            FilamentProfile.UsageRowMg);

    /// <summary>
    /// The print's usage rows alone, each resolved by <see cref="FilamentProfile.UsageRowMg"/>
    /// and rounded, then added: no "other filament" scalar. What the print list, get_print and search_prints
    /// report as a print's material.
    /// </summary>
    public static Expression<Func<Print, long>> RowsMgExpr { get; } =
        ExpressionSplice.Inline<Print, Func<PrintFilament, double>, long>(
            (p, row) => p.FilamentUsage!.Sum(pf => (long)Math.Round(row(pf))),
            FilamentProfile.UsageRowMg);

    /// <summary>True when ANY usage row fell back to its estimate (<see cref="FilamentProfile.UsageRowIsEstimated"/>).</summary>
    public static Expression<Func<Print, bool>> RowsAreEstimatedExpr { get; } =
        ExpressionSplice.Inline<Print, Func<PrintFilament, bool>, bool>(
            (p, isEstimated) => p.FilamentUsage!.Any(isEstimated),
            FilamentProfile.UsageRowIsEstimated);

    /// <summary>
    /// True when ANY term contributing to MaterialMgExpr fell back to its estimate —
    /// either a per-filament row or the "other filament" scalar. Top-level use only.
    /// </summary>
    public static Expression<Func<Print, bool>> MaterialIsEstimatedExpr { get; } =
        ExpressionSplice.Inline<Print, Func<PrintFilament, bool>, bool>(
            (p, isEstimated) => p.FilamentUsage!.Any(isEstimated)
                || (!(p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0)
                    && p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0),
            FilamentProfile.UsageRowIsEstimated);

    private static readonly Func<PrintFilament, double> RowMg = FilamentProfile.UsageRowMg.Compile();
    private static readonly Func<PrintFilament, double> RowActualMg = FilamentProfile.UsageRowActualMg.Compile();
    private static readonly Func<PrintFilament, double> RowEstimatedMg = FilamentProfile.UsageRowEstimatedMg.Compile();

    /// <summary>
    /// <see cref="RowsMgExpr"/> over rows already in memory, for a mapping that runs after the
    /// query (the print list, projects). Each row's <c>Filament</c> must be loaded for its length
    /// or volume to convert. Rounds midpoints away from zero, as SQL's ROUND does, so the two
    /// paths agree. A collection that was not loaded counts as no usage.
    /// </summary>
    public static long RowsMg(IEnumerable<PrintFilament>? rows) => rows?.Sum(r => Round(RowMg(r))) ?? 0;

    /// <summary>The actual side of <see cref="RowsMg"/>: each row's <see cref="FilamentProfile.UsageRowActualMg"/>.</summary>
    public static long ActualRowsMg(IEnumerable<PrintFilament>? rows) => rows?.Sum(r => Round(RowActualMg(r))) ?? 0;

    /// <summary>The estimated side of <see cref="RowsMg"/>: each row's <see cref="FilamentProfile.UsageRowEstimatedMg"/>.</summary>
    public static long EstimatedRowsMg(IEnumerable<PrintFilament>? rows) => rows?.Sum(r => Round(RowEstimatedMg(r))) ?? 0;

    /// <summary>Milligrams rounded the way SQL Server's ROUND rounds them, for in-memory sums.</summary>
    public static long Round(double? milligrams) => (long)Math.Round(milligrams ?? 0, MidpointRounding.AwayFromZero);
}
