namespace PrintLogApi.Services;

/// <summary>
/// Normalization for <see cref="Models.Print.ExternalSource"/> / <see cref="Models.Print.ExternalId"/>
/// (#144). Every write and every lookup goes through these, so <c>Moonraker</c> and
/// <c>moonraker </c> name the same pair rather than two prints.
/// </summary>
public static class ExternalPrintIds
{
    /// <summary>
    /// The source MCP <c>create_print</c> stamps with its idempotency key. Reserved: a REST caller
    /// may not use it, or it could replay (and so read back) a print an agent created under that key.
    /// </summary>
    public const string McpSource = "mcp";

    /// <summary>Sources are identifiers, so case and surrounding space are not significant.</summary>
    public static string NormalizeSource(string source) => source.Trim().ToLowerInvariant();

    /// <summary>
    /// Ids are opaque, so only surrounding space is dropped and the stored id keeps the case it was
    /// sent in. They are nonetheless <em>matched</em> case-insensitively (#204): the column has no
    /// explicit collation, so the lookups and the unique index follow SQL Server's default
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>, and <c>ABc1</c> finds a print stored as <c>abC1</c>.
    /// That is the documented contract rather than a binary-collation migration, which would
    /// rebuild a unique index on <c>Prints</c> for collisions no current id format produces.
    /// Anything that compares a pair in memory must use <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// to agree with the database. SQLite compares binary, so the test suite cannot see the
    /// database half of this.
    /// </summary>
    public static string NormalizeId(string externalId) => externalId.Trim();
}
