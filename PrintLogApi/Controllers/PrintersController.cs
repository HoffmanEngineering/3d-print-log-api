using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Exceptions;
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
    IPrinterImageService printerImageService,
    IPrinterCategoryService printerCategoryService,
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    private const string DEFAULT_PRINTER_CATEGORY_NICKNAME = PrinterService.DefaultPrinterCategoryNickname;
    private const string PRINTER_SUMMARY_CACHE_PREFIX = "printer_summary_";

    /// <summary>
    /// List the current user's printers.
    /// </summary>
    /// <remarks>
    /// Inactive printers are left out unless `includeInactive=true`. Use a printer's `id` as the
    /// `printerId` when creating a print.
    /// </remarks>
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

        // The cached instance is shared with every concurrent caller and is marked
        // [ImmutableObject(true)] for HybridCache, so it must be treated as read-only. Sign
        // into a fresh copy: bucketed expiry makes repeated signing cheap and byte-identical
        // within a window, and expiry stays bounded by the request rather than by the cache
        // entry's lifetime.
        var items = new List<PrinterSummarySimpleDto>(response.Page.Items.Count);
        foreach (var item in response.Page.Items)
        {
            response.DefaultImages.TryGetValue(item.Id, out var defaultImage);

            items.Add(new PrinterSummarySimpleDto
            {
                Id = item.Id,
                Name = item.Name,
                Make = item.Make,
                Model = item.Model,
                IsActive = item.IsActive,
                WattageW = item.WattageW,
                SlotCount = item.SlotCount,
                Category = item.Category,
                LoadedFilaments = item.LoadedFilaments,
                DefaultImageThumbnailUrl = defaultImage?.BlobPath is null
                    ? null
                    : await printerService.SignImageOrNullAsync(
                        defaultImage.BlobPath,
                        defaultImage.ContentType ?? "image/webp",
                        item.Id,
                        HttpContext.RequestAborted)
            });
        }

        return Ok(new PagedList<PrinterSummarySimpleDto>(
            items,
            response.Page.Paging.TotalCount,
            response.Page.Paging.CurrentPage,
            response.Page.Paging.PageSize));

        async Task<CachedPrinterSummaryPage> LoadPrinterSummary(PrintLogContext db)
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

            var page = await PagedList<PrinterSummarySimpleDto>.CreateAsync(
                result, pagingRequest.PageNumber, pagingRequest.PageSize);

            return new CachedPrinterSummaryPage(
                page, await LoadDefaultImagePathsAsync(db, page.Items));
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

        var dto = mapper.Map<PrinterDetailDto>(printer);

        // Hydration is explicit rather than a member mapping: SAS signing is async and
        // cannot run inside ProjectTo. Without this call Images is always null and the edit
        // panel shows nothing.
        await printerService.HydrateDetailImageUrlsAsync(dto, HttpContext.RequestAborted);

        return dto;
    }

    /// <summary>
    /// Replace a printer's details.
    /// </summary>
    /// <remarks>
    /// Replaces the whole printer rather than patching it: send every field, not only the changed ones.
    /// </remarks>
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

        var requestedFilaments = printer.LoadedFilaments ?? [];
        foreach (var filament in requestedFilaments)
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

        await printerService.setLoadedFilament(existingPrinter.Id, requestedFilaments.Select(f => f.FilamentId).ToList());

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

        var loadedAt = DateTimeOffset.Now;
        newPrinter.LoadedFilaments = (printer.LoadedFilaments ?? [])
            .Where(f => f.FilamentId != default)
            .Select(f => new PrinterFilament { FilamentId = f.FilamentId, LoadedDateTime = loadedAt })
            .ToList();

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

        return mapper.Map<List<PrinterFilamentSummaryDto>>(printer.LoadedFilaments!
            .OrderBy(pf => pf.Slot == null).ThenBy(pf => pf.Slot).ThenBy(pf => pf.LoadedDateTime));
    }

    /// <summary>
    /// Load a spool into one of a printer's slots.
    /// </summary>
    /// <remarks>
    /// Slots are 0-based and must be below the printer's `slotCount`. Whatever is in the slot is
    /// unloaded, and so is the spool from any other slot or printer it was loaded on: a spool is
    /// loaded in one place at a time. Loading the spool that is already there only updates the
    /// label. Returns the printer's loaded filament, in slot order.
    /// </remarks>
    /// <param name="id">The ID of the printer.</param>
    /// <param name="slot">The 0-based slot, such as 2 for T2.</param>
    /// <param name="body">The spool to load, and what the printer calls the slot.</param>
    /// <response code="200">Returned with the printer's loaded filament.</response>
    /// <response code="400">Returned if the slot is outside the printer's slot count.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the printer or the spool.</response>
    /// <response code="404">Returned if the printer does not exist.</response>
    [HttpPut("{id}/slots/{slot}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<PrinterFilamentSummaryDto>>> LoadSlot(long id, int slot, LoadPrinterSlotDto body)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var printer = await printerService.getPrinterById(id);
        if (printer == null)
        {
            return NotFound();
        }

        if (printer.UserId != userId)
        {
            return Forbid();
        }

        if (slot < 0 || slot >= printer.SlotCount)
        {
            return BadRequest($"slot must be between 0 and {printer.SlotCount - 1}; this printer has {printer.SlotCount}.");
        }

        // [Required] guarantees a value.
        var filamentId = body.FilamentId!.Value;
        if (!await filamentService.CanUserAccessFilament(userId.Value, filamentId))
        {
            return StatusCode(403, "User does not have access to filament.");
        }

        var label = string.IsNullOrWhiteSpace(body.SlotLabel) ? null : body.SlotLabel.Trim();
        await printerService.LoadSlot(printer, slot, filamentId, label);
        await context.SaveChangesAsync();

        telemetry.TrackEvent("PrinterSlotLoaded");
        cacheVersionService.InvalidateUserCache(userId.Value);

        // The tracked printer still holds the rows just unloaded; read the result fresh.
        context.ChangeTracker.Clear();
        return await GetLoadedFilament(id);
    }

    /// <summary>
    /// Unload whatever spool is in one of a printer's slots.
    /// </summary>
    /// <remarks>
    /// Unloading an empty slot succeeds. To unload every slot, use `PUT /api/Printers/{id}/filament/unload`.
    /// </remarks>
    /// <param name="id">The ID of the printer.</param>
    /// <param name="slot">The 0-based slot.</param>
    /// <response code="204">Returned when the slot is empty.</response>
    /// <response code="401">Returned if the user is not authenticated.</response>
    /// <response code="403">Returned if the current user cannot access the printer.</response>
    /// <response code="404">Returned if the printer does not exist.</response>
    [HttpDelete("{id}/slots/{slot}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnloadSlot(long id, int slot)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var printer = await printerService.getPrinterById(id);
        if (printer == null)
        {
            return NotFound();
        }

        if (printer.UserId != userId)
        {
            return Forbid();
        }

        await printerService.UnloadSlot(printer, slot);
        await context.SaveChangesAsync();

        telemetry.TrackEvent("PrinterSlotUnloaded");
        cacheVersionService.InvalidateUserCache(userId.Value);

        return NoContent();
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

    // ------------------------------------------------------------------------------------
    // Printer images
    // ------------------------------------------------------------------------------------

    /// <summary>Largest accepted image upload.</summary>
    private const int MaxImageSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The unsigned default-image blob path for each printer on a page, keyed by printer id.
    /// Runs INSIDE the cached computation: a path never expires, so caching it is safe, while
    /// the signed URL built from it must not be cached at all.
    /// </summary>
    private static async Task<Dictionary<long, CachedDefaultImage>> LoadDefaultImagePathsAsync(
        PrintLogContext db, IList<PrinterSummarySimpleDto> items)
    {
        if (items.Count == 0) return [];

        var ids = items.Select(i => i.Id).ToList();

        var candidates = await db.PrinterImages
            .AsNoTracking()
            .Where(pi => ids.Contains(pi.PrinterId))
            .Select(pi => new
            {
                pi.PrinterId,
                pi.IsDefault,
                pi.DisplayOrder,
                pi.Id,
                pi.ContentType,
                HasThumbnail = pi.ThumbnailFile != null,
                Path = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : pi.File.Path
            })
            .ToListAsync();

        // The filtered unique index enforces AT MOST one default, never at least one, so a
        // printer can legitimately have images and no flagged default. Fall back to the
        // lowest DisplayOrder, matching detail hydration.
        return candidates
            .GroupBy(c => c.PrinterId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var chosen = g.OrderByDescending(c => c.IsDefault)
                                  .ThenBy(c => c.DisplayOrder)
                                  .ThenBy(c => c.Id)
                                  .First();

                    return new CachedDefaultImage
                    {
                        BlobPath = chosen.Path,
                        ContentType = chosen.HasThumbnail ? "image/webp" : chosen.ContentType
                    };
                });
    }

    /// <summary>
    /// Signed default-image thumbnails for every printer the caller owns.
    /// </summary>
    /// <remarks>
    /// Deliberately has no anonymous variant. Surfaces that merely mention a printer read
    /// this map instead of carrying a signed URL on their own DTO, which is what keeps
    /// printer photos off public print pages without a guard on each of those surfaces.
    /// </remarks>
    [HttpGet("thumbnails")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IList<PrinterThumbnailDto>>> GetPrinterThumbnails()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        // Ownership is Printer.UserId. A predicate on PrinterImage.CreatedById compiles and
        // reads plausibly; because this endpoint returns a whole-user map rather than taking
        // a printer id, that mistake would leak every default printer image in the database
        // while still passing every happy-path test.
        var candidates = await context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.Printer.UserId == userId.Value)
            .Select(pi => new
            {
                pi.PrinterId,
                pi.IsDefault,
                pi.DisplayOrder,
                pi.Id,
                pi.ContentType,
                HasThumbnail = pi.ThumbnailFile != null,
                Path = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : pi.File.Path
            })
            .ToListAsync(HttpContext.RequestAborted);

        // Same at-most-versus-at-least-one-default fallback as detail hydration.
        var chosen = candidates
            .GroupBy(c => c.PrinterId)
            .Select(g => g.OrderByDescending(c => c.IsDefault)
                          .ThenBy(c => c.DisplayOrder)
                          .ThenBy(c => c.Id)
                          .First());

        var result = new List<PrinterThumbnailDto>();
        foreach (var c in chosen)
        {
            result.Add(new PrinterThumbnailDto
            {
                PrinterId = c.PrinterId,
                ThumbnailUrl = await printerService.SignImageOrNullAsync(
                    c.Path,
                    c.HasThumbnail ? "image/webp" : c.ContentType,
                    c.PrinterId,
                    HttpContext.RequestAborted)
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Upload a photo of a printer.
    /// </summary>
    /// <remarks>
    /// The uploaded bytes are decoded server-side; the declared content type from the client
    /// is not trusted. An undecodable or disallowed file is rejected before anything is
    /// stored.
    /// </remarks>
    /// <response code="201">The stored image, with signed URLs.</response>
    /// <response code="400">The file is missing, too large, or not a supported image.</response>
    /// <response code="404">No such printer belonging to the current user.</response>
    [HttpPost("{id}/images")]
    // Enforced BEFORE model binding, unlike the file.Length check in the body: by the time
    // IFormFile has materialized, a much larger multipart body has already been buffered.
    //
    // Not covered by an integration test on purpose: the limit is a Kestrel feature and
    // TestServer does not implement IHttpMaxRequestBodySizeFeature, so an oversized body
    // there falls through to the file.Length check and reports 400 instead of 413.
    [RequestSizeLimit(MaxImageSizeBytes + (1024 * 1024))]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterImageDto>> PostPrinterImage(long id, IFormFile file)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        if (file is null || file.Length == 0) return BadRequest("Image file is required.");

        if (file.Length > MaxImageSizeBytes) return BadRequest("Image must be under 10MB.");

        try
        {
            await using var stream = file.OpenReadStream();
            var image = await printerImageService.AddImageAsync(
                id, stream, userId.Value, HttpContext.RequestAborted);

            var dto = await printerService.HydrateImageDtoAsync(image, HttpContext.RequestAborted);

            telemetry.TrackEvent("PrinterPictureAdded");

            // The printer summary is cached and now carries a default-image path, so the
            // list would keep showing the old photo until the entry expired.
            cacheVersionService.InvalidateUserCache(userId.Value);

            return CreatedAtAction(nameof(GetPrinterImage), new { id, imageId = image.Id }, dto);
        }
        catch (InvalidImageException ex) { return BadRequest(ex.Message); }
        // The account storage quota throws this. There is no global exception-to-status
        // mapping in this app, so without the catch it surfaces as a 500.
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Redirects to a signed URL for a printer image.
    /// </summary>
    /// <remarks>
    /// For non-browser API consumers holding a bearer token. The UI does not use this - it
    /// reads pre-signed URLs from the printer DTO and the thumbnail map.
    /// NOT usable from &lt;img src&gt;: the redirect itself requires the bearer token.
    /// </remarks>
    [HttpGet("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrinterImage(long id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        // Ownership is part of the predicate, so a foreign image is indistinguishable from a
        // missing one. This endpoint must not be an existence oracle. Note the deliberate
        // difference from GetPrinter above, which answers 403: the image routes are new and
        // start out non-disclosing rather than inheriting that older behavior.
        var image = await context.PrinterImages
            .AsNoTracking()
            .Include(pi => pi.File)
            .FirstOrDefaultAsync(pi => pi.PrinterId == id
                                    && pi.Id == imageId
                                    && pi.Printer.UserId == userId.Value,
                                 HttpContext.RequestAborted);
        if (image?.File?.Path is null) return NotFound();

        var uri = await printerService.SignImageOrNullAsync(
            image.File.Path, image.ContentType, id, HttpContext.RequestAborted);
        if (uri is null) return NotFound();

        return Redirect(uri);
    }

    /// <summary>
    /// Deletes a printer image, its file rows, and its blobs.
    /// </summary>
    [HttpDelete("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePrinterImage(long id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await printerImageService.DeleteImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Reorders the images on a printer. The supplied IDs must be its exact image set.
    /// </summary>
    [HttpPut("{id}/images/reorder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderPrinterImages(long id, [FromBody] List<int> orderedImageIds)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await printerImageService.ReorderImagesAsync(
                id, orderedImageIds, userId.Value, HttpContext.RequestAborted);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Makes one of the images on a printer its default.
    /// </summary>
    [HttpPost("{id}/images/{imageId}/set-as-default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultPrinterImage(long id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await printerImageService.SetDefaultImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }
}
