using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PrintsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public PrintsController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        // GET: api/Prints
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Print>>> GetPrints()
        {
            return await _context.Prints.ToListAsync();
        }

        // GET: api/Prints/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Print>> GetPrint(long id)
        {
            var print = await _context.Prints.FindAsync(id);

            if (print == null)
            {
                return NotFound();
            }

            return print;
        }

        // PUT: api/Prints/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutPrint(long id, Print print)
        {
            if (id != print.Id)
            {
                return BadRequest();
            }

            _context.Entry(print).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrintExists(id))
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

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult<Print>> PostPrint(AddPrintDTO print)
        {
            Print newPrint = _mapper.Map<Print>(print);

            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            newPrint.CreatedById = userId;
            newPrint.UpdatedById = userId;


            _context.Prints.Add(newPrint);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetPrint", new { id = newPrint.Id }, newPrint);
        }

        // DELETE: api/Prints/5
        [HttpDelete("{id}")]
        public async Task<ActionResult<Print>> DeletePrint(long id)
        {
            var print = await _context.Prints.FindAsync(id);
            if (print == null)
            {
                return NotFound();
            }

            _context.Prints.Remove(print);
            await _context.SaveChangesAsync();

            return print;
        }

        private bool PrintExists(long id)
        {
            return _context.Prints.Any(e => e.Id == id);
        }
    }
}
