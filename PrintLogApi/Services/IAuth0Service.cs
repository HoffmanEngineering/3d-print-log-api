using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs;

namespace PrintLogApi.Services
{
    public interface IAuth0Service
    {
        Task DeleteUser(string oauthUserId);
        Task<string> GetManagementApiBearerToken();
        Task GetUser(string oauthUserId);

        /// <summary>
        /// Lists the user's Auth0 grants for the dedicated MCP audience that include the
        /// read:printdata scope, across all pages. Used to show and revoke connected AI agents.
        /// </summary>
        Task<IReadOnlyList<ConnectedAgentDto>> ListMcpGrants(string authUserId, CancellationToken ct);

        /// <summary>
        /// Revokes one of the user's MCP grants. Verifies the grant belongs to the user AND targets
        /// the MCP audience AND includes read:printdata before deleting; throws for foreign,
        /// non-MCP, or missing grants. A concurrent Auth0 404 is treated as idempotent success.
        /// </summary>
        Task RevokeMcpGrant(string authUserId, string grantId, CancellationToken ct);
    }
}
