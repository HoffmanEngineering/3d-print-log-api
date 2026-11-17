namespace PrintLogApi.Mcp;

/// <summary>
/// A tool-level failure with a stable, client-safe error code. The call-tool filter maps
/// these to <c>IsError = true</c> results; unknown exceptions are replaced with a generic
/// message so internal details never reach the client.
/// </summary>
public sealed class McpToolException : Exception
{
    public string Code { get; }

    /// <summary>A finer machine-readable reason within <see cref="Code"/>, for callers (REST) that map it; never sent over MCP.</summary>
    public string? SubCode { get; }

    private McpToolException(string code, string message, string? subCode = null) : base(message)
    {
        Code = code;
        SubCode = subCode;
    }

    public static McpToolException InvalidArguments(string message) =>
        new("invalid_arguments", message);

    public static McpToolException InvalidArguments(string message, string subCode) =>
        new("invalid_arguments", message, subCode);

    public static McpToolException NotFound(string message = "The requested item was not found.") =>
        new("not_found", message);

    public static McpToolException Forbidden(string message = "Access denied.") =>
        new("forbidden", message);

    public static McpToolException Conflict(string message) =>
        new("conflict", message);

    /// <summary>
    /// A dependency outside this API (the docs site) could not be read. Retrying later can
    /// succeed, unlike every other code here.
    /// </summary>
    public static McpToolException Unavailable(string message) =>
        new("unavailable", message);
}
