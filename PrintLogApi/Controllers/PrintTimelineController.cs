using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Timeline;
using PrintLogApi.Services;
using PrintLogApi.Services.Timeline;

namespace PrintLogApi.Controllers;

/// <summary>
/// A print's timeline (api#343): pauses, the failure reason and lifecycle steps. Only the print's
/// creator reads it; connectors report pauses here.
/// </summary>
[Route("api/Prints/{id}/timeline")]
[ApiController]
[Authorize]
public class PrintTimelineController(
    PrintLogContext context,
    IPrintService printService,
    IPrintTimeline timeline,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    /// <summary>
    /// Report when a print paused or resumed.
    /// </summary>
    /// <remarks>
    /// For connectors. Each event is `pause` or `resume` with the printer's `occurredAt` and a
    /// `key` that is unique per print and the same on every retry. The print stays Printing (`2`)
    /// while paused; `pausedSince` on the print says it is paused. Events are applied in time
    /// order, and what does not apply is ignored rather than refused: a pause while one is open or
    /// on a print that is not Printing, a resume with no open pause or earlier than it, a key already
    /// recorded, a time more than 5 minutes before the print's start or after now. Finishing the
    /// print closes an open pause. At most 20 events per request.
    /// </remarks>
    /// <param name="id">The ID of the print.</param>
    /// <param name="body">The events.</param>
    /// <response code="204">Applied (including anything ignored).</response>
    /// <response code="400">An unknown type, more than 20 events, or an event missing its time or key, or with a key over 200 characters.</response>
    /// <response code="403">The print is not yours.</response>
    /// <response code="404">The print does not exist.</response>
    [DeviceAccess(DeviceScopes.PrintsWrite)]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostTimeline(long id, PostTimelineDto body)
    {
        var editors = await printService.GetPrintEditors(id);
        if (editors is null)
        {
            return NotFound();
        }
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        if (userId != editors.CreatedById && userId != editors.PrinterOwnerId)
        {
            return Forbid();
        }

        // Validate has checked every field.
        var inputs = body.Events!
            .Select(e => new TimelineInput(
                e!.Type == "pause" ? TimelineInputType.Pause : TimelineInputType.Resume,
                e.OccurredAt!.Value,
                e.Key!.Trim()))
            .ToList();
        // As a print created with these credentials records itself (PrintsController.PostPrint).
        var source = AuthMethodClaim.IsApiKey(User) || AuthMethodClaim.IsDevice(User) ? PrintSource.ApiKey : PrintSource.Web;

        try
        {
            await timeline.ApplyAsync(id, inputs, source, HttpContext.RequestAborted);
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }

        // The printer's owner can report on a print another user created.
        cacheVersionService.InvalidateUserCache(editors.CreatedById);
        return NoContent();
    }

    /// <summary>
    /// Read a print's timeline.
    /// </summary>
    /// <remarks>
    /// Oldest first. Only the print's creator may read it, whatever the print's visibility.
    /// </remarks>
    /// <param name="id">The ID of the print.</param>
    /// <response code="200">The timeline; `events` is empty when nothing was recorded.</response>
    /// <response code="403">The print is not yours.</response>
    /// <response code="404">The print does not exist.</response>
    [DeviceAccess(DeviceScopes.PrintsRead)]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TimelineDto>> GetTimeline(long id)
    {
        var creator = await context.Prints
            .Where(p => p.Id == id)
            .Select(p => (long?)p.CreatedById)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (creator is null)
        {
            return NotFound();
        }
        if (User.GetUserId() != creator)
        {
            return Forbid();
        }

        var rows = await context.PrintTimelineEvents
            .Where(e => e.PrintId == id)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);
        // In memory: SQLite cannot order by a DateTimeOffset, and a print has at most 200 rows.
        return new TimelineDto
        {
            Events = rows.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Select(TimelineEventDto.From).ToList(),
        };
    }
}
