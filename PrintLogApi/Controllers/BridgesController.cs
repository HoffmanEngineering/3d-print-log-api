using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Services.Bridges;

namespace PrintLogApi.Controllers;

/// <summary>
/// Paired printlog-bridges (printlog-bridge#100, #103): what each one found on its network, and
/// the printers you chose for it to watch. A bridge checks in with its device token and gets its
/// list back; it can never add to the list itself.
/// </summary>
[Route("api/bridges")]
[ApiController]
public class BridgesController(IBridgeService bridges, TelemetryClient telemetry) : ControllerBase
{
    /// <summary>How often a bridge is asked to check in.</summary>
    public const int CheckInSeconds = 300;

    /// <summary>How often a bridge may poll its desired printers between check-ins.</summary>
    public const int DesiredPollSeconds = 60;

    /// <summary>
    /// A bridge checks in (device token with `bridge:sync`): it reports itself and the printers
    /// it recognized on its networks, and gets back the printers you connected to it.
    /// </summary>
    /// <remarks>
    /// Every field is optional, and one that is missing or null keeps what is stored, so `{}`
    /// only fetches the desired printers. A list that is sent replaces the stored one (an empty
    /// list clears it). Discovered printers are suggestions: nothing in a check-in ever adds a
    /// printer the bridge watches.
    ///
    /// The report is refused (400, `code` `invalid_check_in`) when a list is over its cap (16
    /// networks, 50 printers, 8 MACs per printer) or `agentVersion`, `mode` or `hostName` is
    /// malformed. A network or printer that breaks a rule (not private, malformed, a duplicate)
    /// is dropped and named in `rejected`, and the rest is stored.
    ///
    /// Check in every `checkInSeconds`, and poll `GET /api/bridges/me/desired` every
    /// `desiredPollSeconds` to pick up changes sooner. Counts against the connector write budget.
    /// </remarks>
    /// <response code="200">Checked in. The body carries the desired printers.</response>
    /// <response code="400">The report is malformed (`code`: `invalid_check_in`).</response>
    /// <response code="403">Not a device token (`code`: `device_token_required`), or the grant lacks `bridge:sync` (`insufficient_scope`).</response>
    [HttpPut("me")]
    [Authorize]
    [DeviceAccess(DeviceScopes.BridgeSync)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BridgeCheckInResponseDto>> CheckIn(BridgeCheckInDto body, CancellationToken ct)
    {
        if (DeviceGrantClaim.Get(User) is not { } grantId)
        {
            return DeviceTokenRequired();
        }

        var (report, error) = BridgeCheckInValidator.Validate(body);
        if (report is null)
        {
            return CodedProblem(StatusCodes.Status400BadRequest, error!, "invalid_check_in");
        }

        BridgeCheckInResult result;
        try
        {
            result = await bridges.CheckInAsync(grantId, report, ct);
        }
        catch (BridgeGrantRevokedException)
        {
            return CodedProblem(StatusCodes.Status401Unauthorized, "This device was revoked. Pair it again.", DeviceTokenErrors.InvalidToken);
        }

        if (result.Registered)
        {
            telemetry.TrackEvent("BridgeRegistered", new Dictionary<string, string> { ["GrantId"] = Id(grantId) });
        }

        if (report.Rejected.Count > 0)
        {
            telemetry.TrackEvent("BridgeCheckInEntriesRejected", new Dictionary<string, string>
            {
                ["GrantId"] = Id(grantId),
                ["Codes"] = string.Join(' ', report.Rejected.Select(r => r.Code).Distinct().Order(StringComparer.Ordinal)),
            });
        }

        return new BridgeCheckInResponseDto
        {
            BridgeId = grantId,
            CheckInSeconds = CheckInSeconds,
            DesiredPollSeconds = DesiredPollSeconds,
            Desired = result.Desired,
            Rejected = [.. report.Rejected],
        };
    }

    /// <summary>
    /// A bridge reads the printers you connected to it (device token with `bridge:sync`). Send
    /// the last `ETag` in `If-None-Match`: an unchanged list answers 304, so polling costs one
    /// small read. A poll also keeps the bridge's last-seen time fresh (at most every two
    /// minutes), so the bridge shows online while a backfill holds back its check-ins.
    /// </summary>
    /// <response code="200">The desired printers, with an `ETag`.</response>
    /// <response code="304">Unchanged since the `ETag` sent.</response>
    /// <response code="404">The bridge has not checked in yet (`code`: `bridge_not_registered`).</response>
    [HttpGet("me/desired")]
    [Authorize]
    [DeviceAccess(DeviceScopes.BridgeSync)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DesiredPrintersDto>> GetDesired(CancellationToken ct)
    {
        if (DeviceGrantClaim.Get(User) is not { } grantId)
        {
            return DeviceTokenRequired();
        }

        if (await bridges.PollDesiredVersionAsync(grantId, ct) is not { } version)
        {
            return NotRegistered();
        }

        // Weak: compression serves several encodings under one tag.
        var etag = new EntityTagHeaderValue($"\"{version}\"", isWeak: true);
        if (Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
        {
            Response.GetTypedHeaders().ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        if (await bridges.GetDesiredAsync(grantId, ct) is not { } desired)
        {
            return NotRegistered();
        }

        Response.GetTypedHeaders().ETag = new EntityTagHeaderValue($"\"{desired.Version}\"", isWeak: true);
        return desired;
    }

    /// <summary>Lists your bridges, with their health and how many printers each has found and watches.</summary>
    [HttpGet]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<BridgeDto>>> List(CancellationToken ct)
        => User.GetUserId() is { } userId ? Ok(await bridges.ListAsync(userId, ct)) : Unauthorized();

    /// <summary>Gets one of your bridges.</summary>
    /// <response code="404">You have no bridge with that id.</response>
    [HttpGet("{id:long}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BridgeDto>> Get(long id, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await bridges.GetAsync(userId, id, ct) is { } bridge ? bridge : NotFound();
    }

    /// <summary>Names a bridge or labels where it is. A field that is null is kept; an empty string clears it.</summary>
    /// <response code="400">A field is over 100 characters or has control characters (`code`: `invalid_request`).</response>
    /// <response code="404">You have no bridge with that id.</response>
    [HttpPatch("{id:long}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BridgeDto>> Update(long id, UpdateBridgeDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!BridgeRules.TryText(body.Name, out _, Bridge.NameMaxLength) || !BridgeRules.TryText(body.Location, out _, Bridge.LocationMaxLength))
        {
            return CodedProblem(StatusCodes.Status400BadRequest, "name and location: up to 100 characters, no control characters.", "invalid_request");
        }

        if (!await bridges.UpdateAsync(userId, id, body.Name, body.Location, ct))
        {
            return NotFound();
        }

        return (await bridges.GetAsync(userId, id, ct))!;
    }

    /// <summary>
    /// Lists the printers a bridge found on its network, as of its last check-in, with where each
    /// stands: new (a suggestion), connected, in the bridge's config file, or ignored.
    /// </summary>
    /// <response code="404">You have no bridge with that id.</response>
    [HttpGet("{id:long}/discovered-printers")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DiscoveredPrinterDto>>> ListDiscovered(long id, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await bridges.ListDiscoveredAsync(userId, id, ct) is { } list ? Ok(list) : NotFound();
    }

    /// <summary>Stops suggesting a discovered printer on this bridge, including when it drops out and comes back.</summary>
    /// <response code="204">Ignored, or already was.</response>
    /// <response code="404">You have no such bridge, or it found no printer with that id.</response>
    /// <response code="409">The bridge has 200 ignored printers (`code`: `ignore_limit`).</response>
    [HttpPost("{id:long}/discovered-printers/{discoveredId:long}/ignore")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Ignore(long id, long discoveredId, CancellationToken ct) => SetIgnored(id, discoveredId, true, ct);

    /// <summary>Suggests an ignored printer again.</summary>
    /// <response code="204">No longer ignored.</response>
    /// <response code="404">You have no such bridge, or it found no printer with that id.</response>
    [HttpDelete("{id:long}/discovered-printers/{discoveredId:long}/ignore")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Unignore(long id, long discoveredId, CancellationToken ct) => SetIgnored(id, discoveredId, false, ct);

    private async Task<IActionResult> SetIgnored(long id, long discoveredId, bool ignored, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        try
        {
            return await bridges.SetIgnoredAsync(userId, id, discoveredId, ignored, ct) ? NoContent() : NotFound();
        }
        catch (BridgeProblemException problem)
        {
            return CodedProblem(problem.Status, problem.Message, problem.Code, problem.BridgeId);
        }
    }

    /// <summary>Lists the printers you connected to a bridge: its desired printers.</summary>
    /// <response code="404">You have no bridge with that id.</response>
    [HttpGet("{id:long}/printers")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<BridgePrinterDto>>> ListPrinters(long id, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await bridges.ListPrintersAsync(userId, id, ct) is { } list ? Ok(list) : NotFound();
    }

    /// <summary>
    /// Connects a printer to a bridge: from what it found (`discoveredPrinterId`), or by hand
    /// (`kind` and `host`). The bridge starts watching it at its next check-in or poll, with no
    /// restart. Its prints are logged to `printerId`; to log to a new printer, create it with
    /// `POST /api/Printers` first.
    /// </summary>
    /// <remarks>
    /// No LAN credential is sent here. A printer that needs one (a Bambu Lab access code, a
    /// PrusaLink password, an OctoPrint or Moonraker API key) gets it on the bridge until
    /// credentials can be encrypted to the bridge's own key.
    /// </remarks>
    /// <response code="201">Connected.</response>
    /// <response code="400">`code`: `printer_not_found`, `discovered_printer_not_found`, `invalid_kind`, `invalid_host`, `invalid_port`, `invalid_serial` or `serial_required`.</response>
    /// <response code="404">You have no bridge with that id.</response>
    /// <response code="409">`code`: `printer_already_connected`, `discovered_printer_already_connected` (the same printer or address is already on this bridge), `printer_connected_elsewhere` (another of your bridges watches it; `bridgeId` names it), `printer_configured_on_bridge` (a bridge watches it from its config file; `bridgeId` names it) or `bridge_printer_limit` (50).</response>
    [HttpPost("{id:long}/printers")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BridgePrinterDto>> Connect(long id, ConnectBridgePrinterDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        BridgePrinterDto? entry;
        try
        {
            entry = await bridges.ConnectAsync(userId, id, body, ct);
        }
        catch (BridgeProblemException problem)
        {
            return CodedProblem(problem.Status, problem.Message, problem.Code, problem.BridgeId);
        }

        if (entry is null)
        {
            return NotFound();
        }

        telemetry.TrackEvent("BridgePrinterConnected", new Dictionary<string, string>
        {
            ["GrantId"] = Id(id),
            ["Kind"] = entry.Kind ?? string.Empty,
            ["FromDiscovery"] = (body.DiscoveredPrinterId is not null).ToString(),
        });
        return StatusCode(StatusCodes.Status201Created, entry);
    }

    /// <summary>
    /// Disconnects a printer from a bridge; it stops watching at its next check-in or poll. The
    /// printer's connection and prints are kept.
    /// </summary>
    /// <response code="204">Disconnected.</response>
    /// <response code="404">You have no such bridge, or it has no such printer.</response>
    [HttpDelete("{id:long}/printers/{entryId:long}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(long id, long entryId, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!await bridges.DisconnectAsync(userId, id, entryId, ct))
        {
            return NotFound();
        }

        telemetry.TrackEvent("BridgePrinterDisconnected", new Dictionary<string, string> { ["GrantId"] = Id(id) });
        return NoContent();
    }

    /// <summary>
    /// Asks a bridge to sweep its networks for printers now. It sees the request at its next
    /// check-in or poll. A second request within a minute is the same request.
    /// </summary>
    /// <response code="202">Requested.</response>
    /// <response code="404">You have no bridge with that id.</response>
    [HttpPost("{id:long}/scan")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BridgeScanDto>> Scan(long id, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await bridges.RequestScanAsync(userId, id, ct) is { } requestedAt
            ? StatusCode(StatusCodes.Status202Accepted, new BridgeScanDto { ScanRequestedAt = requestedAt })
            : NotFound();
    }

    private static string Id(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private ObjectResult DeviceTokenRequired() => CodedProblem(StatusCodes.Status403Forbidden,
        "Only a paired bridge's device token can check in.", "device_token_required");

    private ObjectResult NotRegistered() => CodedProblem(StatusCodes.Status404NotFound,
        "This bridge has not checked in yet: PUT /api/bridges/me first.", "bridge_not_registered");

    private ObjectResult CodedProblem(int status, string detail, string code, long? bridgeId = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        if (bridgeId is { } other)
        {
            problem.Extensions["bridgeId"] = other;
        }
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
