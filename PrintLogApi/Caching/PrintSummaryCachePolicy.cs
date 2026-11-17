using PrintLogApi.Models;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Caching;

/// <summary>
/// Which <c>GET api/Prints/summary</c> requests go through the cache.
///
/// <para>The owner's own list caches whatever its shape, as it always has: the key carries the
/// owner's id, and every write bumps the owner's version. A reader who is not the owner
/// (anonymous, or signed in and looking at someone else's list) is different: the endpoint is
/// <c>[AllowAnonymous]</c> and every query value is part of the key, so varying one of them (a
/// date to the tick, a page number, a printer id, search text) made a new entry per request in the
/// one process-wide budget (<see cref="CacheBudget"/>), and about a dozen large pages evicted
/// everyone else's summaries, analytics and lookups.</para>
///
/// <para>Such a reader is therefore cached only for the shapes the web app's public pages send,
/// and runs uncached otherwise, with the same query and the same redaction. The public pages
/// (3d-print-log-ui, November 2026) send:</para>
/// <list type="bullet">
/// <item>the profile's print list (<c>user-prints.component.ts</c>): ten a page, start date newest
/// first, optionally one status, "load more" for later pages;</item>
/// <item>the project page (<c>project-detail.component.ts</c>): one hundred a page, the same sort,
/// filtered to that one project.</item>
/// </list>
/// <para>Neither sends a date range, printer or filament ids, or another sort. The profile's search
/// box does send <c>searchText</c>; a search runs uncached, because its text is unbounded.</para>
/// </summary>
public static class PrintSummaryCachePolicy
{
    /// <summary>The highest page a reader who is not the owner is cached for.</summary>
    public const int MaxVisitorPageNumber = 5;

    /// <summary>The public profile's page size.</summary>
    public const int ProfilePageSize = 10;

    /// <summary>The project page's page size.</summary>
    public const int ProjectPageSize = 100;

    /// <summary>
    /// Whether this request's page may be cached. <paramref name="viewerIsOwner"/> is true when
    /// the request lists the caller's own prints (no <c>userId</c>, or the caller's own).
    /// <paramref name="statuses"/> and <paramref name="projectIds"/> are the folded collections the
    /// controller builds, scalar parameters included.
    /// </summary>
    public static bool ShouldCache(
        bool viewerIsOwner,
        PagedRequest pagingRequest,
        string? searchText,
        IEnumerable<long>? filterByPrinterIds,
        IEnumerable<Guid>? filterByFilamentIds,
        SortRequest<PrintSummarySortColumn>? sortRequest,
        IReadOnlyCollection<Print.PrintStatus> statuses,
        IReadOnlyCollection<Guid> projectIds,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate)
    {
        ArgumentNullException.ThrowIfNull(pagingRequest);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(projectIds);

        if (viewerIsOwner)
        {
            return true;
        }

        var publicPageFilters =
            fromDate is null && toDate is null
            && string.IsNullOrEmpty(searchText)
            && filterByPrinterIds?.Any() != true
            && filterByFilamentIds?.Any() != true
            && sortRequest is { SortColumn: PrintSummarySortColumn.StartDate, SortDirection: SortDirection.Desc }
            && statuses.Count <= 1
            && pagingRequest.PageNumber is >= 1 and <= MaxVisitorPageNumber;

        if (!publicPageFilters)
        {
            return false;
        }

        return (pagingRequest.PageSize, projectIds.Count) switch
        {
            (ProfilePageSize, 0) => true,
            (ProjectPageSize, 1) => true,
            _ => false,
        };
    }
}
