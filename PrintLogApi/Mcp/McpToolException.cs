namespace PrintLogApi.Mcp;

/// <summary>
/// A tool-level failure with a stable, client-safe error code. The call-tool filter maps
/// these to <c>IsError = true</c> results; unknown exceptions are replaced with a generic
/// message so internal details never reach the client.
/// </summary>
public sealed class McpToolException : Exception
{
    public string Code { get; }

    private McpToolException(string code, string message) : base(message)
    {
        Code = code;
    }

    public static McpToolException InvalidArguments(string message) =>
        new("invalid_arguments", message);

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
