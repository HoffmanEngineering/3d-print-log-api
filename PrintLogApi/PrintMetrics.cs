using System;
using System.Linq.Expressions;
using PrintLogApi.Models;

namespace PrintLogApi
{
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
    }
}
