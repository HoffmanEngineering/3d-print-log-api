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

namespace PrintLogApi.Controllers
{
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


        [HttpPut("{id}")]
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
                return Forbid();
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

        [HttpDelete("{id}")]
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

            _telemetry.TrackEvent("CommentDelete");

            return Ok();
        }


        private bool CommentExists(long id)
        {
            return _context.Comments.Any(e => e.Id == id);
        }
    }
}
