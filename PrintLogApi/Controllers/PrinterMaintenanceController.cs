using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.PrinterMaintenance;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage a user's printer maintenance entries
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PrinterMaintenanceController(
    IPrinterMaintenanceService printerMaintenanceService,
    IMapper mapper,
    IPrinterService printerService) : ControllerBase
{
    /// <summary>
    /// Gets a Paged Result of printer maintenance entries
    /// </summary>
    /// <param name="pagingRequest">The paging request information.</param>
    /// <param name="sortRequest">The Column and Direction to sort the results for.</param>
    /// <param name="searchText">Search maintenance entries's category/description/notes for text.</param>
    /// <param name="filterByPrinterIds">Filter by specific printer ids.</param>
    /// <param name="includeDone">Whether to include entries marked as Done.</param>
    /// <param name="includeNotDone">Whether to include entries marked as Done.</param>
    /// <returns>A Paged List of printer maintenance entries.</returns>
    /// <response code="200">Returns the paged list of printer maintenance entries.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedList<PrinterMaintenanceDto>>> GetPrinterMaintenanceEntriesForCurrentUser(
        [FromQuery] PagedRequest pagingRequest,
        [FromQuery] SortRequest<PrinterMaintenanceSortColumn> sortRequest,
        // Optional filter: absent from the query string means null here today, and must keep
        // meaning that rather than becoming an implicit [Required] 400 (#45).
        [FromQuery] string? searchText,
        // Deliberately NOT nullable. The collection binder supplies an empty array when the
        // query string omits it, which is why the service can read .Length unguarded. An
        // implicit [Required] is satisfied by that empty array, so this one is safe as-is.
        [FromQuery] long[] filterByPrinterIds,
        [FromQuery] bool? includeDone,
        [FromQuery] bool? includeNotDone)
    {
        long? currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Unauthorized("Please login before requesting filaments.");
        }

        return await printerMaintenanceService.GetPrinterMaintenanceByUser(currentUserId.Value,
            sortRequest.SortDirection,
            sortRequest.SortColumn,
            pagingRequest.PageNumber,
            pagingRequest.PageSize,
            searchText,
            filterByPrinterIds,
            includeDone,
            includeNotDone
            );
    }


    /// <summary>
    /// Returns detailed information for a maintence entry
    /// </summary>
    /// <param name="id">The GUID of the entry.</param>
    /// <returns></returns>
    /// <response code="200">The Maintenance Entry details on success.</response>
    /// <response code="403">Returned when the requested entry does not belong to the current user.</response>
    /// <response code="404">Returned when an entry with that GUID does not exist.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterMaintenanceDto>> GetMaintenanceEntry(Guid id)
    {
        var entry = await printerMaintenanceService.GetEntryById(id);

        if (entry == null)
        {
            return NotFound();
        }

        var currentUserId = User.GetUserId();

        if (currentUserId != entry.CreatedById)
        {
            return Forbid();
        }

        return mapper.Map<PrinterMaintenanceDto>(entry);
    }

    /// <summary>
    /// Update an existing maintenance entry with new information
    /// </summary>
    /// <param name="id">The GUID of the entry to update.</param>
    /// <param name="maintenanceDto">The updated entry details.</param>
    /// <returns>The updated entry.</returns>
    /// <response code="201">The updated entry information.</response>
    /// <response code="400">Returned if the id does not match the id of the entry details provided.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    /// <response code="403">Returned if the current user tries to update an entry which is not theirs.</response>
    /// <response code="404">Returned if a entry with the specified ID is not found.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutPrinterMaintenanceEntry(Guid id, PutPrinterMaintenanceDto maintenanceDto)
    {
        if (id != maintenanceDto.Id)
        {
            return BadRequest("ID in route does not match body.");
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingEntry = await printerMaintenanceService.GetEntryById(id);

        if (existingEntry == null)
        {
            return NotFound();
        }

        if (existingEntry.CreatedById != userId)
        {
            return Forbid();
        }

        var printer = await printerService.getPrinterById(maintenanceDto.PrinterId);

        // Return if the printer does not belong to the user making the request.
        // Null-forgiven: an unknown PrinterId already threw here before nullable analysis
        // was enabled, and it fails closed either way. Tracked in #57.
        if (printer!.UserId != userId)
        {
            return Forbid();
        }

        try
        {
            var updatedEntry = await printerMaintenanceService.UpdateEntry(id, maintenanceDto, userId.Value);

            return CreatedAtAction("GetMaintenanceEntry", new { id = existingEntry.Id }, mapper.Map<PrinterMaintenanceDto>(updatedEntry));
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>
    ///     Create a new Maintenance Entry for the current user.
    /// </summary>
    /// <param name="dto">The dto containing all of the details for the printer maintenance entry to create.</param>
    /// <returns>The filament detail DTO that was created.</returns>
    /// <response code="201">The filament detail DTO that was created.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PrinterMaintenanceDto>> PostPrinterMaintenanceEntry(AddPrinterMaintenanceDto dto)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var newEntry = await printerMaintenanceService.AddEntry(dto, userId.Value);


        return CreatedAtAction("GetMaintenanceEntry", new { id = newEntry.Id }, mapper.Map<PrinterMaintenance, PrinterMaintenanceDto>(newEntry));
    }

    /// <summary>
    /// Permantently delete a maintenance entry
    /// </summary>
    /// <param name="id">The ID of the entry to delete.</param>
    /// <response code="204">Returned if the entry was deleted successfully.</response>
    /// <response code="403">Returned if the current user cannot access the filament.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeletePrinterMaintenceEntry(Guid id)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingEntry = await printerMaintenanceService.GetEntryById(id);

        // Null-forgiven: a missing id already threw here before nullable analysis was
        // enabled, and it fails closed either way. Turning it into a clean 404, as the
        // sibling action does, is a behaviour change - tracked in #57.
        if (existingEntry!.CreatedById != userId)
        {
            return Forbid();
        }

        try
        {
            await printerMaintenanceService.DeleteMaintenanceEntry(existingEntry);
        }
        catch (Exception)
        {
            return BadRequest("Entry cannot be deleted");
        }

        return NoContent();
    }

    /// <summary>
    /// Returns a DTO which includes a list of all maintenance categories for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Maintence Categories on success.</response>
    [HttpGet("categories")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PrinterMaintenanceCategoriesDto>> GetPrinterMaintenanceCategories()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Forbid();
        }

        var categories = await printerMaintenanceService.GetMaintenanceCategories(currentUserId.Value);

        return new PrinterMaintenanceCategoriesDto { Categories = categories };
    }
}
