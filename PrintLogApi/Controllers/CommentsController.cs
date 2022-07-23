using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Extensions;
using PrintLogApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Globalization;
using System.Collections.Generic;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Editing and removing existing comments. Comments cannot be created by themselves, they need to be created 
    /// attached to another resource (ie, to add a comment on a Print, use the POST api/Prints/{printid}/comment).
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CommentsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly ICommentService _commentService;

        public CommentsController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, ICommentService commentService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _commentService = commentService;
        }

        // TODO: Figure out authorization, as you'd want to make sure someone has the right permission to view this comment when requesting standalone.
        //// GET: api/Comments/5
        //[HttpGet("{id}", Name = "GetComment")]
        //public async Task<ActionResult<CommentDetailDto>> GetComment(long id)
        //{
        //    var comment = await _commentService.GetCommentDetailById(id);

        //    if (comment == null)
        //    {
        //        return NotFound();
        //    }

        //    return comment;
        //}

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
            if(!userId.HasValue)
            {
                return Unauthorized();
            }

            var existingComment = await _context.Comments.FindAsync(id);

            if (existingComment == null)
            {
                return NotFound();
            }

            if (userId != existingComment.CreatedById)
            {
                return StatusCode(403, "Cannot edit another user's comment");
            }

            existingComment.Body = edittedComment.Body;

            _context.Entry(existingComment).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
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

            _telemetry.TrackEvent("CommentEdit");

            return CreatedAtAction("GetComment", new { id = existingComment.Id }, _mapper.Map<CommentDetailDto>(existingComment));
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

            var existingComment = await _context.Comments.FindAsync(id);

            if (existingComment == null)
            {
                return NotFound();
            }

            if (userId != existingComment.CreatedById)
            {
                return Forbid();
            }

            await _commentService.DeleteCommentById(id);

            var properties = new Dictionary<string, string> {
                { "CommentId", existingComment.Id.ToString() },
                { "UserId", userId.ToString() },
                { "CommentCreated", existingComment.CreatedDate.ToString("O", CultureInfo.InvariantCulture) }
            };
            _telemetry.TrackEvent("CommentDelete", properties);

            return Ok();
        }


        private bool CommentExists(long id)
        {
            return _context.Comments.Any(e => e.Id == id);
        }
    }
}
