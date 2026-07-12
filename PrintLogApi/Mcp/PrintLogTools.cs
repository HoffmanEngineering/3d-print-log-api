using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The read-only MCP tool surface. Every tool runs as the authenticated MCP user; the
    /// class-level <see cref="AuthorizeAttribute"/> is defense-in-depth on top of the endpoint's
    /// McpAccess policy. Later tasks extend this type with the data tools.
    /// </summary>
    [McpServerToolType]
    [Authorize(Policy = "McpAccess")]
    public class PrintLogTools
    {
        [McpServerTool, Description("Health check. Echoes the input.")]
        public static string Ping([Description("Any string")] string message) => $"pong: {message}";
    }
}
