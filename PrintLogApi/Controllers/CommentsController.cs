using System.Globalization;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Editing and removing existing comments. Comments cannot be created by themselves, they need to be created 
/// attached to another resource (ie, to add a comment on a Print, use the POST api/Prints/{printid}/comment).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class CommentsController(
    PrintLogContext context,
    IMapper mapper,
    TelemetryClient telemetry,
    ICommentService commentService) : ControllerBase
{
    /// <summary>
    /// Edit a comment.
    /// </summary>
    /// <param name="id">The Comment Id to edit</param>
    /// <param name="edittedComment">The DTO containing the edited information.</param>
    /// <response code="201">An 201 Created if the edit was successful.</response>
    /// <response code="403">Returned if the user is not the owner of the comment.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommentDetailDto>> PutComment([FromRoute] long id, [FromBody] EditCommentDto edittedComment)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingComment = await context.Comments.FindAsync(id);

        if (existingComment == null)
        {
            return NotFound();
        }

        if (userId != existingComment.CreatedById)
        {
            return Forbid();
        }

        existingComment.Body = edittedComment.Body;

        context.Entry(existingComment).State = EntityState.Modified;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!CommentExists(id))
            {
                return NotFound();
            }
            else
            {
                throw;
            }
        }

        telemetry.TrackEvent("CommentEdit");

        return Ok(mapper.Map<CommentDetailDto>(existingComment));
    }

    /// <summary>
    /// Delete a comment by comment id.
    /// </summary>
    /// <param name="id">The comment ID to delete</param>
    /// <response code="200">An OK if the delete was successful.</response>
    /// <response code="403">Returned if the user is not the owner of the comment.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteComment([FromRoute] long id)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingComment = await context.Comments.FindAsync(id);

        if (existingComment == null)
        {
            return NotFound();
        }

        if (userId != existingComment.CreatedById)
        {
            return Forbid();
        }

        await commentService.DeleteCommentById(id);

        var properties = new Dictionary<string, string> {
            { "CommentId", existingComment.Id.ToString() },
            { "UserId", userId.ToString()! },
            { "CommentCreated", existingComment.CreatedDate.ToString("O", CultureInfo.InvariantCulture) }
        };
        telemetry.TrackEvent("CommentDelete", properties);

        return Ok();
    }


    private bool CommentExists(long id)
    {
        return context.Comments.Any(e => e.Id == id);
    }
}
