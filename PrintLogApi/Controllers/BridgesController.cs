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

        if (result.KeyChange is { } change)
        {
            TrackKeyChange(grantId, change, result.KeyChangesToday);
        }

        List<RejectedEntryDto> rejected = [.. report.Rejected, .. result.Rejected];
        if (rejected.Count > 0)
        {
            telemetry.TrackEvent("BridgeCheckInEntriesRejected", new Dictionary<string, string>
            {
                ["GrantId"] = Id(grantId),
                ["Codes"] = string.Join(' ', rejected.Select(r => r.Code).Distinct().Order(StringComparer.Ordinal)),
            });
        }

        return new BridgeCheckInResponseDto
        {
            BridgeId = grantId,
            CheckInSeconds = CheckInSeconds,
            DesiredPollSeconds = DesiredPollSeconds,
            Desired = result.Desired,
            Rejected = rejected,
            PublicKeyFingerprint = result.PublicKeyFingerprint,
            PendingPublicKeyFingerprint = result.PendingPublicKeyFingerprint,
        };
    }

    /// <summary>
    /// One event per key change (<c>first</c>, <c>pending</c>, <c>confirmed</c>) with the bridge's
    /// count for the UTC day, so a bridge whose key flips is visible. Never the key itself, nor
    /// anything sealed.
    /// </summary>
    private void TrackKeyChange(long grantId, string change, int changesToday)
        => telemetry.TrackEvent("BridgeKeyChanged", new Dictionary<string, string>
        {
            ["GrantId"] = Id(grantId),
            ["Change"] = change,
            ["ChangesToday"] = changesToday.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

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
    /// PrusaLink password, an OctoPrint or Moonraker API key) gets it sealed to the bridge's key
    /// with `PUT /api/bridges/{id}/printers/{entryId}/secrets/{field}` once connected.
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
    /// Moves a connected printer to another of your bridges: this bridge stops watching it and the
    /// other starts, each at its next check-in or poll, with no restart. The entry keeps its id;
    /// prints already logged keep their connection.
    /// </summary>
    /// <remarks>
    /// Give the printer as the other bridge found it (`discoveredPrinterId`), or the address the
    /// other bridge reaches it at (`host`, `port`); with neither it keeps its address.
    ///
    /// Credentials move in the same save. Values sealed to this bridge's key can't be opened by the
    /// new bridge, so send `secrets` (`{field: sealed}`), each sealed in the browser to the new
    /// bridge's current key with associated data
    /// `printlog-credential:v1:{toBridgeId}:{entryId}:{field}`, and that key's `keyFingerprint`.
    /// They are checked exactly as `PUT .../secrets/{field}` checks a seal. A credential the printer
    /// can't connect without that was set must be sent (`secrets_required`, with `fields`). Others
    /// not sent are deleted and named in `droppedSecretFields`.
    ///
    /// A job already running when the printer moves stays one print: the old bridge may still log
    /// it, and the new bridge picking it up is handed the same print.
    /// </remarks>
    /// <response code="200">Moved.</response>
    /// <response code="400">`code`: `invalid_target_bridge` (missing, this bridge, or not yours), `discovered_printer_not_found` (the new bridge found no such printer), `invalid_kind`, `invalid_host`, `invalid_port`, `invalid_serial`, `serial_required`, `secrets_required` (`fields` names the credentials to send), `invalid_secret_field`, `invalid_sealed_value` or `invalid_key_fingerprint`.</response>
    /// <response code="404">You have no such bridge, or no such printer entry.</response>
    /// <response code="409">`code`: `printer_moved` (it is on another bridge already; `bridgeId` names it), `printer_configured_on_bridge` (this bridge or the new one watches it from its config file; `bridgeId` names it), `discovered_printer_already_connected` (the new bridge already has that printer or address), `printer_connected_elsewhere`, `bridge_printer_limit` (50), or for `secrets` `bridge_key_missing`, `bridge_key_unconfirmed` or `bridge_key_changed` (with `keyFingerprint`).</response>
    [HttpPost("{id:long}/printers/{entryId:long}/move")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MovedBridgePrinterDto>> Move(long id, long entryId, MoveBridgePrinterDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        MovedBridgePrinterDto? moved;
        try
        {
            moved = await bridges.MoveAsync(userId, id, entryId, body, ct);
        }
        catch (BridgeProblemException problem)
        {
            var result = CodedProblem(problem.Status, problem.Message, problem.Code, problem.BridgeId, problem.KeyFingerprint);
            if (problem.Fields is { } fields && result.Value is ProblemDetails details)
            {
                details.Extensions["fields"] = fields;
            }

            return result;
        }

        if (moved is null)
        {
            return NotFound();
        }

        telemetry.TrackEvent("BridgePrinterMoved", new Dictionary<string, string>
        {
            ["GrantId"] = Id(id),
            ["ToGrantId"] = Id(moved.ToBridgeId),
            ["Kind"] = moved.Printer?.Kind ?? string.Empty,
            ["FromDiscovery"] = (body.DiscoveredPrinterId is not null).ToString(),
            ["DroppedSecrets"] = (moved.DroppedSecretFields?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return moved;
    }

    /// <summary>
    /// Sets or replaces one LAN credential of a connected printer, sealed in the browser to the
    /// bridge's public key (docs/bridge-credential-sealing.md). The API stores the ciphertext and
    /// can't read it; only the bridge gets it back, at its next check-in or poll.
    /// </summary>
    /// <remarks>
    /// Seal with the bridge's `publicKey` (from `GET /api/bridges/{id}`), associated data
    /// `printlog-credential:v1:{id}:{entryId}:{field}`, and send the `publicKeyFingerprint` you
    /// sealed to. `field` is one of the entry's `secretFields`. The response never contains the
    /// sealed value.
    /// </remarks>
    /// <response code="200">Stored. The body is the entry, with its secrets' fields, fingerprints and times.</response>
    /// <response code="400">`code`: `invalid_secret_field`, `invalid_sealed_value` or `invalid_key_fingerprint`.</response>
    /// <response code="404">You have no such bridge, or it has no such printer.</response>
    /// <response code="409">`code`: `bridge_key_missing` (the bridge has reported no key; it needs updating), `bridge_key_unconfirmed` (sealed to the bridge's pending key: confirm it first) or `bridge_key_changed` (not the current key: reload the bridge, compare the fingerprint and seal again).</response>
    [HttpPut("{id:long}/printers/{entryId:long}/secrets/{field}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BridgePrinterDto>> SetSecret(long id, long entryId, string field, SetBridgePrinterSecretDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        BridgePrinterDto? entry;
        try
        {
            entry = await bridges.SetSecretAsync(userId, id, entryId, field, body, ct);
        }
        catch (BridgeProblemException problem)
        {
            return CodedProblem(problem.Status, problem.Message, problem.Code, problem.BridgeId, problem.KeyFingerprint);
        }

        if (entry is null)
        {
            return NotFound();
        }

        telemetry.TrackEvent("BridgePrinterSecretSet", new Dictionary<string, string>
        {
            ["GrantId"] = Id(id),
            ["Kind"] = entry.Kind ?? string.Empty,
            ["Field"] = field,
        });
        return entry;
    }

    /// <summary>Clears one sealed LAN credential of a connected printer. The bridge drops it at its next check-in or poll.</summary>
    /// <response code="204">Cleared, or there was none.</response>
    /// <response code="400">`code`: `invalid_secret_field`.</response>
    /// <response code="404">You have no such bridge, or it has no such printer.</response>
    [HttpDelete("{id:long}/printers/{entryId:long}/secrets/{field}")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearSecret(long id, long entryId, string field, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        try
        {
            if (!await bridges.ClearSecretAsync(userId, id, entryId, field, ct))
            {
                return NotFound();
            }
        }
        catch (BridgeProblemException problem)
        {
            return CodedProblem(problem.Status, problem.Message, problem.Code);
        }

        telemetry.TrackEvent("BridgePrinterSecretCleared", new Dictionary<string, string> { ["GrantId"] = Id(id), ["Field"] = field });
        return NoContent();
    }

    /// <summary>
    /// Confirms the new encryption key a bridge reported, after you compared its fingerprint with
    /// the one the bridge shows. Values sealed to the old key become stale and need sealing again.
    /// </summary>
    /// <remarks>
    /// A bridge's first key is taken as is; a different one later is held as `pendingPublicKey`
    /// until this call, because a stolen device token could report its own key. Send the
    /// fingerprint you computed from `pendingPublicKey` (lower-case hex SHA-256 of its SPKI bytes),
    /// not the API's `pendingPublicKeyFingerprint`.
    /// </remarks>
    /// <response code="200">Confirmed. The body is the bridge.</response>
    /// <response code="400">`code`: `invalid_key_fingerprint`.</response>
    /// <response code="404">You have no bridge with that id.</response>
    /// <response code="409">`code`: `no_pending_key`, or `pending_key_changed` (the fingerprint isn't the pending key's: the bridge may have reported another; reload and compare again).</response>
    [HttpPost("{id:long}/public-key/confirm")]
    [Authorize(Policy = "InteractiveUserOnly")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BridgeDto>> ConfirmKey(long id, ConfirmBridgeKeyDto body, CancellationToken ct)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        BridgeKeyConfirmation? confirmed;
        try
        {
            confirmed = await bridges.ConfirmPendingKeyAsync(userId, id, body.Fingerprint, ct);
        }
        catch (BridgeProblemException problem)
        {
            return CodedProblem(problem.Status, problem.Message, problem.Code);
        }

        if (confirmed is null)
        {
            return NotFound();
        }

        TrackKeyChange(id, "confirmed", confirmed.KeyChangesToday);
        return confirmed.Bridge;
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

    private ObjectResult CodedProblem(int status, string detail, string code, long? bridgeId = null, string? keyFingerprint = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        if (bridgeId is { } other)
        {
            problem.Extensions["bridgeId"] = other;
        }

        if (keyFingerprint is not null)
        {
            problem.Extensions["keyFingerprint"] = keyFingerprint;
        }
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
