using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Achievements;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Achievements;

namespace PrintLogApi.Controllers;

/// <summary>
/// Achievements: the badge catalog, the signed-in user's collection, and the hint card.
/// </summary>
[Route("api/achievements")]
[ApiController]
[Authorize]
public class AchievementsController(IAchievementQueryService achievements) : ControllerBase
{
    /// <summary>
    /// Get the achievement catalog. Hidden badges are counted but never described.
    /// </summary>
    /// <response code="200">The catalog, with each tier's rarity.</response>
    [HttpGet("catalog")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AchievementCatalogDto>> GetCatalog(CancellationToken ct)
    {
        var catalog = await achievements.GetCatalogAsync(ct);
        // Identical for every caller and only changes with a deploy or the hourly rarity refresh.
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(catalog);
    }

    /// <summary>
    /// Get the current user's achievements, progress and next hint. Grants anything the user
    /// qualifies for but does not hold yet, first.
    /// </summary>
    /// <response code="200">The user's achievements.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MyAchievementsDto>> GetMine(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        return Ok(await achievements.GetMineAsync(userId.Value, ct));
    }

    /// <summary>
    /// Get another maker's earned achievements for their public profile. Follows the profile's
    /// visibility and the owner's "show on profile" setting; anything the viewer may not see comes
    /// back empty rather than as an error, so a logged-out visitor is never bounced.
    /// </summary>
    /// <param name="id">The maker's user id.</param>
    /// <response code="200">The earned achievements, or an empty result.</response>
    [HttpGet("~/api/users/{id:long}/achievements")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PublicAchievementsDto>> GetForUser(long id, CancellationToken ct) =>
        Ok(await achievements.GetPublicAsync(User, id, ct));

    /// <summary>
    /// Dismiss the hint card for one badge tier. It stays hidden until a different hint is chosen.
    /// </summary>
    /// <response code="204">Dismissed.</response>
    /// <response code="400">The key or tier is not in the catalog.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    [HttpPost("hint/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DismissHint([FromBody] DismissHintRequestDto request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            await achievements.DismissHintAsync(userId.Value, request.Key ?? "", request.Tier, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }
}
