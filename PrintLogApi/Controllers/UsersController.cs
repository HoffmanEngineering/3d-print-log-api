using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public UsersController(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        // GET: api/Users
        [HttpGet]
        public async Task<ActionResult<IEnumerable<User>>> GetUsers()
        {
            return await _context.Users.ToListAsync();
        }

        // GET: api/Users/5
        [HttpGet("{id}")]
        public async Task<ActionResult<User>> GetUser(long id)
        {
            var user = await _context.Users.FindAsync(id);

            if (user == null)
            {
                return NotFound();
            }

            return user;
        }

        // PUT: api/Users/5
        [HttpPut("{id}")]
        public async Task<IActionResult> PutUser(long id, User user)
        {
            if (id != user.Id)
            {
                return BadRequest();
            }

            _context.Entry(user).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!UserExists(id))
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

        // POST: api/Users
        [HttpPost]
        public async Task<ActionResult<User>> PostUser(User user)
        {
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetUser", new { id = user.Id }, user);
        }

        // DELETE: api/Users/5
        [HttpDelete("{id}")]
        public async Task<ActionResult<User>> DeleteUser(long id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return user;
        }


        private bool UserExists(long id)
        {
            return _context.Users.Any(e => e.Id == id);
        }

        // GET: api/users/{id}/printers
        [HttpGet("{userId}/printers")]
        public async Task<ActionResult<IEnumerable<UserPrinterDTO>>> GetPrintersForUser(long userId)
        {
            return await _context.Printers
                .Where(p => p.UserId == userId)
                .ProjectTo<UserPrinterDTO>(_mapper.ConfigurationProvider)
                .ToListAsync();
        }


        // PUT: api/Printers/5
        [HttpPut("{userId}/printers/{printerId}")]
        public async Task<IActionResult> PutPrinter(long userId, long printerId, Printer printer)
        {
            if (printerId != printer.Id)
            {
                return BadRequest();
            }

            printer.UserId = userId;


            _context.Entry(printer).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrinterExists(printerId))
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

        // POST: api/Printers
        [HttpPost("{userId}/printers")]
        public async Task<ActionResult<Printer>> PostPrinter(long userId, Printer printer)
        {
            printer.UserId = userId;
            _context.Printers.Add(printer);
            await _context.SaveChangesAsync();

            return CreatedAtAction("GetPrinter", new { id = printer.Id }, printer);
        }

        // DELETE: api/Printers/5
        [HttpDelete("{userId}/printers/{id}")]
        public async Task<ActionResult<Printer>> DeletePrinter(long userId, long id)
        {
            var printer = await _context.Printers.FindAsync(id);
            if (printer == null)
            {
                return NotFound();
            }

            if (printer.UserId != userId)
            {
                return BadRequest("Cannot delete printer for other user");
            }

            _context.Printers.Remove(printer);
            await _context.SaveChangesAsync();

            return printer;
        }

        private bool PrinterExists(long id)
        {
            return _context.Printers.Any(e => e.Id == id);
        }
    }
}
