namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Bounded paging for MCP tools. Page size defaults to 25 and is hard-capped at 100
    /// (the existing <c>PagedList</c> does not cap); page numbers are 1-based.
    /// </summary>
    public static class McpPaging
    {
        public const int Default = 25;
        public const int Max = 100;

        // Upper bound so (page - 1) * pageSize cannot overflow a 32-bit offset (max page size 100
        // keeps the worst-case offset well under int.MaxValue). Values beyond this are rejected as
        // invalid arguments rather than surfacing an unexpected EF error.
        public const int MaxPage = 1_000_000;

        public static int ClampPageSize(int? requested) =>
            System.Math.Clamp(requested ?? Default, 1, Max);

        public static int RequirePage(int page)
        {
            if (page < 1 || page > MaxPage)
            {
                throw McpToolException.InvalidArguments($"page must be between 1 and {MaxPage}.");
            }
            return page;
        }
    }
}
