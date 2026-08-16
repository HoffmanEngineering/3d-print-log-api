using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Lists and revokes the current user's connected AI agents (Auth0 grants for the MCP audience).
    /// Uses normal app authentication — NOT the MCP scope.
    /// </summary>
    [Route("api/connected-agents")]
    [ApiController]
    [Authorize]
    public class ConnectedAgentsController : ControllerBase
    {
        private readonly IAuth0Service _auth0Service;

        public ConnectedAgentsController(IAuth0Service auth0Service)
        {
            _auth0Service = auth0Service;
        }

        /// <summary>Gets the current user's connected AI agents.</summary>
        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<ConnectedAgentDto>>> GetConnectedAgents(CancellationToken ct)
        {
            var authUserId = User.FindFirst(ClaimTypes.Upn)?.Value;
            if (string.IsNullOrEmpty(authUserId))
            {
                return Unauthorized();
            }

            var agents = await _auth0Service.ListMcpGrants(authUserId, ct);
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
                await _auth0Service.RevokeMcpGrant(authUserId, grantId, ct);
                return NoContent();
            }
            catch (NotFoundException)
            {
                return NotFound();
            }
        }
    }
}
