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
        public async Task<ActionResult<PagedList<PrintSummaryDTO>>> GetPrintSummary([FromQuery] PagedRequest pagingRequest)
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var prints = _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .OrderByDescending(p => p.StartDate).ThenByDescending(p => p.CreatedDate)
                .ProjectTo<PrintSummaryDTO>(_mapper.ConfigurationProvider);

            var response = await PagedList<PrintSummaryDTO>.CreateAsync(prints, pagingRequest.PageNumber, pagingRequest.PageSize);
            return Ok(response);
        }

        /// <summary>
        /// Get Print Statistics
        /// </summary>
        /// <returns></returns>
        [HttpGet("statistics")]
        public async Task<ActionResult<object>> GetPrintStatistics([FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var baseQuery = _context.Prints
                .Where(p => p.CreatedById == userId || p.Printer.UserId == userId)
                .Where(p => p.StartDate >= fromDate && p.StartDate <= toDate);

            var numberOfPrints = await baseQuery.CountAsync();
            var groupByStatus = await baseQuery
                .GroupBy(p => p.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToListAsync();

            var estimatedPrintTime = await baseQuery
                .Where(p => p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => p.EstimatedPrintTimeInSeconds)
                .SumAsync();
            var totalPrintTime = await baseQuery
                .Where(p => p.PrintTimeInSeconds.HasValue)
                .Select(p => p.PrintTimeInSeconds)
                .SumAsync();

            var estimatedFilamentUsage = await baseQuery
                .Where(p => p.EstimatedFilamentUsageMg.HasValue)
                .Select(p => p.EstimatedFilamentUsageMg)
                .SumAsync();
            var totalFilamentUsage = await baseQuery
                .Where(p => p.FilamentUsageMg.HasValue)
                .Select(p => p.FilamentUsageMg)
                .SumAsync();

            var printTimeForPrinters = await baseQuery
                .Where(p => p.PrintTimeInSeconds.HasValue || p.EstimatedPrintTimeInSeconds.HasValue)
                .Select(p => new { printerId = p.PrinterId, printTime = p.PrintTimeInSeconds.HasValue ? p.PrintTimeInSeconds : p.EstimatedPrintTimeInSeconds })
                .GroupBy(p => p.printerId)
                .Select(group => new
                {
                    printerId = group.Key,
                    printTime = group.Sum(p => p.printTime)
                })
                .ToListAsync();

            return Ok(new { numberOfPrints, groupByStatus, estimatedPrintTime, totalPrintTime, estimatedFilamentUsage, totalFilamentUsage, printTimeForPrinters });
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

            if (existingPrint == null)
            {
                return NotFound();
            }


            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            if (userId != existingPrint.CreatedById)
            {
                return Forbid();
            }

            existingPrint = _mapper.Map<PrintDetailDTO, Print>(printDTO, existingPrint);

            var printer = await _context.Printers.FindAsync(printDTO.PrinterId);
            existingPrint.Printer = printer;

            // Set UpdatedByIds
            
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
