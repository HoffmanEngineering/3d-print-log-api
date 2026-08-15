#nullable enable

using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

        /// <summary>
        /// A stable, non-reversible hash of the Auth0 subject for telemetry — never the raw
        /// subject or the internal user id.
        /// </summary>
        public static string HashSubject(ClaimsPrincipal? user)
        {
            var subject = user?.FindFirst(ClaimTypes.Upn)?.Value;
            if (string.IsNullOrEmpty(subject))
            {
                return "anonymous";
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }
    }
}
