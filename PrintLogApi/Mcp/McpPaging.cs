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

        public static int ClampPageSize(int? requested) =>
            System.Math.Clamp(requested ?? Default, 1, Max);

        public static int RequirePage(int page) =>
            page >= 1 ? page : throw McpToolException.InvalidArguments("page must be 1 or greater.");
    }
}
