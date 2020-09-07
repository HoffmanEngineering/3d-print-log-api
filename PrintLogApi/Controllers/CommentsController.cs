using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CommentsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;

        public CommentsController(PrintLogContext context, IMapper mapper, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
        }

        // GET: api/Comments/5
        [HttpGet("{id}", Name = "GetComment")]
        public async Task<ActionResult<CommentDetailDto>> GetComment(long id)
        {
            var comment = await _context.Comments
                .Where(c => c.Id == id)
                .AsNoTracking()
                .ProjectTo<CommentDetailDto>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync();

            if (comment == null)
            {
                return NotFound();
            }

            return comment;
        }


        [HttpPut("{id}")]
        public async Task<ActionResult<CommentDetailDto>> PutComment([FromRoute] long id, [FromBody] EditCommentDto edittedComment)
        {
            var existingComment = await _context.Comments.FindAsync(id);

            if (existingComment == null)
            {
                return NotFound();
            }

            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

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

            _telemetry.TrackEvent("PrintEdit");

            return CreatedAtAction("GetComment", new { id = existingComment.Id }, _mapper.Map<CommentDetailDto>(existingComment));
        }


        private bool CommentExists(long id)
        {
            return _context.Comments.Any(e => e.Id == id);
        }
    }
}
