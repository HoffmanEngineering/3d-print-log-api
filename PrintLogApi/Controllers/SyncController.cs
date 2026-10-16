using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Sync;
using PrintLogApi.Services;
using PrintLogApi.Services.Sync;

namespace PrintLogApi.Controllers;

/// <summary>
/// Cheap polling for integrations such as Home Assistant (#234): a change check and one
/// aggregated snapshot. Plain <c>[Authorize]</c>, so interactive sign-ins and connector
/// credentials (API keys) both work; both actions are reads, so the connector write budget does
/// not apply.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class SyncController(
    ICacheVersionService cacheVersionService,
    ISyncSnapshotService snapshotService,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Check whether anything changed, without reading any data.
    /// </summary>
    /// <remarks>
    /// Returns an opaque `version` that changes whenever printers, prints, spools, loaded
    /// filament or settings change. The same value is the response's (weak) `ETag`; send it back
    /// in `If-None-Match` to get a `304 Not Modified` with no body. Poll this about once a minute
    /// and fetch `GET /api/sync/snapshot` only when it changes. A version can also change
    /// without a data change (a server restart), which costs one extra snapshot fetch.
    /// </remarks>
    /// <response code="200">Returned with the current version.</response>
    /// <response code="304">Returned when `If-None-Match` matches the current version.</response>
    /// <response code="401">Returned if the caller is not authenticated.</response>
    [HttpGet("version")]
    [ProducesResponseType(typeof(SyncVersionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<SyncVersionResponse> GetVersion()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var version = SyncValidators.Version(userId.Value, cacheVersionService.GetUserCacheVersion(userId.Value));
        if (NotModified(new EntityTagHeaderValue($"\"{version}\"", isWeak: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(new SyncVersionResponse(version));
    }

    /// <summary>
    /// Everything a home-automation client renders, in one response.
    /// </summary>
    /// <remarks>
    /// Printers (with connection health, the current print and the last finished print), what is
    /// loaded in each slot, every spool with its remaining and used grams, and this month's
    /// totals. `schemaVersion` versions the shape: new fields can appear without a bump, so
    /// ignore fields you do not know. `remainingGrams` is null for a spool with no initial
    /// weight. Send the response's `ETag` back in `If-None-Match` to get a `304 Not Modified`.
    /// Fetch this when `GET /api/sync/version` changes, and conditionally every few minutes so
    /// connection health (stale after 15 minutes without a heartbeat) stays current.
    /// </remarks>
    /// <param name="timeZone">IANA (or Windows) time zone the month totals are counted in. Defaults to UTC.</param>
    /// <response code="200">Returned with the snapshot.</response>
    /// <response code="304">Returned when `If-None-Match` matches the current snapshot.</response>
    /// <response code="400">Returned if `timeZone` is not a recognized time zone.</response>
    /// <response code="401">Returned if the caller is not authenticated.</response>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(SyncSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SyncSnapshot>> GetSnapshot([FromQuery] string? timeZone = null)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var zoneId = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim();
        if (!TimeZoneResolver.TryResolve(zoneId, out var zone))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unknown time zone",
                detail: $"timeZone '{zoneId}' is not a recognized time zone identifier.");
        }

        // The clock is read once, here, and threaded down (AGENTS.md, "The clock").
        var now = clock.GetUtcNow();
        var result = await snapshotService.GetAsync(userId.Value, SyncMonth.Containing(now, zone, zoneId), now, HttpContext.RequestAborted);
        if (NotModified(new EntityTagHeaderValue($"\"{result.ETag}\"", isWeak: true)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(result.Snapshot);
    }

    /// <summary>
    /// Stamps the validator headers and reports whether the request's <c>If-None-Match</c>
    /// already holds this tag.
    /// </summary>
    /// <remarks>
    /// Tags are weak (<c>W/</c>): response compression serves the same body as identity, gzip
    /// and brotli under one tag, and a strong tag promises byte-identical representations.
    /// <c>If-None-Match</c> uses weak comparison anyway, so 304s are unaffected.
    /// </remarks>
    private bool NotModified(EntityTagHeaderValue etag)
    {
        var headers = Response.GetTypedHeaders();
        headers.ETag = etag;
        headers.CacheControl = new CacheControlHeaderValue { Private = true, NoCache = true };
        return SyncValidators.IfNoneMatchMatches(Request.GetTypedHeaders(), etag);
    }
}
