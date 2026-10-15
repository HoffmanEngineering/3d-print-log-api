using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Middleware;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Services;
using PrintLogApi.Services.AgentReleases;

namespace PrintLogApi.Controllers;

/// <summary>
/// The connectors attached to your printers (the printlog-bridge, plugins) and whether they are
/// still alive.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ConnectionsController(
    IConnectionService connectionService,
    IAgentReleaseCatalog agentReleases,
    TimeProvider clock,
    TelemetryClient telemetry) : ControllerBase
{
    private const int MaxInstanceIdLength = 100;

    /// <summary>Lists your connections.</summary>
    /// <param name="printerId">Only the connections bound to this printer.</param>
    /// <response code="200">Your connections, each with its status: stale after 15 minutes without a heartbeat.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<ConnectionDto>>> GetConnections([FromQuery] long? printerId)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var connections = await connectionService.List(userId, printerId);
        var latest = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kind in connections.Select(c => c.Kind).Distinct())
        {
            latest[kind] = await agentReleases.GetLatestVersionAsync(kind, HttpContext.RequestAborted);
        }
        return Ok(connections.Select(c => ToDto(c, latest[c.Kind])).ToList());
    }

    /// <summary>Gets one of your connections by the agent's instance id.</summary>
    /// <param name="instanceId">The agent's own stable id for the printer.</param>
    /// <response code="200">The connection.</response>
    /// <response code="404">Returned when you have no connection with that instance id.</response>
    [HttpGet("{instanceId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectionDto>> GetConnection(string instanceId)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await connectionService.Get(userId, instanceId) is { } connection
            ? Ok(await ToDtoAsync(connection))
            : NotFound();
    }

    /// <summary>Registers a connection, or sends its heartbeat.</summary>
    /// <remarks>
    /// Agents call this when they start and every 5 minutes. `instanceId` is the agent's own
    /// stable id for one printer (a UUID it stores locally), so a bridge watching three printers
    /// registers three connections. Each call replaces `kind`, `displayName`, `agentVersion` and
    /// `printerId`: send the printer the user chose at pairing every time, because leaving it out
    /// unbinds the connection. The response's `latestAgentVersion` and `updateAvailable` tell the
    /// agent when a newer release of it is out, so it never has to ask anyone else.
    /// </remarks>
    /// <param name="instanceId">The agent's own stable id for the printer, at most 100 characters.</param>
    /// <param name="dto">The connection's details.</param>
    /// <response code="200">The connection existed and was refreshed.</response>
    /// <response code="201">The connection was registered.</response>
    /// <response code="400">Returned when a field is missing or too long, or the printer is not yours.</response>
    [HttpPut("{instanceId}")]
    [ExemptFromConnectorWriteBudget]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ConnectionDto>> PutConnection(string instanceId, PutConnectionDto dto)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        instanceId = instanceId.Trim();
        if (instanceId.Length is 0 or > MaxInstanceIdLength)
        {
            return BadRequest($"instanceId must be 1 to {MaxInstanceIdLength} characters.");
        }

        try
        {
            var (connection, created) = await connectionService.Upsert(userId, instanceId, dto);
            var body = await ToDtoAsync(connection);
            if (!created)
            {
                return Ok(body);
            }

            telemetry.TrackEvent("ConnectionRegistered", new Dictionary<string, string> { ["Kind"] = connection.Kind });
            return CreatedAtAction(nameof(GetConnection), new { instanceId = connection.InstanceId }, body);
        }
        catch (UserCannotAccessPrinterException)
        {
            return BadRequest("The printer does not exist or is not yours.");
        }
    }

    /// <summary>Deletes one of your connections. The prints it logged are kept.</summary>
    /// <param name="instanceId">The agent's own stable id for the printer.</param>
    /// <response code="204">The connection was deleted.</response>
    /// <response code="404">Returned when you have no connection with that instance id.</response>
    [HttpDelete("{instanceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteConnection(string instanceId)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!await connectionService.Delete(userId, instanceId))
        {
            return NotFound();
        }

        telemetry.TrackEvent("ConnectionDeleted");
        return NoContent();
    }

    /// <summary>Dismisses the suggestion to remove the Moonraker notifier, for good.</summary>
    /// <remarks>
    /// The suggestion shows while `showNotifierNotice` is true: notifier events for this
    /// connection's printer were dropped because the connection logs the same jobs. Dismissing it
    /// keeps the count.
    /// </remarks>
    /// <param name="instanceId">The agent's own stable id for the printer.</param>
    /// <response code="204">The notice was dismissed, or already had been.</response>
    /// <response code="404">Returned when you have no connection with that instance id.</response>
    [HttpPost("{instanceId}/notifier-notice/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DismissNotifierNotice(string instanceId)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        return await connectionService.DismissNotifierNotice(userId, instanceId) ? NoContent() : NotFound();
    }

    private static DateTimeOffset? Utc(DateTime? value)
        => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private async Task<ConnectionDto> ToDtoAsync(Connection connection)
        => ToDto(connection, await agentReleases.GetLatestVersionAsync(connection.Kind, HttpContext.RequestAborted));

    private ConnectionDto ToDto(Connection connection, string? latestAgentVersion)
    {
        // Stored as UTC, but SQLite hands it back as Unspecified.
        var lastSeen = new DateTimeOffset(DateTime.SpecifyKind(connection.LastSeenAt, DateTimeKind.Utc));
        return new ConnectionDto
        {
            Id = connection.Id,
            Kind = connection.Kind,
            InstanceId = connection.InstanceId,
            DisplayName = connection.DisplayName,
            AgentVersion = connection.AgentVersion,
            LatestAgentVersion = latestAgentVersion,
            UpdateAvailable = AgentVersion.IsOlder(connection.AgentVersion, latestAgentVersion),
            PrinterId = connection.PrinterId,
            CreatedDate = new DateTimeOffset(DateTime.SpecifyKind(connection.CreatedDate, DateTimeKind.Utc)),
            LastSeenAt = lastSeen,
            Status = clock.GetUtcNow() - lastSeen >= Connection.StaleAfter ? ConnectionStatus.Stale : ConnectionStatus.Online,
            DroppedNotifierEventCount = connection.DroppedNotifierEventCount,
            LastDroppedNotifierEventAt = Utc(connection.LastDroppedNotifierEventAt),
            NotifierNoticeDismissedAt = Utc(connection.NotifierNoticeDismissedAt),
            ShowNotifierNotice = connection.DroppedNotifierEventCount > 0 && connection.NotifierNoticeDismissedAt is null,
        };
    }
}
