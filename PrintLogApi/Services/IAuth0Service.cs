using PrintLogApi.Models.DTOs;

namespace PrintLogApi.Services;

public interface IAuth0Service
{
    Task DeleteUser(string? oauthUserId);
    Task<string> GetManagementApiBearerToken();

    /// <summary>
    /// The user's email address from Auth0, or null when the account has none.
    /// <para>
    /// The API authenticates with an ACCESS token, and Auth0 puts <c>email</c> only in ID
    /// tokens, so a claim lookup can never resolve it — this Management API call is the only
    /// server-side route to the address. Requires <c>read:users</c> on the M2M application in
    /// every environment; without it the request fails and callers degrade.
    /// </para>
    /// <para>
    /// Throws <see cref="Exceptions.Auth0ApiException"/> on failure. Callers on a user-facing
    /// write path must treat this as best-effort and never fail the write over it.
    /// </para>
    /// </summary>
    Task<string?> GetUserEmail(string oauthUserId, CancellationToken ct);

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
