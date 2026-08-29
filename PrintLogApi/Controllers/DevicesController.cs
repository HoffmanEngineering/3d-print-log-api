using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Device;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>Registration of mobile devices for push notifications.</summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "InteractiveUserOnly")]
public class DevicesController(IDeviceTokenService deviceTokenService) : ControllerBase
{
    /// <summary>Registers or refreshes this installation's push token.</summary>
    /// <response code="204">The device was registered.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> RegisterDevice([FromBody] RegisterDeviceDto dto)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        // [Required] has already rejected a missing token via ModelState, but that proof is
        // invisible to flow analysis; the pattern moves it somewhere the compiler can see
        // rather than suppressing the warning.
        if (dto.Token is not { } token)
        {
            return BadRequest();
        }

        await deviceTokenService.RegisterDevice(userId.Value, token, dto.Platform, dto.AppVersion);
        return NoContent();
    }

    /// <summary>Removes this installation's push token.</summary>
    /// <response code="204">The device was removed, or was already absent.</response>
    [HttpDelete("{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> DeleteDevice(string token)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        // 204 whether or not a row existed: logout must be idempotent, and a 404 would leak
        // whether a token is registered to someone else.
        await deviceTokenService.RemoveDevice(userId.Value, token);
        return NoContent();
    }
}
