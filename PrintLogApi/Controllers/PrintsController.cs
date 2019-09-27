using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
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

        /// <summary>
        /// Get Print Summaries for current user
        /// </summary>
        /// <returns></returns>
        [HttpGet("summary")]
        public async Task<ActionResult<IEnumerable<PrintSummaryDTO>>> GetPrintSummary()
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            return await _context.Prints
                .Where(p => p.CreatedById == userId || p.printer.UserId == userId)
                .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate)
                .ProjectTo<PrintSummaryDTO>(_mapper.ConfigurationProvider)
                .ToListAsync();
        }

        // GET: api/Prints/5
        [HttpGet("{id}")]
        public async Task<ActionResult<PrintDetailDTO>> GetPrint(long id)
        {
            var print = await _context.Prints.FindAsync(id);

            if (print == null)
            {
                return NotFound();
            }

            return _mapper.Map<PrintDetailDTO>(print);
        }

        // PUT: api/Prints/5
        [HttpPut("{id}")]
        public async Task<ActionResult<PrintDetailDTO>> PutPrint(long id, PrintDetailDTO printDTO)
        {
            if (id != printDTO.Id)
            {
                return BadRequest();
            }

            Print existingPrint = await _context.Prints.FindAsync(id);

            existingPrint = _mapper.Map<PrintDetailDTO, Print>(printDTO, existingPrint);

            var printer = await _context.Printers.FindAsync(printDTO.PrinterId);
            existingPrint.printer = printer;

            // Set UpdatedByIds
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            existingPrint.UpdatedById = userId;


            _context.Entry(existingPrint).State = EntityState.Modified;

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

            return CreatedAtAction("GetPrint", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(existingPrint));
        }

        // POST: api/Prints
        [HttpPost]
        public async Task<ActionResult<PrintDetailDTO>> PostPrint(AddPrintDTO print)
        {
            Print newPrint = _mapper.Map<Print>(print);

            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            newPrint.CreatedById = userId;
            newPrint.UpdatedById = userId;


            _context.Prints.Add(newPrint);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetPrint", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
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
