using Microsoft.AspNetCore.Authorization;

namespace PrintLogApi.Authentication;

/// <summary>
/// Requires that the authenticated principal carries a non-blank Auth0 subject
/// (mapped to <c>Upn</c>) and resolves to an internal user id. This blocks every
/// MCP request — including <c>tools/list</c> and <c>ping</c> — before dispatch when
/// the caller cannot be mapped to a 3D Print Log user.
/// </summary>
public class McpUserRequirement : IAuthorizationRequirement
{
}
