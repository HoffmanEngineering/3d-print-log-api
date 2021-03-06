using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage a user's list of filaments
    /// </summary>
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

        /// <summary>
        /// Gets a Paged Result of filaments for the user.
        /// </summary>
        /// <param name="pagingRequest">The paging request information.</param>
        /// <param name="sortRequest">The Column and Direction to sort the results for.</param>
        /// <param name="searchText">Search filament's name/description/brand for text.</param>
        /// <param name="includeInactive">Include filament rolls that have been marked as inactive.</param>
        /// <returns>A Paged List of filament rolls.</returns>
        /// <response code="200">Returns the paged list of filament rolls.</response>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<PagedList<FilamentSummaryDto>>> GetFilamentSummariesForUser(
            [FromQuery] PagedRequest pagingRequest,
            [FromQuery] SortRequest<FilamentSummarySortColumn> sortRequest,
            [FromQuery] string searchText,
            [FromQuery] bool? includeInactive)
        {
            long? currentUserId = User.GetUserId();
            if (!currentUserId.HasValue)
            {
                return Unauthorized("Please login before requesting filaments.");
            }

            return await _filamentService.GetFilamentSummaryForUser(currentUserId.Value, 
                sortRequest.SortDirection, 
                sortRequest.SortColumn, 
                pagingRequest.PageNumber, 
                pagingRequest.PageSize,
                searchText,
                includeInactive);
        }


        /// <summary>
        /// Returns detailed information for a filament roll by ID.
        /// </summary>
        /// <param name="id">The GUID of the filament.</param>
        /// <returns></returns>
        /// <response code="200">The Filament details on success.</response>
        /// <response code="403">Returned when the requested filament does not belong to the current user.</response>
        /// <response code="404">Returned when a filament with that GUID does not exist.</response>
        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
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

        /// <summary>
        /// Update an existing filament with new information.
        /// </summary>
        /// <param name="id">The GUID of the filament to update.</param>
        /// <param name="filamentDto">The updated filament details.</param>
        /// <returns>The updated filament.</returns>
        /// <response code="201">The updated filament information.</response>
        /// <response code="400">Returned if the id does not match the id of the filament details provided.</response>
        /// <response code="401">Returned if the request is not authenticated.</response>
        /// <response code="403">Returned if the current user tries to update a filament which is not theirs.</response>
        /// <response code="404">Returned if a filament with the specified ID is not found.</response>
        [HttpPut("{id}")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
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
