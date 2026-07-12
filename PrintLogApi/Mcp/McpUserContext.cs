using System.Security.Claims;
using PrintLogApi.Extensions;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// Defense-in-depth accessors for the authenticated MCP user. Endpoint-level authorization
    /// (McpAccess) already guarantees a mapped user; these guard the tool code paths too.
    /// </summary>
    public static class McpUserContext
    {
        public static long RequireUserId(ClaimsPrincipal user) =>
            user.GetUserId() ?? throw McpToolException.Forbidden("A mapped user is required.");

        public static bool IsCreator(long currentUserId, long recordCreatedById) =>
            currentUserId == recordCreatedById;
    }
}
