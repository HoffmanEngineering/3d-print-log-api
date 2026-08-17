using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage a user's list of printers.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PrintersController(
    PrintLogContext context,
    IMapper mapper,
    TelemetryClient telemetry,
    IFilamentService filamentService,
    IPrinterService printerService,
    IPrinterCategoryService printerCategoryService,
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    private const string DEFAULT_PRINTER_CATEGORY_NICKNAME = PrinterService.DefaultPrinterCategoryNickname;
    private const string PRINTER_SUMMARY_CACHE_PREFIX = "printer_summary_";

    /// <summary>
    /// Get an array of paged Printer Summaries for current user.
    /// </summary>
    /// <param name="pagingRequest">Paging information</param>
    /// <param name="searchText">Filter printers by name, make, and model.</param>
    /// <param name="includeInactive">By default, only returns active printers. Set this to true to return both active and inactive printers.</param>
    /// <response code="200">Returned with a paged list of printer summaries.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    [HttpGet("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IEnumerable<PrinterSummarySimpleDto>>> GetPrinterSummary([FromQuery] PagedRequest pagingRequest, [FromQuery] string? searchText, [FromQuery] bool includeInactive = false)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var version = cacheVersionService.GetUserCacheVersion(userId.Value);
        var cacheKey = GeneratePrinterCacheKey(userId.Value, version, pagingRequest, searchText, includeInactive);

        // Stampede protection: concurrent misses on one key run the query once between them.
        // See the equivalent block in PrintsController.GetPrintSummary — including why the
        // sliding expiration is not reproduced, and why the context comes from
        // CachedComputation's scope rather than the one injected into this controller.
        var response = await cache.GetOrCreateAsync(
            cacheKey,
            ct => computation.RunAsync(
                (services, _) => LoadPrinterSummary(services.GetRequiredService<PrintLogContext>()), ct),
            SummaryCacheOptions,
            cancellationToken: HttpContext.RequestAborted);

        return Ok(response);

        Task<PagedList<PrinterSummarySimpleDto>> LoadPrinterSummary(PrintLogContext db)
        {
            var printers = db.Printers
                .AsNoTracking()
                .Where(p => p.UserId == userId);

            if (!includeInactive)
            {
                printers = printers.Where(p => p.IsActive == true);
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                printers = printers.Where(p => p.Name!.Contains(searchText) || p.Make!.Contains(searchText) || p.Model!.Contains(searchText));
            }

            var result = printers
                .Include(p => p.Category)
                .Include(p => p.LoadedFilaments!)
                    .ThenInclude(lf => lf.Filament)
                .OrderByDescending(p => p.Name)
                .ThenByDescending(p => p.Make)
                .ThenByDescending(p => p.Model)
                .ProjectTo<PrinterSummarySimpleDto>(mapper.ConfigurationProvider);

            return PagedList<PrinterSummarySimpleDto>.CreateAsync(result, pagingRequest.PageNumber, pagingRequest.PageSize);
        }
    }

    private static readonly HybridCacheEntryOptions SummaryCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// Return a specific printer by id.
    /// </summary>
    /// <param name="id">The ID of the printer.</param>
    /// <response code="200">Returned with the printer details.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the requested printer.</response>
    /// <response code="404">Returned if the printer does not exist.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterDetailDto>> GetPrinter(long id)
    {
        var printer = await context.Printers
            .Include(p => p.LoadedFilaments!)
                .ThenInclude(pf => pf.Filament)
                    .ThenInclude(f => f.FilamentAdjustments)
            .Include(p => p.LoadedFilaments!)
                .ThenInclude(pf => pf.Filament)
                    .ThenInclude(f => f.PrintFilaments)
            .Include(p => p.Category!)
                .ThenInclude(type => type.MaterialCategory)
            .Where(p => p.Id == id)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (printer == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (printer.UserId != userId)
        {
            return Forbid();
        }

        return mapper.Map<PrinterDetailDto>(printer);
    }

    /// <summary>
    /// Update a printer. Overwrites all properties of the printer.
    /// </summary>
    /// <param name="id">The ID of the printer to update.</param>
    /// <param name="printer">The updated printer details.</param>
    /// <returns></returns>
    /// <response code="201">Returned with the updated printer details.</response>
    /// <response code="400">Returned if the printer details do not contain all required fields, or if the ID in the printer details does not match the id in the route.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the requested printer.</response>
    /// <response code="404">Returned if the printer does not exist.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutPrinter(long id, AddPrinterDTO printer)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (id != printer.Id)
        {
            return BadRequest("ID in route does not match body.");
        }

        var existingPrinter = await printerService.getPrinterById(id);

        if (existingPrinter == null)
        {

            return NotFound();
        }

        if (existingPrinter.UserId != userId)
        {
            return Forbid();
        }

        existingPrinter = mapper.Map<AddPrinterDTO, Printer>(printer, existingPrinter);

        var printerCategory = await printerCategoryService.get(printer.Category ?? existingPrinter.Category!.Nickname ?? DEFAULT_PRINTER_CATEGORY_NICKNAME);

        if (printerCategory is null)
        {
            return BadRequest("Printer Category not found");
        }

        existingPrinter.Category = printerCategory;

        foreach (var filament in existingPrinter.LoadedFilaments!)
        {
            if (filament.FilamentId != default)
            {
                var canAccessFilament = await filamentService.CanUserAccessFilament(userId.Value, filament.FilamentId);
                if (!canAccessFilament)
                {
                    //throw new UserCannotAccessFilamentException();
                    return StatusCode(403, "User does not have access to filament.");
                }
            }
        }

        await printerService.setLoadedFilament(existingPrinter.Id, existingPrinter.LoadedFilaments.Select(f => f.FilamentId).AsEnumerable());

        context.Entry(existingPrinter).State = EntityState.Modified;

        try
        {
            await context.SaveChangesAsync();
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

        telemetry.TrackEvent("PrinterEdit");

        cacheVersionService.InvalidateUserCache(userId.Value);

        return Ok(mapper.Map<PrinterDetailDto>(existingPrinter));
    }

    /// <summary>
    /// Create a new printer for the current user.
    /// </summary>
    /// <param name="printer">The printer details to create</param>
    /// <response code="201">Returned with the newly creeated printer details.</response>
    /// <response code="400">Returned if the printer details do not contain all required fields, or if the ID in the printer details does not match the id in the route.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the requested printer.</response>
    /// <response code="404">Returned if the printer does not exist.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterDetailDto>> PostPrinter(AddPrinterDTO printer)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var newPrinter = mapper.Map<Printer>(printer);

        var printerType = await printerCategoryService.get(printer.Category ?? DEFAULT_PRINTER_CATEGORY_NICKNAME);

        if (printerType is null)
        {
            return BadRequest("Printer Type not found");
        }

        newPrinter.Category = printerType;

        newPrinter.UserId = userId.Value;

        context.Printers.Add(newPrinter);
        await context.SaveChangesAsync();

        telemetry.TrackEvent("PrinterAdded");

        cacheVersionService.InvalidateUserCache(userId.Value);

        return CreatedAtAction("GetPrinter", new { id = newPrinter.Id }, mapper.Map<PrinterDetailDto>(newPrinter));
    }

    /// <summary>
    /// Retrieve the list of currently loaded filament for this printer
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpGet("{id}/filament")]
    public async Task<ActionResult<List<PrinterFilamentSummaryDto>>> GetLoadedFilament(long id)
    {
        var printer = await context.Printers
            .Include(p => p.LoadedFilaments!)
                .ThenInclude(pf => pf.Filament)
                    .ThenInclude(f => f.FilamentAdjustments)
            .Include(p => p.LoadedFilaments!)
                .ThenInclude(pf => pf.Filament)
                    .ThenInclude(f => f.PrintFilaments)
            .Where(p => p.Id == id)
            .SingleOrDefaultAsync();

        if (printer == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (printer.UserId != userId)
        {
            return Forbid();
        }

        return mapper.Map<List<PrinterFilamentSummaryDto>>(printer.LoadedFilaments);
    }

    /// <summary>
    /// Unload all filament for a printer by ID
    /// </summary>
    /// <param name="id"></param>
    [HttpPut("{id}/filament/unload")]
    public async Task<IActionResult> UnloadPrinterFilament(long id)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingPrinter = await printerService.getPrinterById(id);

        if (existingPrinter == null)
        {

            return NotFound();
        }

        if (existingPrinter.UserId != userId)
        {
            return Forbid();
        }

        // Set loaded filament to an empty list.
        await printerService.setLoadedFilament(existingPrinter.Id, new List<Guid>());

        try
        {
            await context.SaveChangesAsync();
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

        telemetry.TrackEvent("PrinterFilamentUnloaded");

        cacheVersionService.InvalidateUserCache(userId.Value);

        return Ok();
    }

    /// <summary>
    /// Permantently delete a Printer, if the Printer has not been used in any existing prints.
    /// </summary>
    /// <param name="id">The ID of the printer to delete.</param>
    /// <response code="204">Returned if the printer was deleted successfully.</response>
    /// <response code="400">Returned if the printer is unable to be deleted since it has been used in a print.</response>
    /// <response code="403">Returned if the current user cannot access the printer.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeletePrinter(long id)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingPrinter = await printerService.getPrinterById(id);

        if (existingPrinter == null)
        {

            return NotFound();
        }

        if (existingPrinter.UserId != userId)
        {
            return Forbid();
        }

        try
        {
            await printerService.DeletePrinter(id);
        }
        catch (PrinterIsInUseException)
        {
            return BadRequest("This Printer is used in a Print and cannot be deleted. Try editing the Printer and marking it as Inactive instead.");
        }

        cacheVersionService.InvalidateUserCache(userId.Value);

        return NoContent();
    }

    private bool PrinterExists(long id)
    {
        return context.Printers.Any(e => e.Id == id);
    }

    /// <summary>
    /// Generates a unique cache key for printer summary queries based on user and query parameters.
    /// </summary>
    private string GeneratePrinterCacheKey(long userId, string version,
                                           PagedRequest pagingRequest, string? searchText,
                                           bool includeInactive)
    {
        return $"{PRINTER_SUMMARY_CACHE_PREFIX}{userId}_v{version}_" +
               $"p{pagingRequest.PageNumber}_s{pagingRequest.PageSize}_" +
               $"q{searchText ?? "none"}_" +
               $"ia{includeInactive}";
    }

    // EstimatePrinterCacheSize is gone for the same reason as PrintsController's counterpart:
    // HybridCache charges the entry's real serialized byte length. See CacheBudget.
}
