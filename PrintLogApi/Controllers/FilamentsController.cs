using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FilamentsController : ControllerBase
    {
        private readonly IFilamentService _filamentService;
        private readonly IMapper _mapper;

        public FilamentsController(IFilamentService filamentService, IMapper mapper)
        {
            _filamentService = filamentService;
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<PagedList<FilamentSummaryDto>>> GetFilamentSummariesForUser(
            [FromQuery] PagedRequest pagingRequest,
            [FromQuery] SortRequest<FilamentSummarySortColumn> sortRequest,
            [FromQuery] string searchText,
            [FromQuery] bool? includeInactive)
        {
            long? currentUserId = User.GetUserId();
            if (!currentUserId.HasValue)
            {
                return Forbid("Please login before requesting filaments.");
            }

            return await _filamentService.GetFilamentSummaryForUser(currentUserId.Value, 
                sortRequest.SortDirection, 
                sortRequest.SortColumn, 
                pagingRequest.PageNumber, 
                pagingRequest.PageSize,
                searchText,
                includeInactive);
        }

        //// GET: api/Filaments
        //[HttpGet]
        //public async Task<ActionResult<IEnumerable<Filament>>> GetFilaments()
        //{
        //    return await _context.Filaments.ToListAsync();
        //}

        // GET: api/Filaments/5
        [HttpGet("{id}")]
        public async Task<ActionResult<FilamentDetailDto>> GetFilament(Guid id)
        {
            var filament = await _filamentService.GetFilamentById(id);

            if (filament == null)
            {
                return NotFound();
            }


            var currentUserId = User.GetUserId();

            if (currentUserId != filament.CreatedById)
            {
                return Forbid();
            }

            return _mapper.Map<FilamentDetailDto>(filament);
        }

        // PUT: api/Filaments/5
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPut("{id}")]
        public async Task<IActionResult> PutFilament(Guid id, FilamentDetailDto filamentDto)
        {
            if (id != filamentDto.Id)
            {
                return BadRequest();
            }

            var existingFilament = await _filamentService.GetFilamentById(id);

            if (existingFilament == null)
            {
                return NotFound();
            }

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            if (!await _filamentService.CanUserAccessFilament(userId.Value, id))
            {
                return Forbid();
            }

            try
            {
                var updatedPrint = await _filamentService.UpdateFilament(id, filamentDto, userId.Value);

                return CreatedAtAction("GetFilament", new { id = existingFilament.Id }, _mapper.Map<FilamentDetailDto>(updatedPrint));
            }
            catch (DoesNotExistException)
            {
                return NotFound();
            }
        }

        // POST: api/Filaments
        // To protect from overposting attacks, see https://go.microsoft.com/fwlink/?linkid=2123754
        [HttpPost]
        public async Task<ActionResult<FilamentDetailDto>> PostFilament(AddFilamentDto filamentDto)
        {
            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var newFilament = await _filamentService.AddFilament(filamentDto, userId.Value);


            return CreatedAtAction("GetFilament", new { id = newFilament.Id }, _mapper.Map<Filament, FilamentDetailDto>(newFilament));
        }

        //// DELETE: api/Filaments/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteFilament(Guid id)
        {
            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }


            if(!await _filamentService.CanUserAccessFilament(userId.Value, id))
            {
                return Forbid();
            }

            try
            {
                await _filamentService.DeleteFilament(id);
            } catch (FilamentIsInUseException)
            {
                return BadRequest("This Filament is used in a Print and cannot be delete. Try editing the Filament and marking it as Inactive instead.");
            }

            return NoContent();
        }
    }
}
