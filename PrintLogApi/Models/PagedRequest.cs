namespace PrintLogApi.Models;

public class PagedRequest
{
    /// <summary>
    /// The 1-based page number for pagination.
    /// </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// The number of items to return per page.
    /// </summary>
    public int PageSize { get; set; } = 10;
}
