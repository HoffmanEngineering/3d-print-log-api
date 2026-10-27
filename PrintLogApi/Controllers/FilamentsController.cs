using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Enums;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Labels;
using PrintLogApi.SpoolTags.Matching;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage a user's list of filaments
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class FilamentsController(
    IFilamentService filamentService,
    IMapper mapper,
    IFilamentImageService filamentImageService,
    PrintLogContext context,
    IBlobStorageService blobStorageService,
    TagReadService tagReadService,
    FilamentCatalogMatcher catalogMatcher,
    LabelReadService labelReadService,
    TelemetryClient telemetry) : ControllerBase
{
    /// <summary>
    /// Largest accepted image upload. Referenced by the action's [RequestSizeLimit] as well
    /// as its body check, so the two cannot drift apart.
    /// </summary>
    private const int MaxImageSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// List the current user's filaments, paged.
    /// </summary>
    /// <remarks>
    /// These are the user's filament spools, which the web app calls *materials*. Use a filament's
    /// `id` in a print's `filamentUsage` to record what the print consumed. Weights are in milligrams.
    /// </remarks>
    /// <param name="pagingRequest">The paging request information.</param>
    /// <param name="sortRequest">The Column and Direction to sort the results for.</param>
    /// <param name="searchText">Search filament's name/description/brand for text.</param>
    /// <param name="filterByMaterialCategoryNickname">Optional filter by a material category nickname</param>
    /// <param name="includeInactive">Include filament rolls that have been marked as inactive.</param>
    /// <param name="showFavoritesOnly">Show only the favoriate filaments</param>
    /// <param name="showLoadedFilamentOnly">Show only currently loaded filament</param>
    /// <returns>A Paged List of filament rolls.</returns>
    /// <response code="200">Returns the paged list of filament rolls.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedList<FilamentSummaryDto>>> GetFilamentSummariesForUser(
        [FromQuery] PagedRequest pagingRequest,
        [FromQuery] SortRequest<FilamentSummarySortColumn> sortRequest,
        // Nullable because that is what binding already produces: every one of these is
        // optional, and a request that omits the query string binds null here today. Left
        // non-nullable they would pick up MVC's implicit [Required] and 400 instead (#45).
        [FromQuery] string? searchText,
        [FromQuery] string? filterByMaterialCategoryNickname,
        [FromQuery] string? filterByStorageLocation,
        [FromQuery] bool? includeInactive,
        [FromQuery] bool? showFavoritesOnly,
        [FromQuery] bool? showLoadedFilamentOnly,
        [FromQuery] List<ColorPatternType>? colorPatterns = null,
        [FromQuery] List<FilamentFinishType>? finishTypes = null,
        [FromQuery] List<FilamentEffect>? effects = null)
    {
        long? currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Unauthorized("Please login before requesting filaments.");
        }

        return await filamentService.GetFilamentSummaryForUser(currentUserId.Value,
            sortRequest.SortDirection,
            sortRequest.SortColumn,
            pagingRequest.PageNumber,
            pagingRequest.PageSize,
            searchText,
            filterByMaterialCategoryNickname,
            filterByStorageLocation,
            includeInactive,
            showFavoritesOnly,
            showLoadedFilamentOnly,
            colorPatterns,
            finishTypes,
            effects);
    }


    /// <summary>
    /// Find the current user's spool carrying an RFID or NFC tag.
    /// </summary>
    /// <remarks>
    /// The UID is matched in any case and with any separators, so `04:A2:3f:11` and `04A23F11`
    /// find the same spool. Inactive spools are included. Another user's tag is a 404.
    /// </remarks>
    /// <param name="uid">The tag UID, as hex bytes.</param>
    /// <response code="200">The spool carrying the tag.</response>
    /// <response code="400">The UID is not hex bytes.</response>
    /// <response code="404">None of the user's spools carries the tag.</response>
    [HttpGet("by-tag/{uid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FilamentDetailDto>> GetFilamentByTag(string uid)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        Filament? filament;
        try
        {
            filament = await filamentService.GetFilamentByTag(userId.Value, uid);
        }
        catch (BadRequestException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        if (filament is null)
        {
            return NotFound();
        }

        var dto = mapper.Map<FilamentDetailDto>(filament);
        await filamentService.HydrateDetailImageUrlsAsync(dto, HttpContext.RequestAborted);
        return dto;
    }

    /// <summary>
    /// Resolve a raw RFID or NFC tag read to one of the current user's spools.
    /// </summary>
    /// <remarks>
    /// Send the tag's UID and, when the reader could dump it, its memory. A UID the user has
    /// linked answers `linked` with the spool, and refreshes what the app stored from the tag.
    /// Any other tag answers `unknown`: the decoded `reading` (null for a blank or unrecognized
    /// tag), the closest `catalogMatch`, and the user's own materials that fit it as `suggestions`.
    /// A `hint` of `hold-still` means the read was incomplete and worth repeating. Unrecognized
    /// dumps are kept for 90 days, without any account data, so new formats can be added.
    /// </remarks>
    /// <param name="request">The tag UID, the NFC technologies it reported, and its memory.</param>
    /// <response code="200">The tag resolved to a spool, or to a reading with suggestions.</response>
    /// <response code="400">The UID is not hex bytes, or the memory is malformed.</response>
    /// <response code="413">The request body is over 16 KB.</response>
    [HttpPost("tag-reads")]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<TagReadResponse>> ResolveTagRead([FromBody] TagReadRequest request)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return await tagReadService.ResolveAsync(userId.Value, request, HttpContext.RequestAborted);
        }
        catch (BadRequestException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Resolve what the phone read from a spool's label.
    /// </summary>
    /// <remarks>
    /// The phone decodes the label's barcodes and OCRs it; the photo never leaves it. This finds
    /// the brand and color name in the OCR text (the user's own brands and colors, then
    /// SpoolmanDB's), the user's own spools the label may be (a stored barcode first, then
    /// material, color name, brand and weight), and the catalog entry it may be (a barcode enough
    /// users voted for, then brand, material and color name). At most eight barcodes are read and
    /// the first 4,000 characters of OCR text.
    /// </remarks>
    /// <param name="request">The barcodes, OCR text and extracted fields.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The reading, up to five of the user's spools, and the catalog match or null.</response>
    /// <response code="413">The request body is over 16 KB.</response>
    [HttpPost("label-reads")]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<LabelReadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<LabelReadResponse>> ResolveLabelRead([FromBody] LabelReadRequestDto request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        return await labelReadService.ResolveAsync(userId.Value, request, ct);
    }

    /// <summary>
    /// Find the SpoolmanDB catalog entry a label-scanned spool is, by brand, material and color name.
    /// </summary>
    /// <remarks>
    /// The add form calls this once the user has typed the brand a label scan could not read.
    /// Brand, material and color name are required for a match; the entry's name must contain the
    /// color name. Weight and diameter break ties.
    /// </remarks>
    /// <param name="request">What the form knows about the spool.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The best catalog entry, with its temperatures.</response>
    /// <response code="204">No entry matches well enough.</response>
    [HttpPost("catalog-match")]
    [ProducesResponseType<CatalogMatch>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MatchCatalog([FromBody] CatalogMatchRequestDto request, CancellationToken ct)
    {
        if (!User.GetUserId().HasValue)
        {
            return Unauthorized();
        }

        var match = await catalogMatcher.MatchByNameAsync(
            new LabelReading(request.Brand, request.MaterialType, request.ColorName, request.NetWeightG, request.DiameterMm, null, null, null, null), ct);
        return match is null ? NoContent() : Ok(match);
    }

    /// <summary>
    /// Find the current user's spool by its spool number.
    /// </summary>
    /// <remarks>
    /// Every spool has a short number of its own (1, 2, 3…), unique among the user's spools and
    /// never reused after a delete, so a number written on a label always means the same spool.
    /// Spoolman-compatible clients use it as the spool id. Inactive spools are included. Another
    /// user's number is a 404.
    /// </remarks>
    /// <param name="spoolNumber">The spool number.</param>
    /// <response code="200">The spool with that number.</response>
    /// <response code="404">None of the user's spools has that number.</response>
    [HttpGet("by-number/{spoolNumber:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FilamentDetailDto>> GetFilamentBySpoolNumber(int spoolNumber)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var filament = await filamentService.GetFilamentBySpoolNumber(userId.Value, spoolNumber, HttpContext.RequestAborted);
        if (filament is null)
        {
            return NotFound();
        }

        var dto = mapper.Map<FilamentDetailDto>(filament);
        await filamentService.HydrateDetailImageUrlsAsync(dto, HttpContext.RequestAborted);
        return dto;
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
        var filament = await filamentService.GetFilamentById(id);

        if (filament == null)
        {
            return NotFound();
        }


        var currentUserId = User.GetUserId();

        if (currentUserId != filament.CreatedById)
        {
            return Forbid();
        }

        // Hydration runs here rather than in the service because GetFilamentById returns the
        // entity and the controller owns the map. Signing is never a member mapping.
        var dto = mapper.Map<FilamentDetailDto>(filament);
        await filamentService.HydrateDetailImageUrlsAsync(dto, HttpContext.RequestAborted);
        return dto;
    }

    /// <summary>
    /// Replace a filament's details.
    /// </summary>
    /// <remarks>
    /// Replaces the whole filament rather than patching it: read it with `GET /api/Filaments/{id}`,
    /// change the fields you need, and send the whole object back.
    /// </remarks>
    /// <param name="id">The GUID of the filament to update.</param>
    /// <param name="filamentDto">The updated filament details.</param>
    /// <returns>The updated filament.</returns>
    /// <response code="201">The updated filament information.</response>
    /// <response code="400">Returned if the id does not match the id of the filament details provided.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    /// <response code="403">Returned if the current user tries to update a filament which is not theirs.</response>
    /// <response code="404">Returned if a filament with the specified ID is not found.</response>
    /// <response code="409">A tag UID is already on another of the user's spools. The body names it.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PutFilament(Guid id, FilamentDetailDto filamentDto)
    {
        if (id != filamentDto.Id)
        {
            return BadRequest("ID in route does not match body.");
        }

        var existingFilament = await filamentService.GetFilamentById(id);

        if (existingFilament == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (!await filamentService.CanUserAccessFilament(userId.Value, id))
        {
            return Forbid();
        }

        try
        {
            var updatedPrint = await filamentService.UpdateFilament(id, filamentDto, userId.Value);

            var updatedDto = mapper.Map<FilamentDetailDto>(updatedPrint);
            await filamentService.HydrateDetailImageUrlsAsync(updatedDto, HttpContext.RequestAborted);

            return CreatedAtAction("GetFilament", new { id = existingFilament.Id }, updatedDto);
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
        catch (BadRequestException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TagUidConflictException ex)
        {
            return TagConflict(ex);
        }
    }

    /// <summary>
    ///     Create a new Filament for the current user.
    /// </summary>
    /// <remarks>
    /// `source` says how the starting amount was measured: `1` Weight, `2` Length or `3` Volume. The
    /// other two are derived from it where the density and diameter allow. Weights are in milligrams.
    /// </remarks>
    /// <param name="filamentDto">The dto containing all of the details for the filament to create.</param>
    /// <returns>The filament detail DTO that was created.</returns>
    /// <response code="201">The filament detail DTO that was created.</response>
    /// <response code="400">A tag UID is not hex bytes, or there are too many.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    /// <response code="409">A tag UID is already on another of the user's spools. The body names it.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FilamentDetailDto>> PostFilament(AddFilamentDto filamentDto)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        Filament newFilament;
        try
        {
            newFilament = await filamentService.AddFilament(filamentDto, userId.Value);
        }
        catch (BadRequestException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TagUidConflictException ex)
        {
            return TagConflict(ex);
        }


        return CreatedAtAction("GetFilament", new { id = newFilament.Id }, mapper.Map<Filament, FilamentDetailDto>(newFilament));
    }

    /// <summary>
    /// Permantently delete a filament, if the filament has not been used in any existing prints.
    /// </summary>
    /// <param name="id">The ID of the filament to delete.</param>
    /// <response code="204">Returned if the filament was deleted successfully.</response>
    /// <response code="400">Returned if the filament is unable to be deleted since it has been used in a print.</response>
    /// <response code="403">Returned if the current user cannot access the filament.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteFilament(Guid id)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }


        if (!await filamentService.CanUserAccessFilament(userId.Value, id))
        {
            return Forbid();
        }

        try
        {
            await filamentService.DeleteFilament(id);
        }
        catch (FilamentIsInUseException)
        {
            return BadRequest("This Filament is used in a Print and cannot be deleted. Try editing the Filament and marking it as Inactive instead.");
        }

        return NoContent();
    }

    /// <summary>
    /// Correct how much of a spool remains by applying a signed delta.
    /// </summary>
    /// <remarks>
    /// Wraps the same logic as the MCP `adjust_material_remaining` tool. The result cannot go below
    /// zero or above the spool's original capacity, and a spool with no starting amount cannot be
    /// adjusted. `delta` must be finite, non-zero and at most 2,000,000 in magnitude (g / mm / ml).
    /// `beforeGrams` and `afterGrams` are grams whatever unit `delta` is in.
    /// </remarks>
    /// <param name="id">The ID of the filament.</param>
    /// <param name="dto">The unit, signed delta and optional note.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The remaining weight before and after, in grams.</response>
    /// <response code="400">Unknown source, zero or non-finite delta, a delta over 2,000,000 in magnitude, a length or volume delta on a spool without the density or diameter needed to convert it, untracked spool, or an out-of-range result.</response>
    /// <response code="404">The spool does not exist or belongs to someone else. Carries a problem-details body.</response>
    [HttpPost("{id:guid}/adjustments")]
    [ProducesResponseType<AdjustFilamentRemainingResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AdjustRemaining(Guid id, [FromBody] AdjustFilamentRemainingDto dto, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        McpMeasurementSource source;
        switch (dto.Source?.Trim().ToLowerInvariant())
        {
            case "weight": source = McpMeasurementSource.Weight; break;
            case "length": source = McpMeasurementSource.Length; break;
            case "volume": source = McpMeasurementSource.Volume; break;
            default:
                return CodedProblem(StatusCodes.Status400BadRequest, "source must be one of: weight, length, volume.", "invalid_adjustment");
        }

        if (!double.IsFinite(dto.Delta) || dto.Delta == 0)
        {
            return CodedProblem(StatusCodes.Status400BadRequest, "delta must be a finite, non-zero number.", "invalid_adjustment");
        }

        if (dto.Notes is { Length: > 1000 })
        {
            return CodedProblem(StatusCodes.Status400BadRequest, "notes must be at most 1000 characters.", "invalid_adjustment");
        }

        try
        {
            var result = await filamentService.AdjustMaterialRemainingForMcp(userId.Value, id, source, dto.Delta, dto.Notes, ct);
            return Ok(new AdjustFilamentRemainingResultDto { BeforeGrams = result.BeforeGrams, AfterGrams = result.AfterGrams });
        }
        catch (McpToolException ex) when (ex.Code is "not_found" or "invalid_arguments")
        {
            return ToProblem(ex);
        }
    }

    /// <summary>
    /// Activate or retire a spool.
    /// </summary>
    /// <param name="id">The ID of the filament.</param>
    /// <param name="dto">The new active flag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The flag was set.</response>
    /// <response code="400">isActive is missing. Problem details with code invalid_request.</response>
    /// <response code="404">The spool does not exist or belongs to someone else. Carries a problem-details body.</response>
    [HttpPut("{id:guid}/active")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] SetFilamentActiveDto dto, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (dto.IsActive is null)
        {
            return CodedProblem(StatusCodes.Status400BadRequest, "isActive is required.", "invalid_request");
        }

        try
        {
            await filamentService.SetMaterialActiveForMcp(userId.Value, id, dto.IsActive.Value, ct);
            return NoContent();
        }
        catch (McpToolException ex) when (ex.Code is "not_found" or "invalid_arguments")
        {
            return ToProblem(ex);
        }
    }

    /// <summary>
    /// Remember a barcode on a spool, so a later label scan finds it.
    /// </summary>
    /// <remarks>
    /// The value is normalized (trimmed, uppercased, a 12-digit UPC-A padded to EAN-13).
    /// Remembering a barcode the spool already has returns it unchanged. A spool keeps at most
    /// four; a fifth drops the oldest. On a spool created from a catalog entry, this also counts
    /// as the user's vote that the barcode is that entry.
    /// </remarks>
    /// <param name="id">The ID of the filament.</param>
    /// <param name="dto">The barcode value and the symbology the scanner reported.</param>
    /// <response code="200">The stored barcode.</response>
    /// <response code="400">The value is not a barcode. Problem details with code invalid_barcode.</response>
    /// <response code="404">The spool does not exist or belongs to someone else.</response>
    [HttpPost("{id:guid}/barcodes")]
    [ProducesResponseType<FilamentBarcodeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddBarcode(Guid id, [FromBody] BarcodeInputDto dto)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var barcode = await filamentService.AddBarcode(id, userId.Value, dto);
            return barcode is null ? NotFound() : Ok(barcode);
        }
        catch (BadRequestException ex)
        {
            return CodedProblem(StatusCodes.Status400BadRequest, ex.Message, "invalid_barcode");
        }
    }

    /// <summary>
    /// Remove a barcode from a spool.
    /// </summary>
    /// <param name="id">The ID of the filament.</param>
    /// <param name="barcodeId">The barcode's ID, from the spool's `barcodes`.</param>
    /// <response code="204">The barcode was removed.</response>
    /// <response code="404">The spool or barcode does not exist or belongs to someone else.</response>
    [HttpDelete("{id:guid}/barcodes/{barcodeId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveBarcode(Guid id, long barcodeId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        return await filamentService.RemoveBarcode(id, barcodeId, userId.Value) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Maps the MCP service's outcomes to problem details with a machine-readable `code`. A
    /// missing and a foreign spool are both 404 `material_not_found`: the Home Assistant client
    /// tells that apart from an unmatched route (also a 404 problem body) by the code.
    /// Callers catch only the `not_found` and `invalid_arguments` codes, so any other one surfaces
    /// as a server error instead of a misleading 400.
    /// </summary>
    private ObjectResult ToProblem(McpToolException ex)
    {
        if (ex.Code == "not_found")
        {
            return CodedProblem(StatusCodes.Status404NotFound, ex.Message, "material_not_found");
        }

        var code = ex.SubCode is "material_untracked" or "adjustment_out_of_range" ? ex.SubCode : "invalid_adjustment";
        return CodedProblem(StatusCodes.Status400BadRequest, ex.Message, code);
    }

    private ObjectResult CodedProblem(int status, string detail, string code)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Returns a DTO which includes a list of all filament storage locations for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Storage Locations on success.</response>
    [HttpGet("storage-locations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentStorageLocationDto>> GetFilamentStorageLocations()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var locations = await filamentService.GetFilamentStorageLocations(currentUserId.Value);

        return new FilamentStorageLocationDto { StorageLocations = locations };
    }

    /// <summary>
    /// Returns a DTO which includes a list of all filament purchase locations for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Purchase Locations on success.</response>
    [HttpGet("purchase-locations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentPurchaseLocationsDto>> GetFilamentPurchaseLocations()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var locations = await filamentService.GetFilamentPurchaseLocations(currentUserId.Value);

        return new FilamentPurchaseLocationsDto { PurchaseLocations = locations };
    }


    /// <summary>
    /// Returns a DTO which includes a list of all filament brands for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Brands on success.</response>
    [HttpGet("brands")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentBrandsDto>> GetFilamentBrands()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var brands = await filamentService.GetFilamentBrands(currentUserId.Value);

        return new FilamentBrandsDto { Brands = brands };
    }

    /// <summary>
    /// Upload an image to a filament.
    /// </summary>
    /// <remarks>
    /// The uploaded bytes are decoded server-side; the client's declared content type is
    /// not trusted. An undecodable or disallowed file is rejected before anything is stored.
    /// </remarks>
    /// <response code="201">The stored image, with signed URLs.</response>
    /// <response code="400">The file is missing, too large, or not a supported image.</response>
    /// <response code="404">No such filament belonging to the current user.</response>
    [HttpPost("{id}/images")]
    // Enforced BEFORE model binding, unlike the file.Length check in the body: by the time
    // IFormFile has materialized, a much larger multipart body has already been buffered.
    // This lowers Kestrel's 30MB default to roughly the real limit for this one endpoint;
    // the allowance over MaxImageSizeBytes covers multipart framing overhead.
    //
    // Not covered by an integration test on purpose: the limit is a Kestrel feature and
    // TestServer does not implement IHttpMaxRequestBodySizeFeature, so an oversized body
    // there falls through to the file.Length check and reports 400 instead of 413. A test
    // asserting 413 would be asserting something the harness cannot produce.
    [RequestSizeLimit(MaxImageSizeBytes + (1024 * 1024))]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FilamentImageDto>> PostFilamentImage(Guid id, IFormFile file)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        if (file is null || file.Length == 0) return BadRequest("Image file is required.");

        if (file.Length > MaxImageSizeBytes) return BadRequest("Image must be under 10MB.");

        try
        {
            await using var stream = file.OpenReadStream();
            var image = await filamentImageService.AddImageAsync(
                id, stream, userId.Value, HttpContext.RequestAborted);

            var dto = await filamentService.HydrateImageDtoAsync(image, HttpContext.RequestAborted);

            telemetry.TrackEvent("FilamentPictureAdded");

            return CreatedAtAction(nameof(GetFilamentImage), new { id, imageId = image.Id }, dto);
        }
        catch (InvalidImageException ex) { return BadRequest(ex.Message); }
        // The account storage quota throws this. There is no global exception-to-status
        // mapping in this app, so without the catch it surfaces as a 500.
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Redirects to a signed URL for a filament image.
    /// </summary>
    /// <remarks>
    /// For non-browser API consumers (MCP server, scripts) holding a bearer token.
    /// The UI does not use this - it reads pre-signed URLs from the filament DTO.
    /// NOT usable from &lt;img src&gt;: the redirect itself requires the bearer token.
    /// </remarks>
    [HttpGet("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        // Ownership is part of the predicate, so a foreign image is indistinguishable
        // from a missing one. The endpoint must not be an existence oracle.
        var image = await context.FilamentImages
            .AsNoTracking()
            .Include(fi => fi.File)
            .FirstOrDefaultAsync(fi => fi.FilamentId == id
                                    && fi.Id == imageId
                                    && fi.Filament.CreatedById == userId.Value,
                                 HttpContext.RequestAborted);
        if (image?.File?.Path is null) return NotFound();

        var uri = await blobStorageService.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, Path.GetFileName(image.File.Path),
            image.ContentType, TimeSpan.FromHours(6), TimeSpan.FromHours(5));

        return Redirect(uri.ToString());
    }

    /// <summary>
    /// Deletes a filament image, its file rows, and its blobs.
    /// </summary>
    [HttpDelete("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.DeleteImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Reorders a filament's images. The supplied IDs must be the filament's exact image set.
    /// </summary>
    [HttpPut("{id}/images/reorder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderFilamentImages(Guid id, [FromBody] List<int> orderedImageIds)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.ReorderImagesAsync(
                id, orderedImageIds, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Makes one of a filament's images its default.
    /// </summary>
    [HttpPost("{id}/images/{imageId}/set-as-default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.SetDefaultImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// The 409 for a tag already on another spool (#147). ProblemDetails like the rest of the
    /// API's errors, with the holding spool as extension members so a client can name and link it.
    /// </summary>
    private ObjectResult TagConflict(TagUidConflictException ex)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: StatusCodes.Status409Conflict,
            title: "Tag already assigned",
            detail: ex.Message);
        problem.Extensions["tagUid"] = ex.TagUid;
        problem.Extensions["filamentId"] = ex.FilamentId;
        problem.Extensions["filamentDisplayName"] = ex.FilamentDisplayName;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict,
            ContentTypes = { "application/problem+json" },
        };
    }
}
