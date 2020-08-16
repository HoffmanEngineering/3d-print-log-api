using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
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

        public CommentsController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        // GET: api/Comments/5
        [HttpGet("{id}")]
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

        // PUT: api/Comments/5
        // To protect from overposting attacks, enable the specific properties you want to bind to, for
        // more details, see https://go.microsoft.com/fwlink/?linkid=2123754.
        [HttpPut("{id}")]
        public async Task<IActionResult> PutComment(long id, Comment comment)
        {
            if (id != comment.Id)
            {
                return BadRequest();
            }

            _context.Entry(comment).State = EntityState.Modified;

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

            return NoContent();
        }


        private bool CommentExists(long id)
        {
            return _context.Comments.Any(e => e.Id == id);
        }
    }
}
