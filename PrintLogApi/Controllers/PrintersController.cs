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
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PrintersController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public PrintersController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }


        /// <summary>
        /// Get Print Summaries for current user
        /// </summary>
        /// <returns></returns>
        [HttpGet("summary")]
        public async Task<ActionResult<IEnumerable<PrinterSummary>>> GetPrintSummary([FromQuery] PagedRequest pagingRequest, [FromQuery] string searchText, [FromQuery] bool includeInactive = false)
        {
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var printers = _context.Printers
                .Where(p => p.UserId == userId);

            if (!includeInactive)
            {
                printers = printers.Where(p => p.IsActive == true);
            }
            
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                printers = printers.Where(p => p.Name.Contains(searchText) || p.Make.Contains(searchText) || p.Model.Contains(searchText));
            }

            var result = printers.OrderByDescending(p => p.Name).OrderByDescending(p => p.Make).ThenByDescending(p => p.Model)
                .ProjectTo<PrinterSummary>(_mapper.ConfigurationProvider);

            var response = await PagedList<PrinterSummary>.CreateAsync(result, pagingRequest.PageNumber, pagingRequest.PageSize);

            return Ok(response);
        }

        // GET: api/Printers/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Printer>> GetPrinter(long id)
        {
            var printer = await _context.Printers.FindAsync(id);

            if (printer == null)
            {
                return NotFound();
            }
            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);
            if (printer.UserId != userId)
            {
                return Forbid();
            }

            return printer;
        }

        // PUT: api/Printers/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutPrinter(long id, AddPrinterDTO printer)
        {
            if (id != printer.Id)
            {
                return BadRequest();
            }


             Printer existingPrinter = await _context.Printers.FindAsync(id);

            if (existingPrinter == null)
            {

                return NotFound();
            }

            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            if (existingPrinter.UserId != userId)
            {
                return Forbid();
            }

            existingPrinter = _mapper.Map<AddPrinterDTO, Printer>(printer, existingPrinter);

            _context.Entry(existingPrinter).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrinterExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return CreatedAtAction("GetPrinter", new { id = existingPrinter.Id }, existingPrinter);
        }

        // POST: api/Printers
        [HttpPost]
        public async Task<ActionResult<Printer>> PostPrinter(AddPrinterDTO printer)
        {
            Printer newPrinter = _mapper.Map<Printer>(printer);

            var userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            newPrinter.UserId = userId;

            _context.Printers.Add(newPrinter);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetPrinter", new { id = newPrinter.Id }, _mapper.Map<Printer>(newPrinter));
        }

        // TODO: Make the delete be a soft-inactive delete.
        //// DELETE: api/Printers/5
        //[HttpDelete("{id}")]
        //public async Task<ActionResult<Printer>> DeletePrinter(long id)
        //{
        //    var printer = await _context.Printers.FindAsync(id);
        //    if (printer == null)
        //    {
        //        return NotFound();
        //    }

        //    _context.Printers.Remove(printer);
        //    await _context.SaveChangesAsync();

        //    return printer;
        //}

        private bool PrinterExists(long id)
        {
            return _context.Printers.Any(e => e.Id == id);
        }
    }
}
