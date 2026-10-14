using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Spoolman;
using PrintLogApi.Services.Spoolman;

namespace PrintLogApi.Controllers;

/// <summary>
/// Connect a Klipper printer to 3D Print Log through Moonraker's built-in Spoolman support.
/// </summary>
/// <remarks>
/// Each printer gets its own credential, which goes in the <c>[spoolman]</c> section of
/// <c>moonraker.conf</c>. Only a signed-in user can make one: an API key cannot mint another
/// credential.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "InteractiveUserOnly")]
public class SpoolmanConnectionsController(ISpoolmanConnectionService connections) : ControllerBase
{
    /// <summary>
    /// Connect a printer to Spoolman, or reconnect it with a new secret.
    /// </summary>
    /// <remarks>
    /// Returns the secret and a ready-to-paste <c>[spoolman]</c> block once; neither can be shown
    /// again. Reconnecting a printer revokes its previous secret. The Free plan includes one
    /// connected printer; connecting a second returns 403 with <c>code</c>
    /// <c>spoolman_connection_limit</c> and the <c>limit</c>.
    /// </remarks>
    /// <param name="printerId">The printer to connect.</param>
    /// <response code="201">The new connection, with its secret.</response>
    /// <response code="403">The Free plan's connection is used by another printer.</response>
    /// <response code="404">The printer is not yours.</response>
    [HttpPost("/api/Printers/{printerId:long}/spoolman-connection")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SpoolmanConnectionCreatedDto>> CreateSpoolmanConnection(long printerId)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        SpoolmanConnectionCreatedDto? created;
        try
        {
            created = await connections.CreateAsync(userId, printerId, $"{Request.Scheme}://{Request.Host}", HttpContext.RequestAborted);
        }
        catch (SpoolmanConnectionLimitException ex)
        {
            var problem = ProblemDetailsFactory.CreateProblemDetails(
                HttpContext,
                statusCode: StatusCodes.Status403Forbidden,
                title: "Spoolman connection limit reached",
                detail: ex.Message);
            problem.Extensions["code"] = "spoolman_connection_limit";
            problem.Extensions["limit"] = ex.Limit;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentTypes = { "application/problem+json" },
            };
        }

        if (created is null)
        {
            return NotFound();
        }

        // No Location: there is no endpoint that returns one connection, and the secret must not
        // be retrievable again in any case.
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>
    /// List your Spoolman connections.
    /// </summary>
    /// <remarks>
    /// Every connection you have made, newest first, with its printer, when the printer last
    /// connected, and its state: <c>active</c>, <c>disabled</c> (paused because the Free plan
    /// includes one connected printer; upgrading resumes it) or <c>revoked</c>. Never includes a
    /// secret.
    /// </remarks>
    /// <response code="200">Your connections.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<SpoolmanConnectionDto>>> GetSpoolmanConnections()
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return Ok(await connections.ListAsync(userId, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Revoke a Spoolman connection.
    /// </summary>
    /// <remarks>
    /// The printer's secret stops working at once. Revoking is final; connect the printer again
    /// for a new secret.
    /// </remarks>
    /// <param name="id">The connection's id.</param>
    /// <response code="204">Revoked, or already revoked.</response>
    /// <response code="404">The connection is not yours.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSpoolmanConnection(Guid id)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await connections.RevokeAsync(userId, id, HttpContext.RequestAborted)
            ? NoContent()
            : NotFound();
    }
}
