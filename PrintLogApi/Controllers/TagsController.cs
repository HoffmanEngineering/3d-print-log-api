using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Tag;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Print tags: your own labels for prints, with totals per tag. A print can carry several tags,
/// unlike a project, which it belongs to at most one of. Tags are private to their owner, so
/// another user's tag reads as not found.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TagsController(ITagService tagService, TelemetryClient telemetry) : ControllerBase
{
    /// <summary>List your tags, ordered by name.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<TagDto>>> GetTags(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        return await tagService.ListAsync(userId, cancellationToken);
    }

    /// <summary>List your tags with totals over the prints that carry each one.</summary>
    /// <remarks>
    /// Print time is the measured time where recorded, else the slicer estimate; filament is in
    /// milligrams, actual where recorded, else estimated. Cost is filament plus electricity in
    /// `currency`, over the prints that could be costed (`costedPrintCount`), and null when none
    /// could be.
    /// </remarks>
    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TagStatsResponse>> GetTagStats(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        return await tagService.GetStatsAsync(userId, cancellationToken);
    }

    /// <summary>Create a tag.</summary>
    /// <response code="201">The tag.</response>
    /// <response code="400">The name is blank or too long, the colour is not `#rrggbb`, or you are at the tag limit.</response>
    /// <response code="409">You already have a tag with this name, ignoring case.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TagDto>> PostTag(SaveTagDto dto, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        try
        {
            var tag = await tagService.CreateAsync(userId, dto, cancellationToken);
            telemetry.TrackEvent("TagAdded");
            return Created($"/api/Tags/{tag.Id}", tag);
        }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
        catch (TagNameConflictException ex) { return Conflict(ex.Message); }
    }

    /// <summary>Rename or recolour one of your tags.</summary>
    /// <response code="200">The updated tag.</response>
    /// <response code="404">You have no tag with this id.</response>
    /// <response code="409">Another of your tags has this name, ignoring case.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TagDto>> PutTag(Guid id, SaveTagDto dto, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        try
        {
            var tag = await tagService.UpdateAsync(userId, id, dto, cancellationToken);
            if (tag == null)
                return NotFound();

            telemetry.TrackEvent("TagEdit");
            return tag;
        }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
        catch (TagNameConflictException ex) { return Conflict(ex.Message); }
    }

    /// <summary>Delete one of your tags. Its prints keep everything but the tag.</summary>
    /// <response code="204">Deleted.</response>
    /// <response code="404">You have no tag with this id.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        if (!await tagService.DeleteAsync(userId, id, cancellationToken))
            return NotFound();

        telemetry.TrackEvent("TagDelete");
        return NoContent();
    }

    /// <summary>Replace the tags on one of your prints.</summary>
    /// <remarks>
    /// Send the complete set: tags left out are removed from the print, and an empty `tagIds`
    /// removes them all. Duplicates are ignored. At most 20 tags per print.
    /// </remarks>
    /// <response code="200">The print's tags, ordered by name.</response>
    /// <response code="400">A tag id is not one of yours, or there are too many.</response>
    /// <response code="404">You have no print with this id.</response>
    // Lives here rather than on PrintsController: it is a tag operation addressed by print, and
    // the route keeps it where a reader of /api/Prints/{id} expects it.
    [HttpPut("/api/Prints/{printId}/tags")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<TagDto>>> PutPrintTags(
        long printId, SetPrintTagsDto dto, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
            return Unauthorized();

        try
        {
            var tags = await tagService.SetPrintTagsAsync(userId, printId, dto.TagIds ?? [], cancellationToken);
            if (tags == null)
                return NotFound();

            telemetry.TrackEvent("PrintTagsSet", new Dictionary<string, string>
            {
                ["count"] = tags.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return tags;
        }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
    }
}
