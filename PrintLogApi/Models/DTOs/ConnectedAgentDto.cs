using System.Collections.Generic;

namespace PrintLogApi.Models.DTOs
{
    /// <summary>
    /// A connected AI agent, backed by an Auth0 grant for the dedicated MCP audience. v1 exposes
    /// only fields the Auth0 grants response supplies.
    /// </summary>
    public sealed record ConnectedAgentDto(string GrantId, string ClientId, IReadOnlyList<string> Scopes);
}
