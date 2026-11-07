namespace PrintLogApi.Models;

public class PagedRequest
{
    /// <summary>
    /// The 1-based page number for pagination.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// The most rows one page returns (#314). Above every first-party client's largest request
    /// (the web app's analytics filters ask for 200), and small enough that a cached page stays
    /// under HybridCache's 1 MiB payload limit: an entry over it is refused and recomputed on
    /// every call, which is how the cap was found.
    /// </summary>
    public const int MaxPageSize = 250;

    private int _pageSize = 10;

    /// <summary>
    /// The number of items to return per page. A larger value is clamped to
    /// <see cref="MaxPageSize"/> rather than refused, and the response's paging reports the size
    /// used. Values below 1 are left as they are, as they always were.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = Math.Min(value, MaxPageSize);
    }
}
