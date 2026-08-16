using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Lists and revokes the current user's connected AI agents (Auth0 grants for the MCP audience).
/// Uses normal app authentication — NOT the MCP scope.
/// </summary>
[Route("api/connected-agents")]
[ApiController]
[Authorize]
public class ConnectedAgentsController(IAuth0Service auth0Service) : ControllerBase
{
    /// <summary>Gets the current user's connected AI agents.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConnectedAgentDto>>> GetConnectedAgents(CancellationToken ct)
    {
        var authUserId = User.FindFirst(ClaimTypes.Upn)?.Value;
        if (string.IsNullOrEmpty(authUserId))
        {
            return Unauthorized();
        }

        var agents = await auth0Service.ListMcpGrants(authUserId, ct);
        return Ok(agents);
    }

    /// <summary>Revokes one of the current user's connected AI agents by grant id.</summary>
    [HttpDelete("{grantId}")]
    public async Task<IActionResult> RevokeConnectedAgent(string grantId, CancellationToken ct)
    {
        var authUserId = User.FindFirst(ClaimTypes.Upn)?.Value;
        if (string.IsNullOrEmpty(authUserId))
        {
            return Unauthorized();
        }

        try
        {
            await auth0Service.RevokeMcpGrant(authUserId, grantId, ct);
            return NoContent();
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }
}
