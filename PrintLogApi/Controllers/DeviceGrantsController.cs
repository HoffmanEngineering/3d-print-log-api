using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.DeviceGrants;
using PrintLogApi.Services.DeviceGrants;

namespace PrintLogApi.Controllers;

/// <summary>
/// Paired devices (#148): printlog-bridge, and later Home Assistant, holding a scoped device token
/// instead of a personal API key. A device runs Auth0's device flow (the parameters come from
/// <c>GET pairing</c>), then exchanges the Auth0 access token here for a device token. The user
/// lists and revokes their devices from the web app.
/// </summary>
[Route("api/device-grants")]
[ApiController]
public class DeviceGrantsController(
    IDeviceGrantService grants,
    IOptions<DevicePairingOptions> options,
    TelemetryClient telemetry) : ControllerBase
{
    /// <summary>
    /// Describes the Auth0 device flow a device runs to pair: the device authorization and token
    /// endpoints, the client id, the audience, and the scopes it may request.
    /// </summary>
    /// <response code="200">Pairing is available.</response>
    /// <response code="503">Pairing is not configured on this server (`code`: `device_pairing_unavailable`).</response>
    [HttpGet("pairing")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<DevicePairingConfigDto> GetPairingConfig()
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return CodedProblem(StatusCodes.Status503ServiceUnavailable, "Device pairing is not available.", DeviceTokenErrors.PairingUnavailable);
        }

        var issuer = settings.Issuer;
        return new DevicePairingConfigDto(
            issuer,
            issuer + "oauth/device/code",
            issuer + "oauth/token",
            settings.ClientId!,
            settings.Audience!,
            DeviceScopes.All.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Pairs a device: exchanges an Auth0 device-flow access token (in `Authorization: Bearer`,
    /// for the pairing audience, at most a few minutes old) for a device token. The grant gets the
    /// device scopes the access token carries. Pairing the same `deviceId` again replaces its
    /// token and keeps the grant. Each access token pairs once, and one requested with
    /// `offline_access` (or refreshed) is refused.
    /// </summary>
    /// <response code="201">A new device was paired. The body carries its token, shown only this once.</response>
    /// <response code="200">The device was already paired; its token was replaced.</response>
    /// <response code="400">`deviceId` or `name` is missing or invalid.</response>
    /// <response code="403">The token is not a pairing token, or carries no device scope (`code`: `insufficient_scope`), carries `offline_access` (`offline_access_not_allowed`), or was already exchanged (`pairing_token_used`).</response>
    /// <response code="409">The account has as many paired devices as allowed (`code`: `device_grant_limit`).</response>
    [HttpPost]
    [Authorize(Policy = DevicePairingAuthentication.Policy)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PairedDeviceDto>> Pair(PairDeviceDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (body.DeviceId is not { } deviceId || deviceId == Guid.Empty)
        {
            return CodedProblem(StatusCodes.Status400BadRequest, "deviceId must be a non-empty UUID.", "invalid_request");
        }

        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > DeviceGrant.NameMaxLength || name.Any(char.IsControl))
        {
            return CodedProblem(StatusCodes.Status400BadRequest,
                $"name is required: 1 to {DeviceGrant.NameMaxLength} characters, no control characters.", "invalid_request");
        }

        var scopeClaim = User.FindFirst("scope")?.Value;
        if (DevicePairingAuthentication.CarriesOfflineAccess(scopeClaim))
        {
            return CodedProblem(StatusCodes.Status403Forbidden,
                "Pairing tokens must not carry offline_access: run the device flow without it.", DeviceTokenErrors.OfflineAccessNotAllowed);
        }

        var scopes = DeviceScopes.FromScopeClaim(scopeClaim);
        if (scopes.Count == 0)
        {
            return CodedProblem(StatusCodes.Status403Forbidden, "The token carries no device scope.", DeviceTokenErrors.InsufficientScope);
        }

        var clientId = User.FindFirst("azp")?.Value;
        PairedDevice paired;
        try
        {
            paired = await grants.PairAsync(userId, deviceId, name, scopes, clientId,
                DevicePairingAuthentication.SpendOf(User, Request.Headers.Authorization.ToString(), options.Value), ct);
        }
        catch (PairingTokenUsedException)
        {
            return CodedProblem(StatusCodes.Status403Forbidden,
                "This pairing token was already used. Run the device flow again.", DeviceTokenErrors.PairingTokenUsed);
        }
        catch (DeviceGrantLimitException)
        {
            return CodedProblem(StatusCodes.Status409Conflict,
                "This account has as many paired devices as allowed. Revoke one to pair another.", DeviceTokenErrors.GrantLimit);
        }

        var grant = paired.Grant;
        telemetry.TrackEvent(paired.Created ? "DevicePaired" : "DeviceRepaired", GrantProperties(grant.Id, clientId, grant.Scopes));

        var dto = new PairedDeviceDto(grant.Id, grant.DeviceId, grant.Name, DeviceScopes.Split(grant.Scopes),
            AsUtc(grant.CreatedDate), paired.Token, "Bearer");

        // No Location: nothing serves a single grant, and the token is not a resource.
        return paired.Created ? StatusCode(StatusCodes.Status201Created, dto) : Ok(dto);
    }

    /// <summary>Lists the current user's paired devices, newest first. Never includes tokens.</summary>
    [HttpGet]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceGrantDto>>> List(CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var rows = await grants.ListAsync(userId, ct);
        return rows.Select(g => new DeviceGrantDto(
            g.Id, g.DeviceId, g.Name, g.ClientId, DeviceScopes.Split(g.Scopes),
            AsUtc(g.CreatedDate), AsUtc(g.PairedDate), g.LastUsedDate is { } used ? AsUtc(used) : null)).ToList();
    }

    /// <summary>
    /// Revokes a paired device. Its token stops working on its next request; the device has to
    /// pair again.
    /// </summary>
    /// <response code="204">Revoked.</response>
    /// <response code="404">No paired device of yours has that id.</response>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (await grants.RevokeAsync(userId, id, ct) is not { } revoked)
        {
            return NotFound();
        }

        telemetry.TrackEvent("DeviceGrantRevoked", GrantProperties(revoked.Id, revoked.ClientId, revoked.Scopes));
        return NoContent();
    }

    /// <summary>What the pairing and revoke events carry: which grant, through which app, with what. Never the token.</summary>
    private static Dictionary<string, string> GrantProperties(long grantId, string? clientId, string scopes) => new()
    {
        ["GrantId"] = grantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ClientId"] = clientId ?? string.Empty,
        ["Scopes"] = scopes,
    };

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private ObjectResult CodedProblem(int status, string detail, string code)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
