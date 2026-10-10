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
    /// Ids are opaque and stay case-sensitive (a connector's job id may well be). Only surrounding
    /// space is dropped.
    /// </summary>
    public static string NormalizeId(string externalId) => externalId.Trim();
}
