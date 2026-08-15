#nullable enable

using System;
using System.Linq;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>
    /// The one place the analytics tenant boundary and the shared filter clauses are expressed.
    ///
    /// Every analytics endpoint starts here. Divergence between two endpoints' scoping is a
    /// tenant-isolation bug, not a cosmetic one, so the rule is written once and tested once.
    /// Unowned filter ids are NOT rejected: they simply match nothing, which is what stops the
    /// endpoint being used to probe whether another user's printer exists.
    /// </summary>
    public static class AnalyticsQueryScope
    {
        public static IQueryable<Print> Scope(
            IQueryable<Print> prints,
            long userId,
            AnalyticsFilter filter,
            DateTimeOffset? from,
            DateTimeOffset? to)
        {
            var scoped = prints.Where(p => p.CreatedById == userId);

            // Half-open [from, to). Undated prints fall out of a ranged query by construction,
            // because a null StartDate satisfies neither comparison.
            if (from.HasValue && to.HasValue)
                scoped = scoped.Where(p => p.StartDate >= from.Value && p.StartDate < to.Value);

            // Each id filter carries its OWN ownership predicate rather than trusting that the
            // parent print's CreatedById implies an owned printer or spool. The write paths do
            // validate both (PrintService.cs:574 for the printer, CanUserAccessAllFilaments for
            // spools), so a cross-owner reference should not exist — but PrintService itself
            // re-checks ownership on the read side too (PrintService.cs:125,221), and matching
            // that habit is what keeps a future write path, an import, or a manual data fix from
            // silently turning one of these filters into a probe for another user's ids.
            if (filter.PrinterIds.Count > 0)
                scoped = scoped.Where(p =>
                    p.Printer.UserId == userId && filter.PrinterIds.Contains(p.PrinterId));
            if (filter.ProjectIds.Count > 0)
                scoped = scoped.Where(p => p.ProjectId.HasValue && filter.ProjectIds.Contains(p.ProjectId.Value));
            if (filter.Statuses.Count > 0)
                scoped = scoped.Where(p => filter.Statuses.Contains(p.Status));
            if (filter.FilamentIds.Count > 0)
                scoped = scoped.Where(p => p.FilamentUsage!.Any(pf =>
                    pf.FilamentId.HasValue
                    && pf.Filament!.CreatedById == userId
                    && filter.FilamentIds.Contains(pf.FilamentId.Value)));

            return scoped;
        }
    }
}
