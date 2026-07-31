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

            if (filter.PrinterIds.Count > 0)
                scoped = scoped.Where(p => filter.PrinterIds.Contains(p.PrinterId));
            if (filter.ProjectIds.Count > 0)
                scoped = scoped.Where(p => p.ProjectId.HasValue && filter.ProjectIds.Contains(p.ProjectId.Value));
            if (filter.Statuses.Count > 0)
                scoped = scoped.Where(p => filter.Statuses.Contains(p.Status));
            if (filter.FilamentIds.Count > 0)
                scoped = scoped.Where(p => p.FilamentUsage.Any(pf =>
                    pf.FilamentId.HasValue && filter.FilamentIds.Contains(pf.FilamentId.Value)));

            return scoped;
        }
    }
}
